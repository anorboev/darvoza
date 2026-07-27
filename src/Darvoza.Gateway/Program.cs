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
using System.Text.Json;

// --- Configuration: bounded .env -> environment, then validated options ---------------------------
// T2a (G-07): load the nearest project-root .env WITHOUT walking past the repo/solution root.
DotEnvLoader.Load(Directory.GetCurrentDirectory());

// T3: load + validate the policy BEFORE building the host. Any failure (missing, unparseable, or
// half-configured policy) throws here, so the host NEVER STARTS open — deny-by-default requires an
// explicit policy.yaml (mirrors the fail-fast upstream lifecycle, ADR-0002 / decision #6). Since T7 the
// same file also selects WHICH MCP server to govern (ADR-0004) — the config file is the only source.
var policy = PolicyLoader.Load(GatewayOptions.ResolvePolicyPath(Directory.GetCurrentDirectory()));

// T2c + T6a + T7: validate ADO_ORG shape fail-fast — but ONLY for the built-in azure-devops profile,
// since an ADO org is meaningless for a server that is not Azure DevOps. ADO_ORG becomes a positional
// arg to that profile's launch. Since A01-T6a the Windows launch goes through `node npx-cli.js`
// (UpstreamLaunch), so no batch file (npx.cmd) ever re-parses our argv — that closed the G-10
// arg-injection surface. This strict allowlist (no quotes/spaces/metacharacters) stays as
// defense-in-depth. Keep it strict.
string? adoOrg = null;
if (policy.Upstream is { IsCustom: false, Profile: UpstreamOptions.AzureDevOpsProfile })
{
    adoOrg = Environment.GetEnvironmentVariable(GatewayOptions.AdoOrgEnvVar);
    if (!GatewayOptions.IsValidAdoOrg(adoOrg))
    {
        throw new InvalidOperationException(
            $"{GatewayOptions.AdoOrgEnvVar} is missing or malformed. Set it to your Azure DevOps " +
            "organization name (alphanumeric and interior hyphens only, e.g. \"darvoza-demo\"). " +
            "Governing a different MCP server instead? Configure 'upstream' in the policy file.");
    }
}

// --- Upstream transport (PAT resolved once here; never stored in an app-lifetime object or logged) -
// T7: the launch spec comes from the config-file selection — the built-in azure-devops profile by
// default, or the operator's own command + args. Resolved here, BEFORE the host is built, which is also
// why no HTTP request can influence it: none can exist yet (ADR-0004).
var launch = UpstreamLaunch.Resolve(
    policy.Upstream, adoOrg, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists);
var upstreamTransport = BuildUpstreamTransport(policy.Upstream, launch);

var builder = WebApplication.CreateBuilder(args);

// --- DI: upstream owned by the container so it is disposed on shutdown (T2b — kills the npx child) -
builder.Services.AddSingleton(new GatewayOptions { AdoOrg = adoOrg });
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
// T6e (G-17 #1): audit fingerprints are HMAC-SHA256 under a per-deployment salt —
// DARVOZA_FINGERPRINT_SALT when configured (stable across restarts), else generated fresh at startup
// (fingerprints then correlate within a run only). The salt is never logged or written to the trail.
builder.Services.AddSingleton(new CallerFingerprint(GatewayOptions.ResolveFingerprintSalt()));
var auditPath = GatewayOptions.ResolveAuditPath(Directory.GetCurrentDirectory());
builder.Services.AddSingleton<IAuditSink>(_ => new JsonlAuditSink(auditPath));

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
            sp.GetRequiredService<ICallDecisionContext>(),
            sp.GetRequiredService<CallerFingerprint>()),
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

