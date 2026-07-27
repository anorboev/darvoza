using System.Text.Json;
using ModelContextProtocol.Protocol;

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

        Assert.NotEqual(true, result.IsError);                           // MCP SDK leaves IsError null on success
        Assert.NotEmpty(result.Content);                                 // a real upstream payload came back
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

        Assert.NotEqual(true, result.IsError);                           // MCP SDK leaves IsError null on success
        Assert.NotEmpty(result.Content);                                 // a real upstream payload came back
        Assert.Equal(ReadTool, factory.Upstream.LastCallParams!.Name);

        var record = SingleAuditRecord(factory);
        Assert.Equal("allow", record.GetProperty("decision").GetString());
        Assert.Equal("analyst", record.GetProperty("caller").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Tools_list_is_filtered_per_role_so_the_analyst_cannot_even_see_the_write()
    {
        await using var factory = new DarvozaWebAppFactory();

        await using var analyst = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.AnalystKey);
        var analystTools = (await analyst.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.Contains(ReadTool, analystTools);
        Assert.DoesNotContain(WriteTool, analystTools);     // deny-by-default hides the unlisted write entirely

        await using var engineer = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);
        var engineerTools = (await engineer.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.Contains(WriteTool, engineerTools);          // the engineer role does see it
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
    public async Task Unknown_key_is_denied_with_one_audit_record_and_upstream_is_never_called()
    {
        // T6b (G-10 #2): an unknown-but-present key is the probe case — it must deny AND leave a trail
        // (unlike unauthenticated tools/list, which is deliberately unaudited — G-21).
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync("not-a-configured-key");

        var result = await client.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.True(result.IsError);
        Assert.Null(factory.Upstream.LastCallParams);

        var record = SingleAuditRecord(factory);
        Assert.Equal("deny", record.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("caller").GetProperty("role").ValueKind);
    }

    [Fact]
    public async Task Duplicated_key_header_is_ambiguous_and_denied()
    {
        // T6b: a proxy or client misconfig that duplicates X-Darvoza-Key must not half-authenticate.
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientWithHeaderValuesAsync(
            [DarvozaWebAppFactory.EngineerKey, DarvozaWebAppFactory.EngineerKey]);

        var result = await client.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.True(result.IsError);
        Assert.Null(factory.Upstream.LastCallParams);
        Assert.Equal("deny", SingleAuditRecord(factory).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Denial_text_is_identical_for_unknown_key_and_known_key_denied_tool()
    {
        // T6b info-leak check: the deny result must not let a caller distinguish "my key is unknown"
        // from "my key is known but this tool is denied" — either would confirm key validity.
        await using var factory = new DarvozaWebAppFactory();

        static string DenyText(CallToolResult result) =>
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        await using var unknown = await factory.CreateMcpClientAsync("not-a-configured-key");
        var unknownDenial = await unknown.CallToolAsync(WriteTool, WorkItemArgs());

        await using var analyst = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.AnalystKey);
        var knownDenial = await analyst.CallToolAsync(WriteTool, WorkItemArgs());

        Assert.True(unknownDenial.IsError);
        Assert.True(knownDenial.IsError);
        Assert.Equal(DenyText(knownDenial), DenyText(unknownDenial));
    }

    [Fact]
    public async Task Audit_record_never_contains_the_raw_caller_key()
    {
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);

        await client.CallToolAsync(WriteTool, WorkItemArgs());

        var line = Assert.Single(factory.Audit.Lines);
        Assert.DoesNotContain(DarvozaWebAppFactory.EngineerKey, line);       // raw key is redacted out...
        Assert.DoesNotContain(DarvozaWebAppFactory.FingerprintSalt, line);   // ...and the salt never leaks (T6e)
        // ...replaced by a short non-reversible fingerprint: the truncated HMAC-SHA256 of the key under
        // the deployment salt the factory configured (T6e — salted, so a published trail cannot be
        // dictionary-matched against guessed keys).
        var fingerprint = SingleAuditRecord(factory)
            .GetProperty("caller").GetProperty("keyFingerprint").GetString();
        var expected = Convert.ToHexStringLower(
                System.Security.Cryptography.HMACSHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(DarvozaWebAppFactory.FingerprintSalt),
                    System.Text.Encoding.UTF8.GetBytes(DarvozaWebAppFactory.EngineerKey)))
            [..Darvoza.Gateway.Audit.CallerFingerprint.HexLength];
        Assert.Equal(expected, fingerprint);
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

    // --- A01-T7: the configured upstream, observed on the REAL composition root ---------------------

    [Fact]
    public async Task Startup_states_the_resolved_upstream_argv_exactly_once()
    {
        // A COUNT assertion, not a contains: an operator reading the log must be able to answer "which
        // server is this gateway in front of?" — and a duplicated line would mean the resolution ran
        // more than once, which for a process launch is exactly the thing worth noticing.
        await using var factory = new DarvozaWebAppFactory();
        await using var client = await factory.CreateMcpClientAsync(DarvozaWebAppFactory.EngineerKey);
        await client.CallToolAsync(ReadTool, WorkItemArgs());

        var launchLines = factory.Logs.Records
            .Where(record => record.Message.Contains("Upstream MCP server:"))
            .ToArray();

        Assert.Single(launchLines);
        Assert.Contains("darvoza-e2e-stub-upstream", launchLines[0].Message);
        // Rendered as a JSON array, so argv element boundaries survive into the log.
        Assert.Contains("""["--never-launched"]""", launchLines[0].Message);
    }

    [Fact]
    public async Task The_configured_upstream_from_the_policy_file_is_what_the_host_resolved()
    {
        // The claim "governance gateway in front of ANY MCP server" made observable end to end: the
        // fixture policy names a non-Azure-DevOps command, and the real composition root — which still
        // fails fast on a bad policy, still enforces deny-by-default, still audits — booted on it with
        // no ADO_ORG and no PAT in the environment at all.
        await using var factory = new DarvozaWebAppFactory();

        var launchLine = Assert.Single(
            factory.Logs.Records, record => record.Message.Contains("Upstream MCP server:"));

        Assert.DoesNotContain("@azure-devops/mcp", launchLine.Message);
        Assert.DoesNotContain("npx", launchLine.Message);
    }
}
