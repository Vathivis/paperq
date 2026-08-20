<!-- paperq:agent-instructions:start -->
## Papercuts

During normal work, record small, non-blocking friction with `paperq add "<concise message>"` or `paperq add --stdin` without resolving it; after the record is added, end the papercut-capture side-track and continue the main task. Examples include dead-end tool calls, broken links, flaky commands, stale caches, confusing errors, and undocumented setup.

Keep each papercut to one or two sentences. Include a suspected cause or fix only when useful. Never log secrets, credentials, full transcripts, or large raw output.

When explicitly assigned papercut maintenance, read `PAPERQ_RESOLUTIONS.md` if it exists, then process the queue one item at a time with `paperq list`, `paperq next --claim`, and `paperq show <id>`. When the user explicitly selects a specific papercut ID, use `paperq claim <id>` instead of the oldest-first `next --claim`. Investigate the claimed item, use `paperq resolve <id> --note "<verified solution>"` when fixed or `paperq block <id> --reason "<reason>"` when it cannot proceed, then continue until no open papercuts remain.
<!-- paperq:agent-instructions:end -->

<!-- paperq:resolutions-reference:start -->
If `PAPERQ_RESOLUTIONS.md` exists, read it before retrying recurring project-specific friction. PaperQ creates it after the first successful `resolve`.
<!-- paperq:resolutions-reference:end -->
