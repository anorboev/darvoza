namespace Darvoza.Gateway.Configuration;

/// <summary>
/// Loads <c>KEY=VALUE</c> lines from a project-root <c>.env</c> into process environment variables
/// (without overwriting values already set), so local dev secrets live only in the gitignored
/// <c>.env</c> and never in the repo.
/// </summary>
/// <remarks>
/// A01-T2a (gotcha G-07): the spike loader walked <c>dir.Parent</c> to the FILESYSTEM ROOT and took
/// the first <c>.env</c> it found, which could pick up a stray <c>.env</c> outside the project tree.
/// This loader is <b>bounded</b>: it searches from the start directory upward but never past the
/// repo/solution root (a directory containing <c>.git</c>, <c>*.sln</c>, or <c>*.slnx</c>).
/// </remarks>
public static class DotEnvLoader
{
    private const string EnvFileName = ".env";

    /// <summary>
    /// Finds the nearest <c>.env</c> walking up from <paramref name="startDir"/>, stopping at (and
    /// never searching above) the first repo/solution root marker. Returns the full path, or
    /// <see langword="null"/> if no <c>.env</c> exists within the bounded range.
    /// </summary>
    public static string? FindEnvFile(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var envPath = Path.Combine(dir.FullName, EnvFileName);
            if (File.Exists(envPath))
                return envPath;

            // Reached the repo/solution root without finding a .env — stop here, do NOT escape above it.
            if (IsRepoRoot(dir))
                return null;
        }

        return null;
    }

    /// <summary>
    /// Resolves the bounded <c>.env</c> (see <see cref="FindEnvFile"/>) starting from
    /// <paramref name="startDir"/> and loads its entries into process environment variables without
    /// overwriting variables that are already set. Returns the loaded path, or <see langword="null"/>
    /// if no <c>.env</c> was found.
    /// </summary>
    public static string? Load(string startDir)
    {
        var path = FindEnvFile(startDir);
        if (path is null)
            return null;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var val = Unquote(line[(eq + 1)..].Trim());
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, val);
        }

        return path;
    }

    /// <summary>
    /// Strips ONE matching pair of surrounding quotes (single or double) from a value, leaving it
    /// otherwise intact (inner <c>=</c> and base64 <c>=</c> padding are preserved). Supports both
    /// <c>KEY="value"</c> and Docker-style <c>KEY='value'</c> so an operator's PAT isn't passed to the
    /// upstream with literal quotes around it.
    /// </summary>
    internal static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    private static bool IsRepoRoot(DirectoryInfo dir) =>
        Directory.Exists(Path.Combine(dir.FullName, ".git"))
        || File.Exists(Path.Combine(dir.FullName, ".git"))   // .git file for worktrees/submodules
        || dir.EnumerateFiles("*.sln").Any()
        || dir.EnumerateFiles("*.slnx").Any();
}