// T6b (G-10 #2): X-Darvoza-Key is app-layer AUTHORIZATION, not transport authentication. The v1
// deployment assumption is loopback/trusted-network; if the operator binds wider, say so loudly.
// (Also remember G-21: unauthenticated tools/list probing leaves no audit trail in v1.)
app.Lifetime.ApplicationStarted.Register(() =>
{
    // T6f (@security-reviewer MEDIUM-1): force the audit sink into existence NOW. Its ctor creates
    // the audit directory (owner-only on Unix), so the permission check below inspects the REAL
    // directory even on a fresh deployment — and an unusable audit path gets LOGGED at startup
    // instead of being discovered on the first tool call. (ApplicationStarted callbacks are
    // best-effort, not process-aborting; the hard guarantee stays the fail-closed write path in
    // AuditingToolClient.)
    _ = app.Services.GetRequiredService<IAuditSink>();

    // T7: state the fully resolved upstream launch ONCE, so "which server is this gateway actually in
    // front of?" is answerable from the log rather than by reading config. Argv is rendered as a JSON
    // ARRAY, not a command line — the element boundaries are the point, and a space-joined rendering
    // would misrepresent an argv that is never joined. (Credentials must live in the environment, not
    // in 'upstream.args' — see UpstreamOptions and README.)
    app.Logger.LogInformation(
        "Upstream MCP server: {UpstreamCommand} with argv {UpstreamArguments}",
        launch.Command, JsonSerializer.Serialize(launch.Arguments));

    if (OperatingSystem.IsWindows() && UpstreamOptions.IsWindowsBatchCommand(launch.Command))
    {
        app.Logger.LogWarning(
            "Configured upstream command {UpstreamCommand} is a Windows batch file, which a process " +
            "launcher runs through cmd.exe — .NET's argument escaping for batch files has known gaps. " +
            "Darvoza passes argv as an array and no untrusted input reaches it (both the command and " +
            "its arguments come from your config file), but prefer the real executable where you can. " +
            "See docs/adr/ADR-0004.", launch.Command);
    }

    foreach (var url in app.Urls.Where(url => !GatewayOptions.IsLoopbackUrl(url)))
    {
        app.Logger.LogWarning(
            "Darvoza is listening on non-loopback address {Url}. The X-Darvoza-Key header is " +
            "authorization, NOT transport authentication — on an untrusted network, front the " +
            "gateway with TLS and network-level authentication (see README, Security model).", url);
    }

    // T6f (G-17 #2): the trail carries roles/fingerprints/tool names — warn if the audit directory is
    // readable beyond its owner. Unix only: Windows ACLs have no equally cheap+reliable check, so the
    // Windows guidance is the icacls recipe in README/RUNBOOK (documented decision, A01-T6f).
    if (!OperatingSystem.IsWindows())
    {
        var auditDir = Path.GetDirectoryName(Path.GetFullPath(auditPath));
        if (auditDir is not null && Directory.Exists(auditDir)
            && GatewayOptions.IsGroupOrWorldAccessible(File.GetUnixFileMode(auditDir)))
        {
            app.Logger.LogWarning(
                "Audit directory {AuditDir} is accessible to group/other users. The trail exposes " +
                "roles, key fingerprints, and tool usage — restrict it to the gateway's operator " +
                "(chmod 700 <dir>; chmod 600 <file>). See README, Security model.", auditDir);
        }
    }
});
app.Run();

// -------------------------------------------------------------------------------------------------
// Resolve the pass-through stage from the request's service provider (a fresh per-request scope, since
// McpServerOptions.ScopeRequests defaults true). Guard the nullable Services with a clear message.
static PassthroughToolHandlers Handlers(IServiceProvider? services) =>
    (services ?? throw new InvalidOperationException(
        "MCP request context has no service provider — the server is not DI-integrated."))
        .GetRequiredService<PassthroughToolHandlers>();

static StdioClientTransport BuildUpstreamTransport(UpstreamOptions options, UpstreamLaunchSpec launch)
{
    // T7: only the azure-devops profile needs Darvoza to marshal a credential. A custom upstream's
    // credentials are the operator's business and reach the child the same way they always have — by
    // INHERITANCE of the gateway's own environment (the child process inherits it). That is why
    // credentials belong in the environment and never in 'upstream.args', which is logged at startup.
    var childEnvironment = new Dictionary<string, string?>();
    if (!options.IsCustom)
    {
        // T2c: the @azure-devops/mcp "pat" mode reads PERSONAL_ACCESS_TOKEN = base64("email:pat").
        // Accept a pre-encoded PERSONAL_ACCESS_TOKEN, or a raw PAT in AZURE_DEVOPS_EXT_PAT we encode
        // in-process. The token is held only inside the child transport's environment.
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

        childEnvironment["PERSONAL_ACCESS_TOKEN"] = token;
    }

    return new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = options.IsCustom ? "configured-upstream" : "azure-devops-upstream",
        Command = launch.Command,
        // Argv stays a COLLECTION from config file to process launch — never joined here or anywhere
        // else, so no shell or batch file can re-parse it (G-10 #1 / Decision #17, ADR-0004).
        Arguments = [.. launch.Arguments],
        EnvironmentVariables = childEnvironment,
    });
}

// Test affordance only (A01-T5 / closes A01-T6d): make the implicit Program entry type public + partial so
// the e2e test project can drive the REAL composition root via WebApplicationFactory<Program> (the live
// X-Darvoza-Key -> HttpHeaderCallerKeyProvider -> policy -> audit pipeline). No runtime behavior change.
public partial class Program;
