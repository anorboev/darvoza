namespace Darvoza.Gateway.Upstream;

/// <summary>Whether the policy stage let a call through or refused it.</summary>
public enum CallDecisionKind
{
    /// <summary>The tool was on the caller-role's allow-list and the call was forwarded.</summary>
    Allow,

    /// <summary>The tool was not permitted; the call was refused before the inner client (deny-by-default).</summary>
    Deny,
}

/// <summary>
/// A mutable, single-call carrier for the policy decision so the OUTER audit decorator (A01-T4) can
/// record what the INNER policy decorator (A01-T3) decided — including the denial reason, which the
/// returned <see cref="ModelContextProtocol.Protocol.CallToolResult"/> cannot reliably convey (it cannot
/// be subclassed, and a policy-denial and an upstream error are otherwise indistinguishable).
/// </summary>
/// <remarks>
/// The audit decorator creates one box per <c>CallToolAsync</c> and flows it DOWN to the policy stage via
/// <see cref="ICallDecisionContext"/> (an <see cref="System.Threading.AsyncLocal{T}"/> propagates to
/// callees reliably, but a callee's write does NOT flow back to the caller across an await — so the box
/// is a shared reference the policy mutates and the audit decorator reads through its own handle, never
/// relying on upward propagation). One box per call — never shared between calls.
/// </remarks>
public sealed class CallDecisionBox
{
    /// <summary>The decision the policy stage reached, or <c>null</c> if that stage never ran (defensive).</summary>
    public CallDecisionKind? Decision { get; private set; }

    /// <summary>The non-leaky denial reason; <c>null</c> on allow.</summary>
    public string? Reason { get; private set; }

    /// <summary>The caller's resolved role, or <c>null</c> when the key is unknown/missing.</summary>
    public string? Role { get; private set; }

    /// <summary>A non-reversible short fingerprint of the caller key; <c>null</c> when no key was presented.</summary>
    public string? CallerFingerprint { get; private set; }

    /// <summary>Records that the call was permitted.</summary>
    public void RecordAllow(string? role, string? callerFingerprint)
    {
        Decision = CallDecisionKind.Allow;
        Reason = null;
        Role = role;
        CallerFingerprint = callerFingerprint;
    }

    /// <summary>Records that the call was refused, with the reason the policy stage produced.</summary>
    public void RecordDeny(string reason, string? role, string? callerFingerprint)
    {
        Decision = CallDecisionKind.Deny;
        Reason = reason;
        Role = role;
        CallerFingerprint = callerFingerprint;
    }
}
