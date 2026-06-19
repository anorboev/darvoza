// Darvoza — MCP governance gateway for Azure DevOps (.NET), provider-agnostic (demo led by Claude).
// =================================================================================================
// COMPOSITION ROOT (A01-T2 skeleton + A01-T3 policy + A01-T4 audit): config, DI registration, lifecycle,
// and MCP server wiring. As of A01-T3 the gateway ENFORCES per-role policy (deny-by-default) via an
// IUpstreamToolClient decorator at the seam — it is no longer a transparent pass-through. A01-T4 adds the
// outermost decorator: a 100%-coverage JSONL audit trail. Enforcement and audit live in the decorators,
// not here.
//
// Shape (see the typed components for detail):
//   - Configuration/  GatewayOptions (ADO_ORG validation, T2c; policy path, T3; audit path, T4) +
//                      DotEnvLoader (bounded .env, T2a) + Policy / PolicyDocument / PolicyLoader
//                      (declarative policy.yaml, T3)
//   - Upstream/        IUpstreamToolClient (seam) + McpUpstreamToolClient (owns/disposes the upstream
//                      session, T2b) + UpstreamConnectionInitializer (fail-fast connect at startup)
//                      + PolicyEnforcingToolClient (deny-by-default decorator, T3) + ICallerKeyProvider
//                      / HttpHeaderCallerKeyProvider (X-Darvoza-Key, T3) + PassthroughToolHandlers
//                      + AuditingToolClient (outermost audit decorator, T4) + ICallDecisionContext
//   - Audit/           AuditRecord (JSONL schema + redacted arg summary) + IAuditSink / JsonlAuditSink
//                      (append-only writer) + CallerFingerprint (non-reversible caller id, T4)
//
// Resolved SDK facts (Decision #1/#2/#3, verified against MCP C# SDK 1.4.0):
//   - Upstream client created with McpClient.CreateAsync(transport) (McpClientFactory removed in 1.4.0).
//   - Server handlers: WithListToolsHandler / WithCallToolHandler; the request ctx exposes ctx.Services
//     so handlers resolve the DI-registered IUpstreamToolClient (the outermost decorator) at call time.
//   - app.MapMcp() maps the Streamable HTTP endpoint at the ROOT path "/" (Decision #2 — unchanged).
//   - Upstream pinned @azure-devops/mcp@2.7.0 local stdio, PAT = base64("email:pat") (Decision #3).

using Darvoza.Gateway.Audit;
using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Client;

// --- Configuration: bounded .env -> environment, then validated options ---------------------------
// T2a (G-07): load the nearest project-root .env WITHOUT walking past the repo/solution root.
DotEnvLoader.Load(Directory.GetCurrentDirectory());

// T2c: validate ADO_ORG shape fail-fast. ADO_ORG becomes a positional arg to `npx … <org> …`; on
// Windows npx resolves to npx.cmd (shell), where .NET arg-escaping for batch files has known gaps.
// This strict allowlist (no quotes/spaces/metacharacters) is therefore the LOAD-BEARING mitigation
// for that argument-injection surface — not the OS argument escaping. Keep it strict.
var adoOrg = Environment.GetEnvironmentVariable(GatewayOptions.AdoOrgEnvVar);
if (!GatewayOptions.IsValidAdoOrg(adoOrg))
{
    throw new InvalidOperationException(
        $"{GatewayOptions.AdoOrgEnvVar} is missing or malformed. Set it to your Azure DevOps " +
        "organization name (alphanumeric and interior hyphens only, e.g. \"darvoza-demo\").");
}

// T3: load + validate the policy BEFORE building the host. Any failure (missing, unparseable, or
// half-configured policy) throws here, so the host NEVER STARTS open — deny-by-default requires an
// explicit policy.yaml (mirrors the fail-fast upstream lifecycle, ADR-0002 / decision #6).
var policy = PolicyLoader.Load(GatewayOptions.ResolvePolicyPath(Directory.GetCurrentDirectory()));

// --- Upstream transport (PAT resolved once here; never stored in an app-lifetime object or logged) -
// T2c: the @azure-devops/mcp@2.7.0 "pat" mode reads env PERSONAL_ACCESS_TOKEN = base64("email:pat").
// Accept a pre-encoded PERSONAL_ACCESS_TOKEN, or a raw PAT in AZURE_DEVOPS_EXT_PAT we encode in-process.
// The token is held only inside the child transport's environment (required to launch it).
var upstreamTransport = BuildUpstreamTransport(adoOrg!);

var builder = WebApplication.CreateBuilder(args);

// --- DI: upstream owned by the container so it is disposed on shutdown (T2b — kills the npx child) -
builder.Services.AddSingleton(new GatewayOptions { AdoOrg = adoOrg! });
builder.Services.AddSingleton(_ => new McpUpstreamToolClient(upstreamTransport));

