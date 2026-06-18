using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Darvoza.Gateway.Configuration;

/// <summary>
/// Loads and validates <c>policy.yaml</c> into an immutable <see cref="Policy"/> (A01-T3). Every failure
/// mode throws — the composition root calls this at startup so an invalid, missing, or half-configured
/// policy <b>fails host startup</b> rather than letting the gateway run open ("never start open",
/// mirroring the fail-fast upstream lifecycle, ADR-0002 / decision #6).
/// </summary>
public static class PolicyLoader
{
    /// <summary>Reads, parses, and validates the policy file at <paramref name="path"/>, or throws.</summary>
    public static Policy Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Policy file not found at '{path}'. Darvoza refuses to start without a policy — " +
                "deny-by-default requires an explicit policy.yaml (copy policy.example.yaml).");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>
    /// Parses + validates a policy from a YAML string, resolving each caller's <c>keyEnv</c> to its secret
    /// value via <paramref name="envLookup"/> (defaults to process environment variables). Internal so unit
    /// tests can drive parse/validation and key resolution without touching the filesystem or real env vars.
    /// </summary>
    internal static Policy Parse(string yaml, string source, Func<string, string?>? envLookup = null)
    {
        envLookup ??= Environment.GetEnvironmentVariable;

        PolicyDocument? doc;
        try
        {
            doc = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<PolicyDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(
                $"Policy file '{source}' is not valid YAML: {ex.Message}", ex);
        }

        if (doc is null)
            throw new InvalidOperationException($"Policy file '{source}' is empty — no roles or callers defined.");

        var roleAllowlists = BuildRoleAllowlists(doc);
        var keyToRole = BuildKeyToRole(doc, roleAllowlists, source, envLookup);
        return new Policy(keyToRole, roleAllowlists);
    }

    private static Dictionary<string, IReadOnlySet<string>> BuildRoleAllowlists(PolicyDocument doc)
    {
        var allowlists = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (roleName, role) in doc.Roles)
            allowlists[roleName] = new HashSet<string>(role?.Allow ?? [], StringComparer.Ordinal);
        return allowlists;
    }

    private static Dictionary<string, string> BuildKeyToRole(
        PolicyDocument doc,
        IReadOnlyDictionary<string, IReadOnlySet<string>> roleAllowlists,
        string source,
        Func<string, string?> envLookup)
    {
        var keyToRole = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var caller in doc.Callers)
        {
            if (string.IsNullOrWhiteSpace(caller.KeyEnv))
                throw new InvalidOperationException($"Policy file '{source}' has a caller with no 'keyEnv'.");

            if (string.IsNullOrWhiteSpace(caller.Role))
                throw new InvalidOperationException(
                    $"Policy file '{source}' caller '{caller.KeyEnv}' has no 'role'.");

            if (!roleAllowlists.ContainsKey(caller.Role))
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}' caller '{caller.KeyEnv}' references undefined role " +
                    $"'{caller.Role}'. Defined roles: {string.Join(", ", roleAllowlists.Keys)}.");
            }

            var keyValue = envLookup(caller.KeyEnv);
            if (string.IsNullOrEmpty(keyValue))
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}' caller references env var '{caller.KeyEnv}', but it is " +
                    "unset/empty. Set it to the caller's secret key — Darvoza refuses to start " +
                    "half-configured (a caller with no key can never authenticate).");
            }

            if (!keyToRole.TryAdd(keyValue, caller.Role))
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}' has two callers resolving to the same key value " +
                    $"(env '{caller.KeyEnv}'). Caller keys must be unique.");
            }
        }

        return keyToRole;
    }
}
