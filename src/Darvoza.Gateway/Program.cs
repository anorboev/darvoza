// Darvoza — MCP governance gateway for Azure DevOps (.NET), provider-agnostic (demo led by Claude).
// =================================================================================================
// A01-T2 GATEWAY SKELETON: the structured, maintainable foundation that A01-T3 (policy) and A01-T4
// (audit) bolt onto. This file is the COMPOSITION ROOT only — config, DI registration, lifecycle,
// and the MCP server wiring. The behavior is still TRANSPARENT PASS-THROUGH (no policy, no audit, no
// deny-by-default); those slot in as IUpstreamToolClient decorators at the seam (see that interface).
//
// Shape (see the typed components for detail):
//   - Configuration/  GatewayOptions (ADO_ORG validation, T2c) + DotEnvLoader (bounded .env, T2a)
//   - Upstream/        IUpstreamToolClient (seam) + McpUpstreamToolClient (owns/disposes the upstream
//                      session, T2b) + UpstreamConnectionInitializer (fail-fast connect at startup)
//                      + PassthroughToolHandlers (no-op pass-through stage)
//
// Resolved SDK facts (Decision #1/#2/#3, verified against MCP C# SDK 1.4.0):
//   - Upstream client created with McpClient.CreateAsync(transport) (McpClientFactory removed in 1.4.0).
//   - Server handlers: WithListToolsHandler / WithCallToolHandler; the request ctx exposes ctx.Services
//     so handlers resolve the DI-registered IUpstreamToolClient (the outermost decorator) at call time.
//   - app.MapMcp() maps the Streamable HTTP endpoint at the ROOT path "/" (Decision #2 — unchanged).
//   - Upstream pinned @azure-devops/mcp@2.7.0 local stdio, PAT = base64("email:pat") (Decision #3).

using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Client;

// --- Configuration: bounded .env -> environment, then validated options ---------------------------
// T2a (G-07): load the nearest project-root .env WITHOUT walking past the repo/solution root.
DotEnvLoader.Load(Directory.GetCurrentDirectory());

// T2c: validate ADO_ORG shape fail-fast — it becomes a positional arg to the upstream npx launch.
var adoOrg = Environment.GetEnvironmentVariable(GatewayOptions.AdoOrgEnvVar);
if (!GatewayOptions.IsValidAdoOrg(adoOrg))
{
    throw new InvalidOperationException(
        $"{GatewayOptions.AdoOrgEnvVar} is missing or malformed. Set it to your Azure DevOps " +
        "organization name (alphanumeric and interior hyphens only, e.g. \"darvoza-demo\").");
}

// --- Upstream transport (PAT resolved once here; never stored in an app-lifetime object or logged) -
// T2c: the @azure-devops/mcp@2.7.0 "pat" mode reads env PERSONAL_ACCESS_TOKEN = base64("email:pat").
// Accept a pre-encoded PERSONAL_ACCESS_TOKEN, or a raw PAT in AZURE_DEVOPS_EXT_PAT we encode in-process.
// The token is held only inside the child transport's environment (required to launch it).
var upstreamTransport = BuildUpstreamTransport(adoOrg!);

var builder = WebApplication.CreateBuilder(args);

// --- DI: upstream owned by the container so it is disposed on shutdown (T2b — kills the npx child) -
builder.Services.AddSingleton(new GatewayOptions { AdoOrg = adoOrg! });
builder.Services.AddSingleton(_ => new McpUpstreamToolClient(upstreamTransport));
builder.Services.AddSingleton<IUpstreamToolClient>(sp => sp.GetRequiredService<McpUpstreamToolClient>());
builder.Services.AddSingleton<PassthroughToolHandlers>();
builder.Services.AddHostedService<UpstreamConnectionInitializer>();

// --- Front leg: streamable-HTTP MCP server proxying to the upstream via the pass-through stage -----
// The handlers resolve IUpstreamToolClient through ctx.Services (the seam), so A01-T3/T4 decorators
// take effect with no handler change. No policy / no audit here (T2 is pass-through only).
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithListToolsHandler(async (ctx, ct) =>
        await ctx.Services!.GetRequiredService<PassthroughToolHandlers>().ListToolsAsync(ct))
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var callParams = ctx.Params
            ?? throw new InvalidOperationException("Missing call-tool parameters.");
        return await ctx.Services!.GetRequiredService<PassthroughToolHandlers>()
            .CallToolAsync(callParams, ct);
    });

var app = builder.Build();
app.MapMcp();   // streamable-HTTP MCP endpoint at "/"
app.Run();

// -------------------------------------------------------------------------------------------------
static StdioClientTransport BuildUpstreamTransport(string adoOrg)
{
    var token = Environment.GetEnvironmentVariable("PERSONAL_ACCESS_TOKEN");
    if (string.IsNullOrEmpty(token))
    {
        var rawPat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT")
            ?? throw new InvalidOperationException(
                "Set PERSONAL_ACCESS_TOKEN (base64 of \"email:pat\") or AZURE_DEVOPS_EXT_PAT (raw PAT).");
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