// T3 policy seam. The caller key arrives as the X-Darvoza-Key header; IHttpContextAccessor (a singleton
// over AsyncLocal) carries it across the per-request scope boundary so the singleton decorator can read
// it without a captive scoped dependency.
builder.Services.AddSingleton(policy);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICallerKeyProvider, HttpHeaderCallerKeyProvider>();

// T4 audit seam. The decision context (AsyncLocal-backed, singleton-safe) carries each call's policy
// decision from the inner T3 decorator up to the outer audit decorator. TimeProvider gives testable UTC
// timestamps + latency. The JSONL sink is a container-owned singleton (IAsyncDisposable) so the file is
// flushed/closed on shutdown — its path is env-configurable (DARVOZA_AUDIT_PATH), defaulting to the
// gitignored audit/ dir (ADR-0003 / decision #6 lifecycle posture).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICallDecisionContext, AsyncLocalCallDecisionContext>();
builder.Services.AddSingleton<IAuditSink>(_ =>
    new JsonlAuditSink(GatewayOptions.ResolveAuditPath(Directory.GetCurrentDirectory())));

// The decorator chain (ADR-0002): audit (T4, OUTERMOST) wraps policy (T3) wraps the concrete client.
// Both decorators are singletons to sit cleanly under the singleton PassthroughToolHandlers (G-09 #1 —
// no captive scoped dependency). KEEP the concrete McpUpstreamToolClient registration above:
// UpstreamConnectionInitializer resolves the concrete type for its connect lifecycle, while handlers
// resolve the (now audit-then-policy-decorated) interface (G-09 #2).
builder.Services.AddSingleton<IUpstreamToolClient>(sp =>
    new AuditingToolClient(
        new PolicyEnforcingToolClient(
            sp.GetRequiredService<McpUpstreamToolClient>(),
            sp.GetRequiredService<Policy>(),
            sp.GetRequiredService<ICallerKeyProvider>(),
            sp.GetRequiredService<ICallDecisionContext>()),
        sp.GetRequiredService<IAuditSink>(),
        sp.GetRequiredService<ICallDecisionContext>(),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<PassthroughToolHandlers>();
builder.Services.AddHostedService<UpstreamConnectionInitializer>();

// --- Front leg: streamable-HTTP MCP server proxying to the upstream via the pass-through stage -----
// The handlers resolve IUpstreamToolClient through ctx.Services (the seam), so the T3 policy decorator
// (and a future T4 audit decorator) take effect with no handler change. Enforcement lives in that
// decorator, not here — these handlers stay a thin pass-through over the resolved outermost interface.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithListToolsHandler(async (ctx, ct) =>
        await Handlers(ctx.Services).ListToolsAsync(ct))
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var callParams = ctx.Params
            ?? throw new InvalidOperationException("Missing call-tool parameters.");
        return await Handlers(ctx.Services).CallToolAsync(callParams, ct);
    });

var app = builder.Build();
app.MapMcp();   // streamable-HTTP MCP endpoint at "/"
app.Run();

// -------------------------------------------------------------------------------------------------
// Resolve the pass-through stage from the request's service provider (a fresh per-request scope, since
// McpServerOptions.ScopeRequests defaults true). Guard the nullable Services with a clear message.
static PassthroughToolHandlers Handlers(IServiceProvider? services) =>
    (services ?? throw new InvalidOperationException(
        "MCP request context has no service provider — the server is not DI-integrated."))
        .GetRequiredService<PassthroughToolHandlers>();

static StdioClientTransport BuildUpstreamTransport(string adoOrg)
{
    var token = Environment.GetEnvironmentVariable("PERSONAL_ACCESS_TOKEN");
    if (string.IsNullOrEmpty(token))
    {
        var rawPat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT")
            ?? throw new InvalidOperationException(
                "Set PERSONAL_ACCESS_TOKEN (base64 of \"email:pat\") or AZURE_DEVOPS_EXT_PAT (raw PAT).");
        // Basic-auth shape is "<username>:<pat>"; Azure DevOps ignores the username, so the literal
        // "darvoza" is an arbitrary, fixed placeholder (Decision #3) — not an operator identity.
        token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"darvoza:{rawPat}"));
    }

    return new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "azure-devops-upstream",
        Command = "npx",
        // @azure-devops/mcp@2.7.0: org is positional; PAT auth via PERSONAL_ACCESS_TOKEN (base64 email:pat).
        Arguments = ["-y", "@azure-devops/mcp", adoOrg, "--authentication", "pat"],
        EnvironmentVariables = new Dictionary<string, string?> { ["PERSONAL_ACCESS_TOKEN"] = token },
    });
}

// Test affordance only (A01-T5 / closes A01-T6d): make the implicit Program entry type public + partial so
// the e2e test project can drive the REAL composition root via WebApplicationFactory<Program> (the live
// X-Darvoza-Key -> HttpHeaderCallerKeyProvider -> policy -> audit pipeline). No runtime behavior change.
public partial class Program;
