namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Connects the upstream MCP client once at host startup (A01-T2b lifecycle). Running the connect in
/// <see cref="StartAsync"/> means a failure to reach the upstream <b>fails host startup</b> (fail-fast),
/// before Kestrel begins serving — preferable for a governance gateway and integrated with host
/// logging/cancellation. Disposal is the DI container's job (the singleton wrapper is container-owned).
/// </summary>
public sealed class UpstreamConnectionInitializer(
    McpUpstreamToolClient upstream,
    ILogger<UpstreamConnectionInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Connecting to upstream Azure DevOps MCP server…");
        await upstream.ConnectAsync(cancellationToken);
        logger.LogDebug("Upstream MCP session established.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
