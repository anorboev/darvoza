using System.Text.Json;
using Darvoza.Gateway.Audit;
using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Tests;

// A01-T4 — the audit decorator IS the headline NFR: EXACTLY ONE JSONL record per CallToolAsync for every
// outcome (allowed→ok, allowed→upstream-error, policy-denied), no raw secrets, fail-closed on write
// failure. The SUT wraps a REAL PolicyEnforcingToolClient (over a fake upstream), so the decision-box
// handshake between the two decorators is exercised end-to-end, not mocked.
public class AuditingToolClientTests
{
    private static Policy TwoRolePolicy() => new(
        keyToRole: new Dictionary<string, string>
        {
            ["analyst-key"] = "analyst",
            ["engineer-key"] = "engineer",
        },
        roleAllowlists: new Dictionary<string, IReadOnlySet<string>>
        {
            ["analyst"] = new HashSet<string> { "wit_get_work_item", "repo_list" },
            ["engineer"] = new HashSet<string> { "wit_get_work_item", "repo_list", "wit_create_work_item" },
        });

    private static (AuditingToolClient sut, FakeUpstreamToolClient upstream, FakeAuditSink sink, ManualTimeProvider time) Build(
        string? callerKey, CallToolResult? upstreamResult = null)
    {
        var upstream = new FakeUpstreamToolClient
        {
            Tools =
            [
                new Tool { Name = "wit_get_work_item" },
                new Tool { Name = "repo_list" },
                new Tool { Name = "wit_create_work_item" },
            ],
            CallResult = upstreamResult ?? new CallToolResult { Content = [new TextContentBlock { Text = "upstream-ok" }] },
        };
        var decisions = new AsyncLocalCallDecisionContext();
        var policy = new PolicyEnforcingToolClient(
            upstream, TwoRolePolicy(), new FakeCallerKeyProvider { Key = callerKey }, decisions,
            new CallerFingerprint("test-salt"u8.ToArray()));
        var sink = new FakeAuditSink();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));
        var sut = new AuditingToolClient(policy, sink, decisions, time);
        return (sut, upstream, sink, time);
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public async Task Denied_call_writes_exactly_one_deny_record_and_never_calls_upstream()
    {
        var (sut, upstream, sink, _) = Build("analyst-key"); // analyst may not create work items

        var result = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_create_work_item" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(upstream.LastCallParams); // policy short-circuited before upstream — the deny case must still log

        var record = Parse(Assert.Single(sink.Lines));
        Assert.Equal("deny", record.GetProperty("decision").GetString());
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, record.GetProperty("upstream").ValueKind); // no upstream status on deny
        Assert.Equal("wit_create_work_item", record.GetProperty("tool").GetString());
        Assert.Equal("analyst", record.GetProperty("caller").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Allowed_ok_call_writes_one_allow_record_with_upstream_ok()
    {
        var (sut, upstream, sink, _) = Build("engineer-key");
        var callParams = new CallToolRequestParams { Name = "wit_create_work_item" };

        var result = await sut.CallToolAsync(callParams, CancellationToken.None);

        Assert.Same(callParams, upstream.LastCallParams); // forwarded verbatim
        Assert.NotEqual(true, result.IsError);

        var record = Parse(Assert.Single(sink.Lines));
        Assert.Equal("allow", record.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("reason").ValueKind);
        Assert.Equal("ok", record.GetProperty("upstream").GetProperty("status").GetString());
        Assert.Equal("engineer", record.GetProperty("caller").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Allowed_call_with_upstream_error_writes_one_allow_record_marking_error()
    {
        var (sut, _, sink, _) = Build("engineer-key",
            upstreamResult: new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }] });

        var result = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_create_work_item" }, CancellationToken.None);

        Assert.True(result.IsError);
        var record = Parse(Assert.Single(sink.Lines));
        Assert.Equal("allow", record.GetProperty("decision").GetString());            // policy allowed it
        Assert.Equal("error", record.GetProperty("upstream").GetProperty("status").GetString()); // upstream failed
    }

    [Fact]
    public async Task Each_call_writes_exactly_one_record()
    {
        var (sut, _, sink, _) = Build("engineer-key");

        await sut.CallToolAsync(new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);
        await sut.CallToolAsync(new CallToolRequestParams { Name = "wit_create_work_item" }, CancellationToken.None);
        await sut.CallToolAsync(new CallToolRequestParams { Name = "not_a_real_tool" }, CancellationToken.None); // denied

        Assert.Equal(3, sink.Lines.Count); // one per call — allows AND the denial
    }

    [Fact]
    public async Task Record_never_contains_the_raw_key_or_argument_values()
    {
        var (sut, _, sink, _) = Build("engineer-key");
        var args = new Dictionary<string, JsonElement>
        {
            ["workItemId"] = JsonSerializer.SerializeToElement("WI-SENSITIVE-TOKEN"),
            ["project"] = JsonSerializer.SerializeToElement("ProjectCodenameZ"),
        };

        await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_get_work_item", Arguments = args }, CancellationToken.None);

        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain("engineer-key", line);       // raw caller key never written
        Assert.DoesNotContain("WI-SENSITIVE-TOKEN", line); // argument value never written
        Assert.DoesNotContain("ProjectCodenameZ", line);   // argument value never written

        var args2 = Parse(line).GetProperty("args");
        var keys = args2.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["project", "workItemId"], keys);     // key NAMES are recorded, ordinally sorted
        Assert.Equal(2, args2.GetProperty("count").GetInt32());
        Assert.False(string.IsNullOrEmpty(args2.GetProperty("sha256").GetString()));
    }

    [Fact]
    public async Task Record_timestamp_is_iso8601_utc_from_the_clock()
    {
        var (sut, _, sink, _) = Build("engineer-key");

        await sut.CallToolAsync(new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);

        var record = Parse(Assert.Single(sink.Lines));
        Assert.Equal("2026-06-18T12:00:00.0000000+00:00", record.GetProperty("ts").GetString());
    }

    [Fact]
    public async Task Record_latency_reflects_time_elapsed_during_the_call()
    {
        var (sut, upstream, sink, time) = Build("engineer-key");
        upstream.BeforeReturn = () => time.Advance(TimeSpan.FromMilliseconds(42));

        await sut.CallToolAsync(new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);

        var record = Parse(Assert.Single(sink.Lines));
        Assert.Equal(42, record.GetProperty("latencyMs").GetInt64());
    }

    [Fact]
    public async Task Caller_fingerprint_is_stable_per_key_differs_across_keys_and_is_not_the_raw_key()
    {
        var (analyst, _, analystSink, _) = Build("analyst-key");
        await analyst.CallToolAsync(new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);
        await analyst.CallToolAsync(new CallToolRequestParams { Name = "repo_list" }, CancellationToken.None);

        var (engineer, _, engineerSink, _) = Build("engineer-key");
        await engineer.CallToolAsync(new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);

        string? Fp(FakeAuditSink s, int i) => Parse(s.Lines[i]).GetProperty("caller").GetProperty("keyFingerprint").GetString();

        Assert.Equal(Fp(analystSink, 0), Fp(analystSink, 1)); // stable for the same key
        Assert.NotEqual(Fp(analystSink, 0), Fp(engineerSink, 0)); // differs across keys
        Assert.NotEqual("analyst-key", Fp(analystSink, 0)); // never the raw key
        Assert.Equal(CallerFingerprint.HexLength, Fp(analystSink, 0)!.Length); // truncated digest (16 hex since T6e)
    }

    [Fact]
    public async Task Audit_write_failure_fails_closed_and_does_not_surface_the_upstream_result()
    {
        var (sut, _, sink, _) = Build("engineer-key");
        sink.ThrowOnWrite = true;

        var result = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("upstream-ok", text.Text); // the unaudited success is NOT returned to the caller
        Assert.Empty(sink.Lines);                         // nothing recorded
    }

    [Fact]
    public async Task Upstream_exception_still_writes_exactly_one_error_record_then_rethrows()
    {
        var (sut, upstream, sink, _) = Build("engineer-key"); // allowed, so the call reaches upstream
        upstream.ThrowOnCall = true;                          // ...where the transport faults

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.CallToolAsync(
                new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None));

        var record = Parse(Assert.Single(sink.Lines)); // 100% coverage: the faulted call is still recorded
        Assert.Equal("allow", record.GetProperty("decision").GetString());
        Assert.Equal("error", record.GetProperty("upstream").GetProperty("status").GetString());
    }

    [Fact]
    public async Task When_both_upstream_and_audit_fail_the_upstream_exception_propagates()
    {
        var (sut, upstream, sink, _) = Build("engineer-key");
        upstream.ThrowOnCall = true; // upstream transport faults...
        sink.ThrowOnWrite = true;    // ...and the best-effort audit write also fails

        // The original upstream fault must surface — not the secondary IOException from the sink.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.CallToolAsync(
                new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None));
        Assert.Empty(sink.Lines);
    }

    [Fact]
    public async Task ListTools_is_forwarded_and_writes_no_audit_record()
    {
        var (sut, _, sink, _) = Build("engineer-key");

        var tools = await sut.ListToolsAsync(CancellationToken.None);

        Assert.NotEmpty(tools);   // the policy-filtered passthrough still works
        Assert.Empty(sink.Lines); // listing tools is not a tool CALL → out of audit scope
    }
}
