using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Darvoza.Gateway.Configuration;

/// <summary>
/// Loads and validates <c>policy.yaml</c> into an immutable <see cref="Policy"/> (A01-T3). Every failure
/// mode throws — the composition root calls this at startup so an invalid, missing, or half-configured
/// policy <b>fails host startup</b> rather than letting the gateway run open ("never start open",
/// mirroring the fail-fast upstream lifecycle — ADR-0002, <c>docs/adr/ADR-0002-tool-pipeline-decorator-seam.md</c>).
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

        // YamlDotNet sets properties directly and may leave a section null (key present but empty, or absent
        // with no C# initializer applied). Guard before iterating so a malformed file fails fast with a clear
        // message rather than a NullReferenceException.
        if (doc.Roles is null || doc.Callers is null)
        {
            throw new InvalidOperationException(
                $"Policy file '{source}' must define both a 'roles' and a 'callers' section.");
        }

        if (doc.Callers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Policy file '{source}' defines no callers — no caller could ever authenticate. " +
                "Define at least one caller (or stop the gateway if you intend to allow nobody).");
        }

        var roleAllowlists = BuildRoleAllowlists(doc);
        var keyToRole = BuildKeyToRole(doc, roleAllowlists, source, envLookup);
        return new Policy(keyToRole, roleAllowlists, BuildUpstream(doc, source));
    }

    /// <summary>
    /// Projects the optional <c>upstream:</c> section into <see cref="UpstreamOptions"/> (A01-T7),
    /// failing fast on every ambiguous shape. Absent section = the azure-devops profile, so a config
    /// file written before A01-T7 keeps its exact behaviour.
    /// </summary>
    private static UpstreamOptions BuildUpstream(PolicyDocument doc, string source)
    {
        if (doc.Upstream is not { } upstream)
            return UpstreamOptions.AzureDevOps;

        var hasProfile = upstream.Profile is not null;
        var hasCommand = upstream.Command is not null;

        if (hasProfile && hasCommand)
        {
            throw new InvalidOperationException(
                $"Policy file '{source}': 'upstream.profile' and 'upstream.command' are mutually " +
                "exclusive — a profile IS a built-in command. Use a profile for a server Darvoza " +
                "ships support for, or a command for your own.");
        }

        if (!hasCommand)
        {
            // A present-but-blank profile is an operator mistake, not a request for the default: a
            // typo'd profile already failed fast, so a whitespace one should not quietly select a
            // different server (@pr-reviewer MINOR, @security-reviewer LOW).
            if (hasProfile && string.IsNullOrWhiteSpace(upstream.Profile))
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}': 'upstream.profile' is blank. Name a profile " +
                    $"('{UpstreamOptions.AzureDevOpsProfile}'), or remove the key for the default.");
            }

            // Args/passEnv only mean something alongside a command. Silently dropping them would leave
            // the gateway running happily against the WRONG server (@pr-reviewer MAJOR).
            if (upstream.Args is not null || upstream.PassEnv is not null)
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}': 'upstream.args' and 'upstream.passEnv' require " +
                    "'upstream.command'. A built-in profile supplies its own arguments and inherits " +
                    "the gateway's environment.");
            }

            return !hasProfile || upstream.Profile == UpstreamOptions.AzureDevOpsProfile
                ? UpstreamOptions.AzureDevOps
                : new UpstreamOptions { Profile = upstream.Profile! };
        }

        if (string.IsNullOrWhiteSpace(upstream.Command))
        {
            throw new InvalidOperationException(
                $"Policy file '{source}': 'upstream.command' is empty. Name the executable to launch " +
                "(Darvoza passes it to the process launcher as a command plus an argv array, and never " +
                "builds a command line out of it).");
        }

        return new UpstreamOptions
        {
            Command = upstream.Command,
            Args = ParseUpstreamStringList(upstream.Args, "upstream.args", source),
            PassEnv = ParseUpstreamStringList(upstream.PassEnv, "upstream.passEnv", source),
        };
    }

    /// <summary>
    /// Reads an upstream string list — one element per argv slot for <c>args</c>, one variable name per
    /// element for <c>passEnv</c>. A single string is rejected rather than split: word-splitting a
    /// command line is precisely what a shell does, and Darvoza does it nowhere (ADR-0004).
    /// </summary>
    /// <remarks>
    /// Elements must be scalars. A nested sequence or map would otherwise reach argv as the CLR type
    /// name via <c>ToString()</c> — silent garbage where the surrounding validation promises fail-fast
    /// (@pr-reviewer MAJOR on PR #14). YamlDotNet yields plain scalars as <see cref="string"/>, so an
    /// unquoted <c>8080</c> or <c>true</c> arrives as text and is accepted as written.
    /// </remarks>
    private static IReadOnlyList<string> ParseUpstreamStringList(object? value, string key, string source)
    {
        if (value is null)
            return [];

        if (value is not IList<object> elements)
        {
            throw new InvalidOperationException(
                $"Policy file '{source}': '{key}' must be a list, one element per entry " +
                """(e.g. args: ["server.js", "--readonly"]). Darvoza never splits a string into """ +
                "multiple entries — that is the shell behaviour the upstream launch exists to avoid.");
        }

        var parsed = new List<string>(elements.Count);
        foreach (var element in elements)
        {
            if (element is not string text)
            {
                throw new InvalidOperationException(
                    $"Policy file '{source}': every entry in '{key}' must be a single scalar value; " +
                    $"found {(element is null ? "an empty entry" : "a nested list or map")}. Quote the " +
                    "value if it contains YAML punctuation.");
            }

            parsed.Add(text);
        }

        return parsed;
    }

    private static Dictionary<string, IReadOnlySet<string>> BuildRoleAllowlists(PolicyDocument doc)
    {
        var allowlists = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (roleName, role) in doc.Roles)
        {
            // A null role body ("analyst:" with no children) or an explicit "allow: []" are both valid
            // deny-all roles — deny-by-default makes an empty allow-list meaningful, not an error.
            allowlists[roleName] = new HashSet<string>(role?.Allow ?? [], StringComparer.Ordinal);
        }

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
