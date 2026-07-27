using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Reports policy allow-list entries the connected upstream does not actually offer (A01-T7). Runs once,
/// at startup, after the upstream session is established.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Before A01-T7 the upstream was pinned, so an allow-list could only drift with
/// a deliberate version bump. Now that the operator chooses the server, an allow-list naming tools the
/// server has never heard of is an easy and silent misconfiguration — the caller simply sees fewer tools
/// than intended, with nothing explaining why.
/// </para>
/// <para>
/// <b>Why WARNING and not fatal.</b> Fail-fast is the correct posture where the failure mode is starting
/// OPEN (a missing policy, an unset caller key — <see cref="PolicyLoader"/> throws on both). Here it is
/// the reverse: deny-by-default makes an allow-listed-but-absent tool inert, so the gateway is only more
/// closed than intended. Upstream tool names have already drifted twice in this project (G-20), and
/// taking a governance gateway down on a benign version bump would be a worse failure than the one being
/// reported. The operator gets the diff; the gateway keeps serving.
/// </para>
/// <para>
/// Only this direction is reported. Upstream tools absent from every allow-list are deny-by-default
/// working as designed — the normal state for a server offering 90 tools to a role allowed six.
/// </para>
/// </remarks>
public static class UpstreamPolicyCheck
{
    /// <summary>
    /// Allow-listed tool names (across all roles, deduplicated) that no upstream tool matches. Matching
    /// is exact and ordinal, like every other tool-name comparison in the gateway.
    /// </summary>
    public static IReadOnlyList<string> MissingFromUpstream(
        Policy policy, IEnumerable<string> upstreamToolNames)
    {
        var available = upstreamToolNames.ToHashSet(StringComparer.Ordinal);
        return [.. policy.AllAllowlistedTools.Where(tool => !available.Contains(tool))];
    }

    /// <summary>
    /// Logs exactly one warning per missing tool name, or nothing at all when the policy is fully
    /// satisfied by the connected upstream.
    /// </summary>
    public static void WarnOnMissing(ILogger logger, Policy policy, IEnumerable<string> upstreamToolNames)
    {
        foreach (var tool in MissingFromUpstream(policy, upstreamToolNames))
        {
            logger.LogWarning(
                "Policy allow-lists tool {ToolName}, which the connected upstream MCP server does not " +
                "offer. Callers will never be able to use it. Check the tool name against the " +
                "upstream's tools/list — names drift between upstream versions.", tool);
        }
    }
}
