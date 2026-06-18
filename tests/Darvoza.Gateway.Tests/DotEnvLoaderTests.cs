using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Tests;

// A01-T2a (G-07) — the spike .env loader walked dir.Parent to the FILESYSTEM ROOT and took the first
// .env it found, so a stray .env outside the project tree could be picked up. The replacement is
// BOUNDED: it never searches above the repo/solution root marker (.git, *.sln, *.slnx).
public sealed class DotEnvLoaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("darvoza-env-test");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void FindEnvFile_does_not_escape_above_the_repo_root_marker()
    {
        // <temp>/.env             <- a stray .env ABOVE the root; must NOT be picked.
        // <temp>/repo/App.slnx    <- root marker.
        // <temp>/repo/sub/        <- search starts here; no .env between here and the marker.
        File.WriteAllText(Path.Combine(_root.FullName, ".env"), "LEAK=should-not-load");
        var repo = _root.CreateSubdirectory("repo");
        File.WriteAllText(Path.Combine(repo.FullName, "App.slnx"), "<Solution/>");
        var sub = repo.CreateSubdirectory("sub");

        var found = DotEnvLoader.FindEnvFile(sub.FullName);

        Assert.Null(found);
    }

    [Fact]
    public void FindEnvFile_returns_env_at_the_repo_root()
    {
        var repo = _root.CreateSubdirectory("repo");
        File.WriteAllText(Path.Combine(repo.FullName, "App.slnx"), "<Solution/>");
        var envPath = Path.Combine(repo.FullName, ".env");
        File.WriteAllText(envPath, "ADO_ORG=demo");
        var sub = repo.CreateSubdirectory("sub");

        var found = DotEnvLoader.FindEnvFile(sub.FullName);

        Assert.Equal(envPath, found);
    }
}
