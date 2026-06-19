using Darvoza.Gateway.Audit;
using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;

namespace Darvoza.Gateway.Tests.E2E;

/// <summary>
/// Boots the REAL gateway composition root (<c>Program</c>) over an in-memory test server so the e2e test
/// can drive the live <c>X-Darvoza-Key → HttpHeaderCallerKeyProvider → policy → audit</c> pipeline
/// (A01-T5; closes the deferred A01-T6d header-flow integration gate, G-13 #2).
/// </summary>
/// <remarks>
/// <para>
/// What stays REAL: the MCP HTTP front leg (<c>MapMcp</c>), <c>PassthroughToolHandlers</c>, the
/// <c>AuditingToolClient → PolicyEnforcingToolClient</c> decorator chain, and crucially
/// <c>HttpHeaderCallerKeyProvider</c> reading the header off a real <see cref="HttpContext"/>.
/// </para>
/// <para>
/// What is faked: only the upstream LEAF (<see cref="FakeUpstreamToolClient"/> instead of
/// <c>McpUpstreamToolClient</c>, so no <c>npx</c>/Azure DevOps) and the audit sink
/// (<see cref="FakeAuditSink"/>, so records are asserted in-memory without touching disk). The
/// <c>UpstreamConnectionInitializer</c> hosted service is removed so host startup never tries to connect.
/// </para>
/// <para>
/// Pre-<c>Build()</c> env (set in the static ctor, before the host builder runs the top-level program):
/// a valid <c>ADO_ORG</c>, a dummy PAT (lets the stdio transport DESCRIPTOR build — it is never connected),
/// the fixture policy path, and the two caller-key env vars the policy references.
/// </para>
/// </remarks>
internal sealed class DarvozaWebAppFactory : WebApplicationFactory<Program>
{
    public const string AnalystKey = "e2e-analyst-key-DO-NOT-LOG";
    public const string EngineerKey = "e2e-engineer-key-DO-NOT-LOG";

    /// <summary>The fake upstream leaf — inspect <see cref="FakeUpstreamToolClient.LastCallParams"/> to prove
    /// a denied call never reached upstream, or configure its result for an allowed call.</summary>
    public FakeUpstreamToolClient Upstream { get; } = new();

    /// <summary>The audit sink — assert on <see cref="FakeAuditSink.Lines"/> (the exact JSONL bytes).</summary>
    public FakeAuditSink Audit { get; } = new();

    /// <summary>
    /// The top-level <c>Program</c> reads ADO_ORG / the PAT / the policy path + caller-key env vars via
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> BEFORE the host is built — there is no
    /// config seam for them. So we set them, force the host to build now (capturing them into the Policy
    /// + transport singletons), then RESTORE the originals so this process-global mutation never leaks into
    /// the unit tests that assert on these vars being unset (e.g. ResolvePolicyPath precedence). The suite
    /// disables cross-class parallelization (see <c>TestParallelization.cs</c>) so no test reads them mid-window.
    /// </summary>
    public DarvozaWebAppFactory()
    {
        var env = new (string Key, string? Value)[]
        {
            ("ADO_ORG", "darvoza-demo"),
            ("AZURE_DEVOPS_EXT_PAT", "dummy-pat-never-connected"),
            ("PERSONAL_ACCESS_TOKEN", null), // force the AZURE_DEVOPS_EXT_PAT path
            ("DARVOZA_POLICY_PATH", Path.Combine(AppContext.BaseDirectory, "fixtures", "policy.e2e.yaml")),
            ("DARVOZA_KEY_ANALYST", AnalystKey),
            ("DARVOZA_KEY_ENGINEER", EngineerKey),
        };

        var originals = env.Select(e => (e.Key, Value: Environment.GetEnvironmentVariable(e.Key))).ToArray();
        foreach (var (key, value) in env)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            _ = Services; // force EnsureServer() -> runs Program (reads the env) + ConfigureTestServices now
        }
        finally
        {
            foreach (var (key, value) in originals)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // 1. No upstream connect at startup — drop the hosted service that would spawn npx.
            var connectInitializer = services.SingleOrDefault(
                d => d.ImplementationType == typeof(UpstreamConnectionInitializer));
            if (connectInitializer is not null)
                services.Remove(connectInitializer);

            // 2. Fake the upstream LEAF + the audit sink; keep the real decorator chain and key provider.
            services.AddSingleton(Upstream);
            services.RemoveAll<IAuditSink>();
            services.AddSingleton<IAuditSink>(Audit);

            services.RemoveAll<IUpstreamToolClient>();
            services.AddSingleton<IUpstreamToolClient>(sp =>
                new AuditingToolClient(
                    new PolicyEnforcingToolClient(
                        sp.GetRequiredService<FakeUpstreamToolClient>(),
                        sp.GetRequiredService<Policy>(),
                        sp.GetRequiredService<ICallerKeyProvider>(),
                        sp.GetRequiredService<ICallDecisionContext>()),
                    sp.GetRequiredService<IAuditSink>(),
                    sp.GetRequiredService<ICallDecisionContext>(),
                    sp.GetRequiredService<TimeProvider>()));
        });
    }

    /// <summary>
    /// Connect a real MCP client to the in-memory gateway over streamable HTTP, presenting
    /// <paramref name="callerKey"/> as the <c>X-Darvoza-Key</c> header on every request (omit it to test
    /// the missing-header deny path). This is the same header mechanism a real MCP client uses.
    /// </summary>
    public async Task<McpClient> CreateMcpClientAsync(string? callerKey, CancellationToken ct = default)
    {
        var httpClient = CreateClient(); // routes to the in-memory TestServer; BaseAddress = http://localhost/
        var options = new HttpClientTransportOptions
        {
            Endpoint = httpClient.BaseAddress!,
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (callerKey is not null)
            options.AdditionalHeaders = new Dictionary<string, string>
            {
                [HttpHeaderCallerKeyProvider.HeaderName] = callerKey,
            };

        var transport = new HttpClientTransport(options, httpClient, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
