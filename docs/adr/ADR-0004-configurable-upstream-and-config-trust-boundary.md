# ADR-0004 — Configurable upstream MCP server; the config file as a trust boundary

**Status:** Accepted · 2026-07-27 (A01-T7).
**Context source:** internal planning-workspace task 09 (upstream-agnostic gateway); amends the threat
model established in A01-T6a.
**Binding on:** A01-T7 and any later change to the upstream launch path.

## Context

Darvoza was built and demonstrated against Microsoft's official Azure DevOps MCP server, and the README
said so. But of the four moving parts, only one was ever Azure-DevOps-specific:

| Part | Coupled to Azure DevOps? |
|---|---|
| Front leg (streamable-HTTP MCP server) | No — a standard MCP server |
| Policy engine (ADR-0002) | No — operates on tool *names* at the decorator seam |
| Audit trail (ADR-0003) | No — records tool names, roles, arg digests |
| **Upstream launch** | **Yes** — a pinned `@azure-devops/mcp@2.7.0` package spec and a fixed argv |

So the claim "a governance gateway for any MCP server" was already three-quarters true and entirely
unverifiable. Making it true is a small, well-isolated change — with one sharp edge.

**The sharp edge.** A01-T6a exists *because* launching an external process with influenced argv is
dangerous. It closed G-10 #1 by removing `npx.cmd` (a cmd.exe batch script, whose .NET argument escaping
has known gaps) from the launch path on Windows, launching `node npx-cli.js …` directly instead. Letting
an operator choose the upstream command must not hand back arbitrary code execution through the front
door that gate just closed.

## Decision

### 1. The upstream is selected by an optional `upstream:` section in the policy config file

```yaml
upstream:
  profile: azure-devops          # built-in; the default when the section is absent
# --- or, explicit opt-in to any other MCP server ---
# command: node
# args: ["/srv/my-mcp-server/index.js", "--readonly"]
```

`profile` and `command` are **mutually exclusive** (both present → startup failure). An absent section
resolves to the `azure-devops` profile, so a config file written before A01-T7 behaves identically.

`ADO_ORG` and the PAT are required **only** by the `azure-devops` profile — an Azure DevOps organization
is meaningless for a server that is not Azure DevOps.

### 2. Config file only — never environment, header, query string, or body

This is the load-bearing constraint. An environment variable was considered and **rejected**: env is
ambient, inherited by child processes, visible in process listings on some platforms, and settable by
anything that can launch the gateway. A file is a single artifact an operator can review, diff, and
permission. Headers and request bodies are closed **structurally** rather than by a check — the launch
spec is resolved before the web host is built, so no request exists yet to influence it.

### 3. `args` is a list. A single string is rejected, never split

Word-splitting a command line is precisely what a shell does. Darvoza does not do it anywhere, so an
operator who writes `args: "server.js --readonly"` gets a startup failure with an explanation, not a
silently different launch. Argv stays an `IReadOnlyList<string>` from the config file through
`UpstreamLaunchSpec` into the process launcher — never joined, never split.

### 4. Allow-list entries the upstream does not offer are a WARNING, not fatal

Startup cross-checks the union of every role's allow-list against the connected upstream's `tools/list`
and logs one warning per absent name.

Fail-fast is the right posture where the failure mode is starting **open** — a missing policy, an unset
caller key, an unreachable upstream all still abort startup. Here the failure mode is the opposite:
deny-by-default makes an allow-listed-but-absent tool inert, so the gateway is merely *more closed* than
intended. Upstream tool names have already drifted twice in this project (see the notes in
`policy.example.yaml`), and taking a governance gateway down on a benign version bump would be a worse
outcome than the misconfiguration being reported. The operator gets the diff; the gateway keeps serving.

Only that direction is reported. Upstream tools absent from every allow-list are deny-by-default working
as designed — the normal state for a server offering 90 tools to a role allowed six.

## Amended threat model

### The config file is a trust boundary equal to the rest of the policy file

Whoever can edit `policy.yaml` can already define a role whose allow-list contains **every** upstream
tool and bind a caller key to it. Their authority over Darvoza's decisions is already total; adding
`upstream.command` does not widen it.

Being precise where that argument is loose: arbitrary tool calls are **not** literally arbitrary code
execution, and this ADR does not claim they are. The sound form is deployment-shaped — in every
deployment Darvoza supports (single-tenant, operator-run, loopback / trusted network), the party who can
write the config file in the gateway's working directory can also write the gateway's binaries, its
`.env`, or its service definition, each of which already yields code execution. **This change makes an
existing boundary explicit; it does not create one.**

**The residual case it does not cover**, stated plainly because it is real: a deployment where the config
file is writable by a party who *cannot* write the gateway's install directory — a config-management
agent with a narrower ACL, a shared operations volume. There, config-driven process launch **is** a
privilege escalation.

The mitigation is an operator responsibility, and it covers **three** inputs, not one — because each of
them selects the file that supplies the upstream command:

1. **The policy file itself.** Keep it owner-writable only, with the same care as the binary.
2. **`DARVOZA_POLICY_PATH`.** It chooses which file is read, and `DotEnvLoader` will set it from a `.env`
   discovered by walking up from the working directory, with no key allowlist.
3. **The working directory.** A `policy.local.yaml` dropped there takes precedence over `policy.yaml`.

### Environment isolation for a configured upstream

