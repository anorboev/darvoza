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
//   dotnet run --project demo/tools/RogueCaller -- --tool wit_get_work_item --arg id=1

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

const string HeaderName = "X-Darvoza-Key";   // the gateway's caller-identity header (T3 contract)

var url = "http://localhost:5000/";
var keyEnv = "DARVOZA_KEY_ANALYST";
var tool = "wit_create_work_item";
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
        // --arg name=value, repeatable. Overrides the defaults below wholesale once any is given.
        case "--arg" when value is not null && value.Split('=', 2) is [var name, var v]:
            toolArgs[name] = v; i++; break;
        case "--help" or "-h":
            Console.WriteLine("usage: RogueCaller [--url URL] [--key-env ENV] [--tool NAME] [--title T] [--arg k=v]...");
            return 0;
        default:
            Console.Error.WriteLine($"unknown or malformed argument: {args[i]}");
            return 2;
    }
}

if (toolArgs.Count == 0)
{
    // Shape verified against @azure-devops/mcp 2.7.0: `workItemType` is a top-level string and the title
    // travels inside the required `fields` array as System.Title — there is no top-level `title` arg.
    // Override with --arg (or --title) if the public-preview upstream surface drifts. Note the analyst
    // path never gets this far: policy denies before upstream ever sees the arguments.
    toolArgs["project"] = "darvoza-demo";
    toolArgs["workItemType"] = "Task";
    toolArgs["fields"] = new[] { new Dictionary<string, object?> { ["name"] = "System.Title", ["value"] = title } };
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
    Endpoint = new Uri(url),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { [HeaderName] = key },
});

await using var client = await McpClient.CreateAsync(transport);

var result = await client.CallToolAsync(tool, toolArgs);

Console.WriteLine(result.IsError is true ? "DENIED (or upstream error) — gateway response:" : "ALLOWED — gateway response:");
foreach (var block in result.Content)
    Console.WriteLine("  " + (block is TextContentBlock text ? text.Text : block.Type));

Console.WriteLine();
Console.WriteLine("One audit line was written for this call either way — tail $DARVOZA_AUDIT_PATH.");
return result.IsError is true ? 1 : 0;
