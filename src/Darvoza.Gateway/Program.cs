// Darvoza — Claude + Azure DevOps governance gateway (.NET)
// =========================================================
// A01-T1 SPIKE (timebox 2-3h): prove SDK-to-SDK passthrough ONLY.
// Goal: a Claude client connects to THIS gateway over streamable-HTTP, can
// list-tools and call-tool, and every call is transparently forwarded to the
// OFFICIAL Azure DevOps MCP server (stdio) and the result returned.
// NO policy, NO auth, NO audit yet -- those are A01-T3 / T4. Prove the pipe first.
//
// ⚠️ Skeleton: handler signatures + result shapes must be confirmed against the
// installed SDK 1.4.0 API (it moves between minors). The STRUCTURE below is the
// intended design; treat the exact type/method names as the spike's first task.

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// --- Upstream leg: MCP client to the official Azure DevOps MCP server (stdio) ---
// REQ-001 chose local/stdio for v1 (simplest, no Entra OAuth dance). Note: MS shipped
// a REMOTE (Entra-backed, streamable-HTTP) variant in public preview (2026-03-17) and
// plans to retire the local server at GA -- address this in the writeup; pin the npm version.
var adoOrg = Environment.GetEnvironmentVariable("ADO_ORG")
    ?? throw new InvalidOperationException("Set ADO_ORG to your Azure DevOps organization name.");

var upstreamTransport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "azure-devops-upstream",
    Command = "npx",
    // Confirm exact package + flags from microsoft/azure-devops-mcp README; PIN the version.
    Arguments = ["-y", "@azure-devops/mcp", adoOrg],
    // ADO auth: least-privilege PAT via env (AZURE_DEVOPS_EXT_PAT) or `az login`. Never in repo.
});

var upstream = await McpClientFactory.CreateAsync(upstreamTransport);

// --- Front leg: streamable-HTTP MCP server that proxies to the upstream ---
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
        var name = ctx.Params?.Name ?? throw new InvalidOperationException("Missing tool name.");
        return await upstream.CallToolAsync(
            name,
            ctx.Params?.Arguments,
            cancellationToken: ct);
    });

var app = builder.Build();
app.MapMcp();   // maps the streamable-HTTP MCP endpoint (default: /)
app.Run();
