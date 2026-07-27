using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Connects the upstream MCP client once at host startup (A01-T2b lifecycle), then reports any policy
/// allow-list entry that upstream does not offer (A01-T7). Running the connect in
/// <see cref="StartAsync"/> means a failure to reach the upstream <b>fails host startup</b> (fail-fast),
/// before Kestrel begins serving — preferable for a governance gateway and integrated with host
/// logging/cancellation. Disposal is the DI container's job (the singleton wrapper is container-owned).
/// </summary>
/// <remarks>
/// The concrete <see cref="McpUpstreamToolClient"/> is injected rather than the decorated
/// <c>IUpstreamToolClient</c> (G-09 #2), which matters twice over since A01-T7: the decorated chain
/// filters <c>tools/list</c> by caller role, and there is no caller at startup — it would report the
/// whole allow-list as missing.
/// </remarks>
public sealed class UpstreamConnectionInitializer(
    McpUpstreamToolClient upstream,
    Policy policy,
    ILogger<UpstreamConnectionInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Connecting to upstream MCP server…");
        await upstream.ConnectAsync(cancellationToken);
        logger.LogDebug("Upstream MCP session established.");

        // A01-T7: a diagnostic, never a gate. It must not become a new way for startup to fail — the
        // connect above is the fail-fast check; this only tells the operator their allow-list names
        // tools this server does not have (see UpstreamPolicyCheck for why it warns rather than throws).
        try
        {
            var tools = await upstream.ListToolsAsync(cancellationToken);
            UpstreamPolicyCheck.WarnOnMissing(logger, policy, tools.Select(tool => tool.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not list the upstream's tools to cross-check the policy allow-list. The gateway " +
                "is running; only this startup diagnostic was skipped.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