The gateway's own environment holds every `DARVOZA_KEY_*` caller key and `DARVOZA_FINGERPRINT_SALT`. A
child that receives those could **authenticate back into Darvoza's front leg as any role** and
de-anonymize the audit trail — defeating the guarantee the gateway exists to provide. So a configured
upstream does **not** inherit it (`InheritEnvironmentVariables = false`): it starts from the SDK's curated
default environment (`PATH`, `HOME`, system directories) plus exactly the variables named in
`upstream.passEnv`. As with a caller's `keyEnv`, the config file carries variable **names**, never values,
and an unset one fails startup rather than launching half-configured.

The built-in `azure-devops` profile still inherits, unchanged from before A01-T7 — it is the pinned,
trusted package, and that is how the PAT reaches it. **Known consequence, pre-existing and not fixed
here:** the official Azure DevOps server therefore also sees the caller keys and the fingerprint salt.
Narrowing that would change the demo's launch environment and belongs to its own task.

### What the pinned SDK does at the spawn boundary (corrects an A01-T6a claim)

A01-T6a concluded that launching `node npx-cli.js …` instead of `npx.cmd` removed the shell from the
Windows launch path. **That conclusion was wrong**, and this ADR corrects it rather than repeating it.

Verified by decompiling the pinned `ModelContextProtocol.Core` 1.4.0,
`StdioClientTransport.ConnectAsync`:

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
    !string.Equals(Path.GetFileName(command), "cmd.exe", StringComparison.OrdinalIgnoreCase))
{
    args = ["/c", command, ..args];
    command = "cmd.exe";
}
```

On Windows the SDK rewrites **every** launch — `node` included — to `cmd.exe /c <command> <args…>`,
applying its own caret-escaping (`EscapeArgumentString`, pattern `[&^><|]`) to each element. So:

- **A shell does re-parse argv on Windows**, one layer below Darvoza. Bypassing `npx.cmd` avoided a
  *second* re-parse (the batch file's own), which is a real improvement, but it did not remove cmd.exe.
- **On Windows the strict `ADO_ORG` allowlist is therefore load-bearing, not defense-in-depth.** A01-T6a
  and the code comments demoted it; that demotion is retracted here for Windows. On non-Windows the SDK
  spawns directly and the demotion holds.
- The gateway logs this at startup rather than leaving it implicit, and the guidance for
  `upstream.args` is to avoid cmd.exe metacharacters.

This is a **pre-existing property of the pinned SDK, not something A01-T7 introduced** — but A01-T7 both
restates the claim and adds operator-controlled argv to the path, so it is corrected here. **Whether this
reopens G-10 #1 as a gate is a call for the project owner**, recorded on PR #14 rather than decided by
the implementing agent.

### What A01-T7 does and does not change about that surface

Preserved exactly:

1. **Darvoza never builds a command line.** Argv is an `IReadOnlyList<string>` from the config file
   through `UpstreamLaunchSpec` into `StdioClientTransportOptions.Arguments`; nothing in this repository
   joins or splits it. A single-string `args` is rejected rather than word-split.
2. **The `azure-devops` profile still launches `node npx-cli.js …`**, never `npx.cmd`, so it still avoids
   the batch file's own re-parse.

What changed is only *who supplies* argv, and it moved from partly environment-derived (`ADO_ORG`) to an
operator config file — a **narrower** input source than before, not a wider one. Since both the command
and its arguments now come from the same trusted file, no untrusted input reaches the cmd.exe re-parse on
the configured-upstream path.

### Known properties an operator should know

- **Credentials belong in `upstream.passEnv`, never in `upstream.args`.** The resolved argv is logged once
  at startup, so a secret written into `args` lands in the logs. This is a deliberate trade: the argv line
  is the only way an operator can answer "which server is this gateway actually in front of?" from the
  log, and the guidance is stated in the ADR, the README, and `policy.example.yaml`.
- **On Windows, every launch passes through `cmd.exe /c`** — a property of the pinned SDK, not of the
  command. Startup says so. See the SDK section above.

## Consequences

- The README leads with the general claim; Azure DevOps remains the demonstrated case, the default
  profile, and the only profile Darvoza ships.
- **One upstream per instance.** Multi-server federation stays roadmap, not scope.
- `dotnet test` no longer requires Node on `PATH`: the e2e fixture configures its own upstream, so the
  launch resolver does no node/npx discovery when booting the real composition root.
- Policy and audit semantics are untouched — deny-by-default, exactly one audit record per call,
  fail-closed writes, and redaction all behave exactly as before.

## Rejected alternatives

- **Named built-in profiles only.** Safest, and useless for the purpose: someone evaluating Darvoza
  against *their* MCP server could not do so without a code change and a release.
- **Arbitrary `command` + `args` with no profiles.** Loses the zero-config demo path, and would push the
  Azure-DevOps-specific parts (the npx/node batch-file hardening, the base64 PAT encoding) into every
  operator's config file, where they would be copy-pasted wrong.
- **`DARVOZA_UPSTREAM_COMMAND` environment variable.** Rejected — see decision 2.

## See also

- `src/Darvoza.Gateway/Configuration/UpstreamOptions.cs`, `Configuration/PolicyLoader.cs` (validation),
  `Upstream/UpstreamLaunch.cs` (resolution), `Upstream/UpstreamPolicyCheck.cs` (the startup diagnostic).
- ADR-0002 (decorator seam — why the policy engine never needed to know the upstream),
  ADR-0003 (audit trail — likewise).
