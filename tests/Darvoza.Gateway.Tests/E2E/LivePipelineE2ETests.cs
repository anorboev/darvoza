using System.Text.Json;

namespace Darvoza.Gateway.Tests.E2E;

/// <summary>
/// End-to-end proof of the headline story over the LIVE HTTP pipeline (A01-T5), and the deferred
/// header-flow integration test (A01-T6d / G-13 #2): a real MCP client presents <c>X-Darvoza-Key</c>
/// over streamable HTTP, the real <c>HttpHeaderCallerKeyProvider</c> reads it off the request, the real
/// policy + audit decorators run, and only the upstream leaf is faked.
///
/// The contract being proven: the SAME write tool is denied for the read-only analyst and allowed for the
/// engineer, each producing EXACTLY ONE audit record — and a denied call never reaches upstream.
/// </summary>
public sealed class LivePipelineE2ETests
{
    private const string WriteTool = "wit_create_work_item";
    private const string ReadTool = "wit_get_work_item";

    private static IReadOnlyDictionary<string, object?> WorkItemArgs() => new Dictionary<string, object?>
    {
        ["project"] = "darvoza-demo",
        ["title"] = "Demo work item",
    };

    private static JsonElement SingleAuditRecord(DarvozaWebAppFactory factory)
    {
        Assert.Single(factory.Audit.Lines);
        return JsonDocument.Parse(factory.Audit.Lines[0]).RootElement;
    }

    [Fact]
    public async Task Analyst_write_is_denied_with_one_deny_record_and_upstream_is_never_called()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.AnalystKey);

        var result = await client.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.True(result.IsError);                       // policy-denied result surfaced to the client
        Assert.Null(factory.Upstream.LastCallParams);      // the call never reached the upstream leaf

        var record = SingleAuditRecord(factory);
        Assert.Equal("deny", record.GetProperty("decision").GetString());
        Assert.Equal("analyst", record.GetProperty("caller").GetProperty("role").GetString());
        Assert.Equal(WriteTool, record.GetProperty("tool").GetString());
        // A denied call never touched upstream, so the record carries no upstream status.
        Assert.Equal(JsonValueKind.Null, record.GetProperty("upstream").ValueKind);
    }

    [Fact]
    public async Task Engineer_write_is_forwarded_with_one_allow_record()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);

        var result = await client.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.NotEqual(true, result.IsError);                           // success result (IsError is null/false)
        Assert.NotNull(factory.Upstream.LastCallParams);                 // forwarded to upstream...
        Assert.Equal(WriteTool, factory.Upstream.LastCallParams!.Name);  // ...as the same tool

        var record = SingleAuditRecord(factory);
        Assert.Equal("allow", record.GetProperty("decision").GetString());
        Assert.Equal("engineer", record.GetProperty("caller").GetProperty("role").GetString());
        Assert.Equal("ok", record.GetProperty("upstream").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Analyst_read_is_allowed_proving_deny_is_per_tool_not_per_role()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.AnalystKey);

        var result = await client.CallToolAsync(ReadTool, WorkItemArgs());

        Assert.NotEqual(true, result.IsError);                           // success result (IsError is null/false)
        Assert.Equal(ReadTool, factory.Upstream.LastCallParams!.Name);

        var record = SingleAuditRecord(factory);
        Assert.Equal("allow", record.GetProperty("decision").GetString());
        Assert.Equal("analyst", record.GetProperty("caller").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Missing_header_is_denied_and_upstream_is_never_called()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(callerKey: null);

        var result = await client.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.True(result.IsError);
        Assert.Null(factory.Upstream.LastCallParams);

        var record = SingleAuditRecord(factory);
        Assert.Equal("deny", record.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Audit_record_never_contains_the_raw_caller_key()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);

        await client.CallToolAsync(WriteTool, WorkItemArgs());

        var line = Assert.Single(factory.Audit.Lines);
        Assert.DoesNotContain(DarvozaWebAppFactory.EngineerKey, line);   // raw key is redacted out...
        // ...replaced by a short non-reversible fingerprint.
        var fingerprint = SingleAuditRecord(factory)
            .GetProperty("caller").GetProperty("keyFingerprint").GetString();
        Assert.False(string.IsNullOrEmpty(fingerprint));
        Assert.DoesNotContain(DarvozaWebAppFactory.EngineerKey, fingerprint!);
    }

    [Fact]
    public async Task Allow_record_summarizes_args_by_keys_and_count_without_values()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);

        await client.CallToolAsync(WriteTool, WorkItemArgs());

        var args = SingleAuditRecord(factory).GetProperty("args");
        Assert.Equal(2, args.GetProperty("count").GetInt32());
        var keys = args.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("project", keys);
        Assert.Contains("title", keys);
        // The redaction contract: argument VALUES never appear in the trail.
        var line = factory.Audit.Lines[0];
        Assert.DoesNotContain("Demo work item", line);
    }
}
