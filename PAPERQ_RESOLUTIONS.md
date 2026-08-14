<!-- paperq:resolutions -->
# PaperQ Resolutions

Project-specific solutions captured when papercuts are resolved. Check this file before repeating a failed approach. Detailed lifecycle history remains in the linked resolved records.

<!-- paperq:resolution:20260814T073042762Z-f14ba17ec8:f4def0e8d65446da -->
## 20260814T073042762Z-f14ba17ec8

Recorded: 2026-08-14T07:34:50.6692200+00:00
Papercut: [.papercuts/resolved/20260814T073042762Z-f14ba17ec8.md](.papercuts/resolved/20260814T073042762Z-f14ba17ec8.md)

### Problem

There is no show-by-ID command: list truncates messages, so inspecting a working or blocked papercut requires opening its Markdown file manually.

### Resolution

Added paperq show <id> across every queue state with full message and lifecycle history output.
Verified the Release build and exercised human and JSON output against this repository's claimed papercut.

<!-- paperq:resolution:20260814T073042795Z-a33aed0db9:8ef071423f2d68e4 -->
## 20260814T073042795Z-a33aed0db9

Recorded: 2026-08-14T07:35:48.7668719+00:00
Papercut: [.papercuts/resolved/20260814T073042795Z-a33aed0db9.md](.papercuts/resolved/20260814T073042795Z-a33aed0db9.md)

### Problem

Resolve and block accept lifecycle text only as command-line arguments, unlike add --stdin; multiline evidence and reasons are awkward to provide safely.

### Resolution

Added bounded --stdin input for resolve notes and block reasons while retaining the existing inline options; the executable harness verifies multiline input, journal output, and conflicting-option rejection.

<!-- paperq:resolution:20260814T073042835Z-d3f4b55292:b3005de43d4446a5 -->
## 20260814T073042835Z-d3f4b55292

Recorded: 2026-08-14T07:36:48.5108898+00:00
Papercut: [.papercuts/resolved/20260814T073042835Z-d3f4b55292.md](.papercuts/resolved/20260814T073042835Z-d3f4b55292.md)

### Problem

Global --json and --root options are order-sensitive for help and version even though the README calls them global options accepted by every command.

### Resolution

Changed help and version parsing so --json and --root are consumed before or after the command/topic; verified help --json add and --version --json through the 1.0.0 executable and regression tests.

<!-- paperq:resolution:20260814T073042868Z-6ab451b407:3917b535da1a1db3 -->
## 20260814T073042868Z-6ab451b407

Recorded: 2026-08-14T07:36:55.0438496+00:00
Papercut: [.papercuts/resolved/20260814T073042868Z-6ab451b407.md](.papercuts/resolved/20260814T073042868Z-6ab451b407.md)

### Problem

The generated AGENTS resolution reference points to PAPERQ_RESOLUTIONS.md before the lazily created file exists, producing a confusing dead link in a freshly initialized repository.

### Resolution

Made the generated and repository AGENTS guidance conditional: read PAPERQ_RESOLUTIONS.md only if it exists, with an explicit note that the first successful resolve creates it. Updated the copy-ready README block and verified init output in the executable harness.

<!-- paperq:resolution:20260814T073042900Z-35148ee6e1:d68111eab3533db3 -->
## 20260814T073042900Z-35148ee6e1

Recorded: 2026-08-14T07:37:00.8676869+00:00
Papercut: [.papercuts/resolved/20260814T073042900Z-35148ee6e1.md](.papercuts/resolved/20260814T073042900Z-35148ee6e1.md)

### Problem

Block validates its reason before checking initialization and ID, so its error precedence differs from resolve and the other lifecycle commands.

### Resolution

Changed block to require an initialized queue and valid papercut ID before validating the reason, matching resolve and transition error precedence. Added a regression test proving both commands return not_initialized first.

<!-- paperq:resolution:20260814T073042933Z-c3a0b60114:533e6d866c33bb97 -->
## 20260814T073042933Z-c3a0b60114

Recorded: 2026-08-14T07:39:02.6544932+00:00
Papercut: [.papercuts/resolved/20260814T073042933Z-c3a0b60114.md](.papercuts/resolved/20260814T073042933Z-c3a0b60114.md)

### Problem

The release checksum documentation reads SHA256SUMS but never compares expected and actual hashes, so the documented verification step does not verify anything.

### Resolution

Replaced the display-only checksum example with strict SHA256SUMS parsing, safe filename checks, missing-asset checks, and expected-versus-actual SHA-256 comparison. Exercised the logic in memory: a valid six-asset set passed and a corrupted checksum was rejected.

<!-- paperq:resolution:20260814T073832267Z-b55a466e92:697bab958065a0b8 -->
## 20260814T073832267Z-b55a466e92

Recorded: 2026-08-14T07:39:02.8143326+00:00
Papercut: [.papercuts/resolved/20260814T073832267Z-b55a466e92.md](.papercuts/resolved/20260814T073832267Z-b55a466e92.md)

### Problem

During a long sequential maintenance session it is easy to retry a resolved ID or call next while another item is already working. Run paperq list before lifecycle transitions to re-establish the current queue state.

### Resolution

Confirmed the CLI already exposes active state through paperq list and emits precise invalid_transition and queue_empty errors. Used list --all to recover the working ID, so no additional command or product complexity was needed.

<!-- paperq:resolution:20260814T073938348Z-21f16cf1d8:f83cb98d7826bcad -->
## 20260814T073938348Z-21f16cf1d8

Recorded: 2026-08-14T07:40:18.2783178+00:00
Papercut: [.papercuts/resolved/20260814T073938348Z-21f16cf1d8.md](.papercuts/resolved/20260814T073938348Z-21f16cf1d8.md)

### Problem

When a lifecycle retry finds its event already appended, the persisted record stays idempotent but the returned in-memory history duplicates that event. Returned show/history state should match the file.

### Resolution

Tracked whether the transition event was already present and now reuses parsed history on retries instead of appending it again in memory. Simulated the interrupted-transition state and verified both persisted and returned histories contain exactly one Blocked event; all 21 tests pass.
