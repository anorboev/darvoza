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

using System.Globalization;
using System.Text;
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
        // A recognized flag that ran out of value — say so, rather than calling it "unknown" below.
        case "--url" or "--key-env" or "--tool" or "--title":
            Console.Error.WriteLine($"{args[i]} needs a value.");
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
    Console.Error.WriteLine($"--tool {Printable(tool)} needs its own arguments: pass --arg name=value (repeatable).");
    return 2;
}

// --key-env names an ENV VAR, never a key. Constrain it to the gateway's own DARVOZA_KEY_* namespace:
// it stops `--key-env AZURE_DEVOPS_EXT_PAT` from turning this into a PAT exfiltrator, and it means a
// shell-expanded fat-finger (`--key-env $DARVOZA_KEY_ANALYST`) is rejected WITHOUT echoing the key.
if (!keyEnv.StartsWith("DARVOZA_KEY_", StringComparison.Ordinal) || !keyEnv.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
{
    Console.Error.WriteLine("--key-env must be the NAME of a DARVOZA_KEY_* environment variable, not a key value.");
    return 2;
}

if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
    || endpoint.Scheme is not (("http") or ("https"))   // `file:` has an empty host, which IsLoopback calls loopback
    || endpoint.UserInfo.Length > 0)                    // userinfo would put a password in the banner below
{
    Console.Error.WriteLine("--url must be an absolute http(s) URL with no embedded credentials.");
    return 2;
}

// The caller key rides on EVERY request to whatever --url names, so this stays a loopback-only tool: the
// gateway it demos is a localhost process. That keeps a mistyped or pasted-from-anywhere URL from handing a
// live credential to a third party. Redirects are disabled below for the same reason — .NET strips
// Authorization across hosts but NOT custom headers, so a 302 would forward X-Darvoza-Key verbatim.
if (!endpoint.IsLoopback)
{
    Console.Error.WriteLine($"refusing to send {HeaderName} to non-loopback host '{endpoint.Host}'. This tool only targets a local gateway.");
    return 2;
}

var key = Environment.GetEnvironmentVariable(keyEnv);
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine($"{keyEnv} is not set. Export it in THIS shell (the same one that ran the gateway's setup).");
    return 2;
}

Console.WriteLine($"rogue caller -> {endpoint.GetLeftPart(UriPartial.Path)}");
Console.WriteLine($"  identity : {HeaderName} from ${keyEnv}   (value never printed)");
Console.WriteLine($"  tool     : {Printable(tool)}   (calling it directly — tools/list is deliberately NOT requested)");
Console.WriteLine($"  args     : {Printable(string.Join(", ", toolArgs.Keys))}");
Console.WriteLine();

// AllowAutoRedirect=false: a redirect off the loopback host would re-send X-Darvoza-Key to the new host.
var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
var transport = new HttpClientTransport(
    new HttpClientTransportOptions
    {
        Endpoint = endpoint,
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { [HeaderName] = key },
    },
    http,
    ownsHttpClient: true);

CallToolResult result;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // fail fast, never hang on camera
var stage = $"reach the gateway at {endpoint.GetLeftPart(UriPartial.Path)}";  // narrows to the call once connected
try
{
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    stage = $"complete the {Printable(tool)} call";
    result = await client.CallToolAsync(tool, toolArgs, cancellationToken: timeout.Token);
}
catch (Exception ex)
{
    // A stack trace mid-shot would expose local paths and derail the take; one clean line instead. The
    // message can carry SERVER-supplied text (a JSON-RPC error string), so it is sanitized like any other
    // upstream content.
    var why = timeout.IsCancellationRequested ? "timed out after 30s" : Printable(ex.Message);
    Console.Error.WriteLine($"could not {stage}: {why}");
    Console.Error.WriteLine("Is the gateway running, and is this the same shell that has the key env vars?");
    return 2;
}

// The verdict line is derived from result.IsError (a bool), never from upstream text, so it cannot be
// spoofed — and every upstream line below is indented, so upstream content can never start at column 0
// and forge a line of this program's own output.
Console.WriteLine(result.IsError is true ? "DENIED (or upstream error) — gateway response:" : "ALLOWED — gateway response:");
foreach (var block in result.Content)
    foreach (var line in Printable(block is TextContentBlock text ? text.Text : block.Type).Split('\n'))
        Console.WriteLine("  " + line);

Console.WriteLine();
Console.WriteLine("One audit line was written for this call either way — tail $DARVOZA_AUDIT_PATH.");
return result.IsError is true ? 1 : 0;

// Upstream content is attacker-influenced (an Azure DevOps work-item field can carry anything). Replace
// control characters — ANSI/CR sequences that would repaint the verdict above — and Unicode FORMAT
// characters (bidi overrides, zero-width), which are category Cf and slip past the control check. Length
// is capped so a wall of text can't scroll the verdict off screen. Enumerating RUNES rather than chars
// keeps non-BMP format characters from slipping through as surrogate halves; CRLF is normalized first so
// legitimate Windows line endings don't each render as a replacement mark.
static string Printable(string s) => string.Concat(
    (s.Length > 2000 ? s[..2000] + " …[truncated]" : s).ReplaceLineEndings("\n")
    .EnumerateRunes()
    .Select(r => r.Value is '\n' or '\t' ? r.ToString()
        : Rune.IsControl(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format ? "�"
        : r.ToString()));
