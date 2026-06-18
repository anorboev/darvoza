// Darvoza — MCP governance gateway for Azure DevOps (.NET), provider-agnostic (demo led by Claude).
// =================================================================================================
// A01-T1 SPIKE: prove SDK-to-SDK passthrough ONLY. An MCP client connects to THIS gateway over
// streamable-HTTP, can list-tools and call-tool, and every call is transparently forwarded to the
// OFFICIAL Azure DevOps MCP server (stdio) and the result returned unmodified.
// NO policy, NO caller auth, NO audit, NO deny-by-default, NO YAML — those are A01-T2/T3/T4.
//
// RESOLVED UNKNOWNS (verified against installed packages + official docs — for T2 to build on):
//
//  1. MCP C# SDK 1.4.0 API (confirmed from the NuGet XML docs):
//       - McpClientFactory was REMOVED in 1.4.0 → create the upstream client with
//         McpClient.CreateAsync(transport) (namespace ModelContextProtocol.Client).
//       - Server handlers take the McpRequestHandler<TParams,TResult> delegate
//         (RequestContext<TParams> ctx, CancellationToken ct) => ValueTask<TResult>:
//           WithListToolsHandler(McpRequestHandler<ListToolsRequestParams, ListToolsResult>)
//           WithCallToolHandler (McpRequestHandler<CallToolRequestParams,  CallToolResult>)
//       - Upstream client: ListToolsAsync(cancellationToken:) returns IList<McpClientTool>;
//         each McpClientTool exposes .ProtocolTool (a Protocol.Tool).
//       - Cleanest call passthrough: CallToolAsync(CallToolRequestParams, ct) overload — forwards
//         the incoming params object verbatim (no Arguments dictionary conversion).
//       - StdioClientTransportOptions.InheritEnvironmentVariables defaults true → the spawned npx
//         child inherits this process's env, so PERSONAL_ACCESS_TOKEN flows through automatically.
//
//  2. Gateway endpoint path: app.MapMcp() (default pattern) maps the Streamable HTTP endpoint at
//     the ROOT path "/" (spec 2025-11-25). Clients connect to http://localhost:<port>/ .
//
//  3. Upstream launch (PINNED): npm @azure-devops/mcp@2.7.0, launched as
//       npx -y @azure-devops/mcp <ADO_ORG> --authentication pat
//     org is a POSITIONAL arg. PAT carried in env var PERSONAL_ACCESS_TOKEN, whose value is the
//     base64 encoding of "<email>:<pat>" (email = any non-empty string; only the token is used).

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// --- Spike-only .env loader (no new dependency; replaced by proper config in T2) -----------------
// Loads KEY=VALUE lines from the nearest .env (walking up from the working dir) into process env,
// without overwriting vars already set. Secrets live in .env only (gitignored) — never in the repo.
LoadDotEnv();

// --- Upstream leg: MCP client to the official Azure DevOps MCP server (stdio) --------------------
// REQ-001 chose local/stdio for v1. MS also ships a REMOTE (Entra-backed) variant and plans to
// retire local at GA — call this out in the writeup. npm version pinned in README.
var adoOrg = Environment.GetEnvironmentVariable("ADO_ORG")
    ?? throw new InvalidOperationException("Set ADO_ORG to your Azure DevOps organization name.");

// Upstream PAT handling. @azure-devops/mcp@2.7.0 with --authentication pat reads the env var
// PERSONAL_ACCESS_TOKEN whose value must be base64("<email>:<pat>"). We accept the PAT either:
//   - already as PERSONAL_ACCESS_TOKEN (pre-encoded, passed through), or
//   - as a RAW PAT in AZURE_DEVOPS_EXT_PAT (az-CLI convention) which we base64-encode here.
// The token value is only ever held in-process and handed to the child via EnvironmentVariables;
// it is never logged.
var upstreamPat = Environment.GetEnvironmentVariable("PERSONAL_ACCESS_TOKEN");
if (string.IsNullOrEmpty(upstreamPat))
{
    var rawPat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT")
        ?? throw new InvalidOperationException(
            "Set PERSONAL_ACCESS_TOKEN (base64 of \"email:pat\") or AZURE_DEVOPS_EXT_PAT (raw PAT).");
    upstreamPat = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"darvoza:{rawPat}"));
}

var upstreamTransport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "azure-devops-upstream",
    Command = "npx",
    // @azure-devops/mcp@2.7.0: org is positional; PAT auth via PERSONAL_ACCESS_TOKEN (base64 email:pat).
    Arguments = ["-y", "@azure-devops/mcp", adoOrg, "--authentication", "pat"],
    // Pass the (derived) token explicitly; child also inherits the rest of the env by default.
    EnvironmentVariables = new Dictionary<string, string?> { ["PERSONAL_ACCESS_TOKEN"] = upstreamPat },
});

// Establishes the MCP session with the upstream (initialize handshake). AC1 passes when this returns.
var upstream = await McpClient.CreateAsync(upstreamTransport);

var builder = WebApplication.CreateBuilder(args);

// --- Front leg: streamable-HTTP MCP server that proxies to the upstream --------------------------
builder.Services.AddSingleton(upstream);
builder.Services.AddMcpServer()
    .WithHttpTransport()
    // PASSTHROUGH (spike core). In A01-T3 these handlers gain the policy filter:
    //   list -> return only tools the caller's role allows
    //   call -> deny-by-default unless allowed; log either way (A01-T4)
    .WithListToolsHandler(async (ctx, ct) =>
    {
        var tools = await upstream.ListToolsAsync(cancellationToken: ct);
        return new ListToolsResult { Tools = [.. tools.Select(t => t.ProtocolTool)] };
    })
    .WithCallToolHandler(async (ctx, ct) =>
    {
        // Forward the incoming params verbatim. Errors from upstream (bad args / upstream down)
        // surface to the caller as the SDK's standard JSON-RPC error or an isError result (AC4).
        var callParams = ctx.Params
            ?? throw new InvalidOperationException("Missing call-tool parameters.");
        return await upstream.CallToolAsync(callParams, ct);
    });

var app = builder.Build();
app.MapMcp();   // streamable-HTTP MCP endpoint at "/"
app.Run();

// -------------------------------------------------------------------------------------------------
static void LoadDotEnv()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (!File.Exists(path)) continue;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, val);
        }
        return; // first .env found wins
    }
}
