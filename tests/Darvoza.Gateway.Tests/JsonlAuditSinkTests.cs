using System.Text.Json;
using Darvoza.Gateway.Audit;

namespace Darvoza.Gateway.Tests;

// A01-T4 — the durable writer. Append-only, newline-terminated, one intact line per record even under
// concurrency, and disposed cleanly on shutdown. Uses a real temp file (no mocked filesystem — per the
// project's DB/IO testing rule).
public sealed class JsonlAuditSinkTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "darvoza-audit-tests", Guid.NewGuid().ToString("N"));

    private string AuditPath => Path.Combine(_dir, "audit.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Constructor_creates_a_missing_audit_directory()
    {
        // T6f: the ctor (not the first write) creates the directory — Program force-resolves the sink
        // at ApplicationStarted so the permission tripwire inspects a real directory even on a fresh
        // deployment. (On Unix it is created owner-only; the mode itself is untestable on Windows.)
        Assert.False(Directory.Exists(_dir));

        await using var sink = new JsonlAuditSink(AuditPath);

        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Appends_each_line_newline_terminated_in_order()
    {
        await using (var sink = new JsonlAuditSink(AuditPath))
        {
            await sink.WriteAsync("{\"n\":1}", CancellationToken.None);
            await sink.WriteAsync("{\"n\":2}", CancellationToken.None);
        }

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], await File.ReadAllLinesAsync(AuditPath));
    }

    [Fact]
    public async Task Preserves_pre_existing_lines_append_only()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(AuditPath, "{\"existing\":true}\n");

        await using (var sink = new JsonlAuditSink(AuditPath))
            await sink.WriteAsync("{\"n\":1}", CancellationToken.None);

        Assert.Equal(["{\"existing\":true}", "{\"n\":1}"], await File.ReadAllLinesAsync(AuditPath));
    }

    [Fact]
    public async Task Written_record_lines_are_valid_json()
    {
        var record = new AuditRecord
        {
            Ts = "2026-06-18T12:00:00.0000000+00:00",
            Tool = "wit_get_work_item",
            Caller = new AuditCaller { Role = "analyst", KeyFingerprint = "abcd1234" },
            Decision = "allow",
            Args = new AuditArgs { Keys = ["id"], Count = 1, Sha256 = "ff" },
            Upstream = new AuditUpstream { Status = "ok" },
            LatencyMs = 5,
        };

        await using (var sink = new JsonlAuditSink(AuditPath))
            await sink.WriteAsync(record.ToJsonLine(), CancellationToken.None);

        var line = Assert.Single(await File.ReadAllLinesAsync(AuditPath));
        using var doc = JsonDocument.Parse(line); // throws if the line is not valid JSON
        Assert.Equal("wit_get_work_item", doc.RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task Concurrent_writes_produce_intact_parseable_lines()
    {
        await using (var sink = new JsonlAuditSink(AuditPath))
        {
            var writes = Enumerable.Range(0, 50)
                .Select(i => sink.WriteAsync($"{{\"n\":{i}}}", CancellationToken.None).AsTask());
            await Task.WhenAll(writes);
        }

        var lines = await File.ReadAllLinesAsync(AuditPath);
        Assert.Equal(50, lines.Length);
        var ns = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("n").GetInt32()).OrderBy(n => n);
        Assert.Equal(Enumerable.Range(0, 50), ns); // every line intact (no interleaving) and parseable
    }

    [Fact]
    public async Task Write_after_dispose_throws_object_disposed()
    {
        var sink = new JsonlAuditSink(AuditPath);
        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sink.WriteAsync("{\"n\":1}", CancellationToken.None));
    }
}
