# paperq

`paperq` is a small, repository-local queue for the tiny problems coding agents encounter while doing real work. It writes transparent Markdown files and promotes resolved problems into a project-level solution journal; it does not run a model, collect transcripts, start a service, or send anything to a server.

The project targets .NET 10 and publishes as Native AOT executables.

## Installation

### Windows

`paperq` is available on WinGet:

```powershell
winget install Vathivis.paperq
```

### Linux

Download the `.deb` matching the machine architecture from [GitHub Releases](https://github.com/Vathivis/paperq/releases), then install it with APT:

```bash
sudo apt install ./paperq_*_amd64.deb   # x86-64
sudo apt install ./paperq_*_arm64.deb   # ARM64
```

The package installs the standalone executable as `/usr/bin/paperq`; it does not require a separate .NET runtime.

## Queue layout

```text
PAPERQ_RESOLUTIONS.md
.papercuts/
  open/
  working/
  blocked/
  resolved/
```

Each papercut is one Markdown file. Its directory is its current state. `next --claim` takes an exclusive handle and atomically moves the oldest open record into `working`, so concurrent local agents cannot claim the same item.

`PAPERQ_RESOLUTIONS.md` is created lazily by the first successful `resolve`. Every resolution adds the original problem, the resolution note, the journal recording time, and a link to the detailed record in `.papercuts/resolved`. The root-level journal is not covered by the optional `.papercuts/` ignore rule, so it can be committed and shared as durable project knowledge. Existing v0.1 resolved records are not backfilled automatically because their free-form history has no unambiguous machine-readable resolution boundary.

The queue and resolution-journal concurrency guarantees cover processes sharing one local filesystem. Git merges and cloud-sync conflicts between separate computers are outside the guarantee.

## Commands

```text
paperq init [--append-agents] [--gitignore]
paperq add "message"
paperq add --stdin
paperq list [--all]
paperq show <id>
paperq next [--claim]
paperq resolve <id> --note "evidence"
paperq resolve <id> --stdin
paperq block <id> --reason "reason"
paperq block <id> --stdin
paperq reopen <id>
```

Every command accepts these global options:

```text
--root <path>  Use this exact directory instead of discovering a Git root.
--json         Write the versioned JSON contract to stdout.
```

Global options may appear before or after a command.

Without `--root`, `paperq` walks upward from the current directory to find the nearest `.git` directory or file. If it finds none, it uses the current directory. An explicit `--root` is always used exactly as supplied.

## Capture and maintenance workflows

### Capture during normal work

PaperQ's default agent workflow is deliberately one-way: when an agent encounters small, non-blocking friction, it records the papercut with `add`, ends the papercut-capture side-track, and immediately returns to its original task. The papercut remains `open`. A normal working agent does not claim, investigate, resolve, block, reopen, or delegate it unless the user or project-specific instructions explicitly request that behavior.

This keeps incidental friction from expanding the current task and preserves the queue for a later focused maintenance pass.

### Dedicated maintenance pass

When an agent is explicitly assigned papercut maintenance, it acts as the resolver:

1. read `PAPERQ_RESOLUTIONS.md` if it exists, so verified approaches are not repeated;
2. run `paperq list` to understand the active queue;
3. claim the oldest open item with `paperq next --claim`;
4. inspect its full message and history with `paperq show <id>`;
5. investigate and fix the underlying friction;
6. record verified evidence with `resolve`, or use `block` with a concrete reason when work cannot proceed;
7. repeat until no open papercuts remain.

Resolution notes and block reasons can be supplied inline or read from standard input with `--stdin`.

### Optional parallel resolution

PaperQ stores queue state; it does not launch models or agents. An environment that supports subagents may use separate user or project instructions to dispatch a resolver after `add` while the main agent continues its task. That external orchestration policy owns the resolver model, reasoning effort, permissions, concurrency, and write coordination.

| Command | State behavior |
|---|---|
| `add` | Creates an `open` papercut. |
| `next` | Previews the oldest open papercut without changing it. |
| `next --claim` | Atomically moves the oldest open papercut to `working`. |
| `show <id>` | Shows the full message and lifecycle history from any state. |
| `resolve` | Moves a `working` papercut to `resolved` and updates the resolution journal. |
| `block` | Moves a `working` papercut to `blocked`. |
| `reopen` | Moves a `working`, `blocked`, or `resolved` papercut back to `open`. |

`list` shows `open`, `working`, and `blocked` records by default. `list --all` also includes resolved records. Selection is oldest-first using the UTC creation time and then the ID as a deterministic tie-breaker.

## Initialization

`paperq init` creates only the queue directories automatically. Without `--json`, it always prints paste-ready `AGENTS.md` instructions and a short resolution-journal reference. In an interactive terminal it also:

1. asks whether to create or append the root `AGENTS.md`, defaulting to No;
2. when the selected root is a Git repository, asks whether to add `.papercuts/` to `.gitignore`, defaulting to No.

When input or output is redirected, or `--json` is used, `init` does not prompt and does not modify `AGENTS.md` or `.gitignore`. Automation can request those changes explicitly with `--append-agents` and `--gitignore`. Both edits are idempotent, and `--gitignore` is rejected outside a Git repository. `--append-agents` preserves custom instructions and appends only PaperQ's managed blocks; running it against an older PaperQ block adds the missing journal reference without duplicating or rewriting existing content.

### Copy-ready `AGENTS.md` instructions

Paste this block into the repository's root `AGENTS.md`, or let `paperq init` add it for you:

```markdown
<!-- paperq:agent-instructions:start -->
## Papercuts

During normal work, record small, non-blocking friction with `paperq add "<concise message>"` or `paperq add --stdin` without resolving it; after the record is added, end the papercut-capture side-track and continue the main task. Examples include dead-end tool calls, broken links, flaky commands, stale caches, confusing errors, and undocumented setup.

Keep each papercut to one or two sentences. Include a suspected cause or fix only when useful. Never log secrets, credentials, full transcripts, or large raw output.

When explicitly assigned papercut maintenance, read `PAPERQ_RESOLUTIONS.md` if it exists, then process the queue one item at a time with `paperq list`, `paperq next --claim`, and `paperq show <id>`. Investigate the claimed item, use `paperq resolve <id> --note "<verified solution>"` when fixed or `paperq block <id> --reason "<reason>"` when it cannot proceed, then continue until no open papercuts remain.
<!-- paperq:agent-instructions:end -->

<!-- paperq:resolutions-reference:start -->
If `PAPERQ_RESOLUTIONS.md` exists, read it before retrying recurring project-specific friction. PaperQ creates it after the first successful `resolve`.
<!-- paperq:resolutions-reference:end -->
```

## Record format

IDs contain a sortable UTC timestamp plus random entropy:

```text
20260805T142301123Z-a7f3c921de
```

A new record looks like this:

```markdown
# Papercut

ID: 20260805T142301123Z-a7f3c921de
Created: 2026-08-05T14:23:01.1230000+00:00

## Message

The setup command fails until the stale cache is removed.

<!-- paperq:events -->
## History
```

Lifecycle commands append human-readable `Blocked`, `Reopened`, or `Resolved` entries below `History`. The state remains defined by the containing directory, not duplicated as mutable metadata.

Resolving the example creates or extends this separate project journal:

```markdown
<!-- paperq:resolutions -->
# PaperQ Resolutions

Project-specific solutions captured when papercuts are resolved.

<!-- paperq:resolution:20260805T142301123Z-a7f3c921de:1a2b3c4d5e6f7788 -->
## 20260805T142301123Z-a7f3c921de

Recorded: 2026-08-05T15:04:12.0000000+00:00
Papercut: [.papercuts/resolved/20260805T142301123Z-a7f3c921de.md](.papercuts/resolved/20260805T142301123Z-a7f3c921de.md)

### Problem

The setup command fails until the stale cache is removed.

### Resolution

Added a cache cleanup step to the setup script and verified it from a clean shell.
```

The resolved record remains the full audit trail. `PAPERQ_RESOLUTIONS.md` is the concise lookup future agents are told to read. Repeating the same resolution note after an interrupted journal update is safe and does not duplicate the entry.

## Input and safety

- Messages, resolution notes, and block reasons must be non-empty valid Unicode.
- Each input is limited to 64 KiB after UTF-8 encoding.
- NUL characters and the internal history marker are rejected.
- Managed records are capped at 1 MiB to avoid unbounded reads after manual corruption.
- The managed resolution journal is capped at 16 MiB.
- Queue state directories and managed files may not be symbolic links or junctions.
- Resolution notes and original messages are copied into the root-level journal, which is intended to be committed. `paperq` performs no automatic secret redaction. Agent instructions explicitly prohibit secrets, credentials, full transcripts, and large raw output.
- If `PAPERQ_RESOLUTIONS.md` already exists without PaperQ's marker, `resolve` refuses to modify it and leaves the papercut in `working`.

## JSON and exit codes

JSON output uses a stable, versioned envelope and stays on stdout, including structured errors. Human diagnostics go to stderr.

```json
{"schemaVersion":1,"ok":true,"command":"add","data":{"id":"20260805T142301123Z-a7f3c921de","state":"open","created":"2026-08-05T14:23:01.1230000+00:00","message":"Example","path":".papercuts/open/20260805T142301123Z-a7f3c921de.md"}}
```

Successful `resolve --json` responses retain the record fields and add `"resolutionPath":"PAPERQ_RESOLUTIONS.md"`. Successful `show --json` responses add the raw lifecycle `history` text.

| Exit code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Unexpected I/O, access, or internal error |
| 2 | Invalid command usage |
| 3 | Queue not initialized |
| 4 | Record not found or no open work |
| 5 | State or concurrency conflict |
| 6 | Invalid input, record, or queue data |

## Build and test

The production project has no NuGet package dependencies. The executable test harness also uses only the .NET SDK and runs with:

```powershell
dotnet build paperq.slnx --configuration Release
dotnet run --project tests/Paperq.Tests/Paperq.Tests.csproj --configuration Release
```

Publish Native AOT on the matching operating system. Windows targets require Windows, while Linux targets require Linux and the native toolchain:

```powershell
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime win-x64
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime win-arm64
```

```bash
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime linux-x64
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime linux-arm64
```
