// Darvoza demo — the ROGUE CALLER.
//
// Why this exists: Darvoza filters `tools/list` per role, so a well-behaved MCP client never even
// attempts a disallowed tool — it just reports the tool isn't available. That is the gateway working,
// but it means the call-level DENY can't be produced from the client, on camera.
//
// This is a deliberately impolite client: it SKIPS `tools/list` entirely and issues `tools/call` for a
// tool it was never offered. The gateway denies it anyway (deny-by-default is enforced on the call, not
// just on the listing) and writes exactly one `deny` audit line. Run it with the engineer key and the
// same invocation is allowed and audited — the terminal-only fallback path for the demo.
//
// It never prints the caller key or the PAT. Keys come from the environment only.
//
//   dotnet run --project demo/tools/RogueCaller
//   dotnet run --project demo/tools/RogueCaller -- --key-env DARVOZA_KEY_ENGINEER
//   dotnet run --project demo/tools/RogueCaller -- --tool wit_update_work_item --arg id=1

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

const string HeaderName = "X-Darvoza-Key";   // the gateway's caller-identity header (T3 contract)
const string DefaultTool = "wit_create_work_item";

var url = "http://localhost:5000/";
var keyEnv = "DARVOZA_KEY_ANALYST";
var tool = DefaultTool;
var title = "Rogue attempt";
var toolArgs = new Dictionary<string, object?>();

for (var i = 0; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--url" when value is not null: url = value; i++; break;
        case "--key-env" when value is not null: keyEnv = value; i++; break;
        case "--tool" when value is not null: tool = value; i++; break;
        case "--title" when value is not null: title = value; i++; break;
        // --arg name=value, repeatable. Supplying any --arg replaces the default argument set wholesale
        // (so --title is then ignored — pass the title inside your own `fields` instead).
        case "--arg" when value is not null && value.Split('=', 2) is [var name, var v]:
            toolArgs[name] = v; i++; break;
        case "--arg":
            Console.Error.WriteLine("--arg needs the form name=value.");
            return 2;
        case "--help" or "-h":
            Console.WriteLine("usage: RogueCaller [--url URL] [--key-env ENV] [--tool NAME] [--title T] [--arg k=v]...");
            return 0;
        default:
            // Report the POSITION, never the token: a fat-fingered `RogueCaller -- $DARVOZA_KEY_ANALYST`
            // would otherwise echo the raw caller key to a terminal that is being recorded.
            Console.Error.WriteLine($"unknown or malformed argument at position {i + 1}. Try --help.");
            return 2;
    }
}

// Defaults are wit_create_work_item's arguments, so only apply them for THAT tool — otherwise
// `--tool something_else` would silently ship create-shaped args and draw a confusing upstream schema
// error instead of the clean allow/deny the script exists to show.
if (toolArgs.Count == 0 && tool == DefaultTool)
{
    // Shape verified against @azure-devops/mcp 2.7.0: `workItemType` is a top-level string and the title
    // travels inside the required `fields` array as System.Title — there is no top-level `title` arg.
    // Override with --arg (or --title) if the public-preview upstream surface drifts. Note the analyst
    // path never gets this far: policy denies before upstream ever sees the arguments.
    toolArgs["project"] = "darvoza-demo";
    toolArgs["workItemType"] = "Task";
    toolArgs["fields"] = new[] { new Dictionary<string, object?> { ["name"] = "System.Title", ["value"] = title } };
}
else if (toolArgs.Count == 0)
{
    Console.Error.WriteLine($"--tool {tool} needs its own arguments: pass --arg name=value (repeatable).");
    return 2;
}

if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
{
    Console.Error.WriteLine($"--url is not an absolute URL: {url}");
    return 2;
}

// The caller key rides on EVERY request to whatever --url names. Refuse to hand a live credential to a
// non-loopback host over plaintext http: a mistyped or pasted-from-anywhere URL would otherwise exfiltrate
// it, and on a real network it would also be on the wire in cleartext.
if (!endpoint.IsLoopback && endpoint.Scheme != Uri.UriSchemeHttps)
{
    Console.Error.WriteLine(
        $"refusing to send {HeaderName} to non-loopback host '{endpoint.Host}' over {endpoint.Scheme}. Use https for a remote gateway.");
    return 2;
}

var key = Environment.GetEnvironmentVariable(keyEnv);
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine($"{keyEnv} is not set. Export it in THIS shell (the same one that ran the gateway's setup).");
    return 2;
}

Console.WriteLine($"rogue caller -> {url}");
Console.WriteLine($"  identity : {HeaderName} from ${keyEnv}   (value never printed)");
Console.WriteLine($"  tool     : {tool}   (calling it directly — tools/list is deliberately NOT requested)");
Console.WriteLine($"  args     : {string.Join(", ", toolArgs.Keys)}");
Console.WriteLine();

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = endpoint,
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { [HeaderName] = key },
});

CallToolResult result;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // fail fast, never hang on camera
try
{
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    result = await client.CallToolAsync(tool, toolArgs, cancellationToken: timeout.Token);
}
catch (Exception ex)
{
    // A stack trace mid-shot would expose local paths and derail the take; one clean line instead.
    Console.Error.WriteLine($"could not reach the gateway at {endpoint}: {ex.Message}");
    Console.Error.WriteLine("Is it running, and is this the same shell that has the key env vars?");
    return 2;
}

Console.WriteLine(result.IsError is true ? "DENIED (or upstream error) — gateway response:" : "ALLOWED — gateway response:");
foreach (var block in result.Content)
    Console.WriteLine("  " + Printable(block is TextContentBlock text ? text.Text : block.Type));

Console.WriteLine();
Console.WriteLine("One audit line was written for this call either way — tail $DARVOZA_AUDIT_PATH.");
return result.IsError is true ? 1 : 0;

// The response body is upstream-controlled (an Azure DevOps work-item field can carry anything). Strip
// control characters so a crafted payload can't emit ANSI/CR sequences that repaint the lines above —
// i.e. forge "ALLOWED" over the gateway's "DENIED" in the one frame the demo's credibility rests on.
static string Printable(string s) =>
    string.Concat(s.Select(c => c is '\n' or '\t' || !char.IsControl(c) ? c : '�'));
