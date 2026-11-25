---
name: seq
description: Query the Seq structured-log server with the `seq-mcp` CLI — Seq SQL aggregates, recent errors, filtered event search, single-event detail, saved signals, and alerts. Use whenever the user asks about logs, errors, exceptions, what happened in prod/local, error rates, or anything that lives in Seq, even if they don't say "Seq".
---

# Seq via the `seq-mcp` CLI

Run `seq-mcp` through Bash. Each call is a one-shot process: output goes to stdout, errors go to stderr with a non-zero exit.

```sh
seq-mcp query "select count(*) as Count from stream group by @Level"   # --from/--to ISO-8601 UTC (default last 24h), --signal <id>
seq-mcp errors --minutes 30                                            # latest Error/Fatal (up to 20)
seq-mcp search "@Exception like '%timeout%'" --count 30                # --signal <id>
seq-mcp event <id>                                                     # full detail: message, exception, properties
seq-mcp signals --filter <substring>
seq-mcp alerts --filter <substring>
seq-mcp alert <id>
seq-mcp alert-state                                                    # alerts currently firing
```

If `seq-mcp` isn't on the PATH or is blocked from starting (common on Windows machines with Defender attack surface reduction rules), run the same command through the signed `dotnet` host with a pinned version, keeping the `--` before the command: `dotnet tool execute SeqMcp@<version> --yes -- errors --minutes 30`. Use the version the user has pinned elsewhere (for example in their MCP config); if you can't find one, ask rather than guessing. Don't use `dnx`.

Every command takes `--env <name>` to choose the Seq environment (for example `--env local`). Leave it off to use the configured default.

## Workflow

- Start broad, then narrow. Use `query` with `group by` to find where the problem is, then `search` or `errors` to list events. Pass an event `Id` to `event` to see the full stack trace.
- Wrap filters and SQL in double quotes, as in the examples above, because Seq string literals use single quotes. Inside double quotes the shell still expands `$` and backticks, so escape them (`\$`, `` \` ``). Quote multi-word values: the CLI rejects stray extra arguments.
- If you're unsure of an option, `seq-mcp commands` prints the full command surface as JSON.
- If a config error comes back (no environment configured), tell the user to set the `SEQ__...` variables (see the README) rather than guessing a URL.
