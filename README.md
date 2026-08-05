# paperq

`paperq` is a small, repository-local queue for the tiny problems coding agents encounter while doing real work. It writes transparent Markdown files; it does not run a model, collect transcripts, start a service, or send anything to a server.

The project targets .NET 10 and publishes as Native AOT executables for:

- `win-x64`
- `linux-x64`
- `linux-arm64`

Native AOT keeps each release standalone without bundling a conventional self-contained .NET deployment or requiring a .NET runtime on the target machine.

## Queue layout

```text
.papercuts/
  open/
  working/
  blocked/
  resolved/
```

Each papercut is one Markdown file. Its directory is its current state. `next --claim` takes an exclusive handle and atomically moves the oldest open record into `working`, so concurrent local agents cannot claim the same item.

The concurrency guarantee covers processes sharing one local filesystem. Git merges and cloud-sync conflicts between separate computers are outside the v1 guarantee.

## Commands

```text
paperq init [--append-agents] [--gitignore]
paperq add "message"
paperq add --stdin
paperq list [--all]
paperq next [--claim]
paperq resolve <id> --note "evidence"
paperq block <id> --reason "reason"
paperq reopen <id>
```

Every command accepts these global options:

```text
--root <path>  Use this exact directory instead of discovering a Git root.
--json         Write the versioned JSON contract to stdout.
```

Without `--root`, `paperq` walks upward from the current directory to find the nearest `.git` directory or file. If it finds none, it uses the current directory. An explicit `--root` is always used exactly as supplied.

`list` shows `open`, `working`, and `blocked` records by default. `list --all` also includes resolved records. Selection is oldest-first using the UTC creation time and then the ID as a deterministic tie-breaker.

## Initialization

`paperq init` creates only the queue directories automatically. In an interactive terminal it also:

1. prints a paste-ready `AGENTS.md` section;
2. asks whether to create or append the root `AGENTS.md`, defaulting to No;
3. when the selected root is a Git repository, asks whether to add `.papercuts/` to `.gitignore`, defaulting to No.

When input or output is redirected, or `--json` is used, `init` does not prompt and does not modify `AGENTS.md` or `.gitignore`. Automation can request those changes explicitly with `--append-agents` and `--gitignore`. Both edits are idempotent, and `--gitignore` is rejected outside a Git repository.

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

## Input and safety

- Messages, resolution notes, and block reasons must be non-empty valid Unicode.
- Each input is limited to 64 KiB after UTF-8 encoding.
- NUL characters and the internal history marker are rejected.
- Managed records are capped at 1 MiB to avoid unbounded reads after manual corruption.
- Queue state directories and managed files may not be symbolic links or junctions.
- `paperq` performs no automatic secret redaction. Agent instructions explicitly prohibit secrets, credentials, full transcripts, and large raw output.

## JSON and exit codes

JSON output uses a stable, versioned envelope and stays on stdout, including structured errors. Human diagnostics go to stderr.

```json
{"schemaVersion":1,"ok":true,"command":"add","data":{"id":"20260805T142301123Z-a7f3c921de","state":"open","created":"2026-08-05T14:23:01.1230000+00:00","message":"Example","path":".papercuts/open/20260805T142301123Z-a7f3c921de.md"}}
```

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

Publish Native AOT on the matching operating system:

```powershell
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime win-x64
```

```bash
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime linux-x64
dotnet publish src/Paperq/Paperq.csproj --configuration Release --runtime linux-arm64
```

Cross-operating-system Native AOT publishing is not supported. The Windows binary must be produced on Windows; the Linux binaries must be produced on Linux with the appropriate native toolchain.
