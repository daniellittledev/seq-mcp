# seq-mcp

A minimal, token-efficient [MCP](https://modelcontextprotocol.io) server for querying a
[Seq](https://datalust.co) structured-log server. It exposes Seq SQL querying (including
`group by` aggregates) plus two convenience tools, and returns terse plain text rather than
raw JSON to keep token usage low.

## Tools

| Tool | Description |
|------|-------------|
| `seq_query(sql, from?, to?, signal?, environment?)` | Run a Seq SQL query. Supports aggregates/grouping, e.g. `select count(*) as Count from stream group by @Level`. `from`/`to` are optional ISO-8601 UTC bounds (default: last 24h). Optionally scope to a saved `signal` id. Results capped at 100 rows. |
| `recent_errors(minutes?, environment?)` | Most recent Error/Fatal events (up to 20) as `Id · Time · Level · Message` lines, newest first (default: last 30 min). |
| `search_events(filter, count?, signal?, environment?)` | Events matching a Seq filter expression (e.g. `@Exception like '%timeout%'`), newest first (default: 30, last 24h). Optionally scope to a saved `signal` id. |
| `get_event(id, environment?)` | Full detail for one event (the `Id` from the list tools): rendered message, exception/stack trace, and all properties. |
| `list_signals(nameFilter?, environment?)` | Saved signals as `Id · Title` lines; the `Id` can scope `seq_query`/`search_events`. Pass a name substring to narrow the list. |
| `list_alerts(nameFilter?, environment?)` | Configured alerts as `Id · Title` lines (with a `[disabled]` marker). Pass a name substring to narrow; the `Id` can be passed to `get_alert`. |
| `get_alert(id, environment?)` | Full detail for one alert: title, description, enabled/disabled, protected status, owner, condition (where/having/window/level), notification channels, and recent activity. |
| `alert_state(environment?)` | Alerts currently firing as `Id · Title · Status · Occurrences · Last check` lines, derived from each alert's activity (shows only alerts whose last check triggered or that are suppressed after recently firing). |

Every tool takes an optional `environment` (see [Configuration](#configuration)); the CLI takes the same as a global `--env <name>`.

## Install

```sh
dotnet tool install -g SeqMcp
```

This installs the `seq-mcp` command, which runs in one of three modes:

- **CLI** (`seq-mcp <command>`, the default with no flags) — run a single query from a shell,
  script, or agent and print plain text. See [CLI](#cli) below.
- **stdio** (`seq-mcp --stdio`) — the MCP client launches and manages the process, speaking
  JSON-RPC over stdin/stdout. Recommended for local single-user clients like Claude Code.
- **HTTP** (`seq-mcp --http`) — a long-running Streamable HTTP server on `http://localhost:5250`
  (override with `ASPNETCORE_URLS`) that clients connect to by URL. Use for Claude Desktop and
  other clients that connect by URL.

On Windows machines where Defender attack surface reduction rules block global tool launchers,
don't install globally: see [Windows with Defender attack surface reduction rules](#windows-with-defender-attack-surface-reduction-rules).

## CLI

The same capabilities are available as shell commands over the same core code, so behaviour and
defaults match the MCP tools exactly. Run `seq-mcp --help` to list them, or `seq-mcp commands`
for a machine-readable JSON description of every command, argument, and option (the CLI analogue
of MCP's `tools/list` — point an agent at this to discover the surface in one call).

```sh
seq-mcp query "select count(*) as Count from stream group by @Level"
seq-mcp errors --minutes 30
seq-mcp search "@Exception like '%timeout%'" --count 30
seq-mcp event event-abc123
seq-mcp signals --filter checkout
seq-mcp alerts --filter checkout
seq-mcp alert alert-123
seq-mcp alert-state
```

Output goes to stdout; errors go to stderr with a non-zero exit code. Environments are configured
the same way as for the server, and `--env <name>` picks one per command (see [Configuration](#configuration)).

## Configuration

Configure one or more named Seq environments. At least one is required; the server exits with
an error on stderr at startup if none is configured.

| Setting | Environment variable |
|---------|----------------------|
| Server URL for environment `<NAME>` | `SEQ__ENVIRONMENTS__<NAME>__SERVERURL` |
| API key for environment `<NAME>` (optional) | `SEQ__ENVIRONMENTS__<NAME>__APIKEY` |
| Default environment | `SEQ__DEFAULTENVIRONMENT` |

Every tool takes an optional `environment` parameter naming which environment to query. When
omitted, the default environment is used: `SEQ__DEFAULTENVIRONMENT` if set, otherwise the only
configured environment, otherwise the one named `prod`. If none of these picks one, the server
exits at startup asking you to set `SEQ__DEFAULTENVIRONMENT`.

The legacy flat `SEQ__SERVERURL` / `SEQ__APIKEY` variables still work and define a single
environment named `prod`.

**Upgrading to 0.2.0:** at least one environment must now be configured and the server fails fast otherwise, whereas 0.1.x silently defaulted to `http://localhost:5341`.

**Upgrading to 0.3.0:** the CLI now rejects input it used to ignore or guess at: unknown options, an option with no value (such as a trailing `--env`), a missing or extra argument (quote multi-word filters), and non-numeric `--minutes` / `--count`. Each exits non-zero with a usage message.

The API key is sent as the `X-Seq-ApiKey` header, and only to the environment it belongs to. It
is optional if your Seq instance allows anonymous read; when the key is missing or Seq is
unreachable, tool calls return a graceful error message rather than failing the server.

### Use with Claude Code

stdio (recommended), Claude launches and manages the process. This example carries two
environments, with `prod` as the default:

```sh
claude mcp add seq \
  -e SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.example.com \
  -e SEQ__ENVIRONMENTS__PROD__APIKEY=prod-api-key \
  -e SEQ__ENVIRONMENTS__LOCAL__SERVERURL=http://localhost:5341 \
  -e SEQ__DEFAULTENVIRONMENT=prod \
  -- seq-mcp --stdio
```

#### Windows with Defender attack surface reduction rules

Corporate Defender ASR rules often block the unsigned launcher that `dotnet tool install -g` puts in
`%USERPROFILE%\.dotnet\tools`, so the `seq-mcp` command (and pointing the config at that `.exe`)
won't start. Skip the global install and run the tool through the signed `dotnet` host with a
pinned version instead (needs the .NET 10 SDK):

```powershell
claude mcp add seq -s user -e SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.example.com -e SEQ__ENVIRONMENTS__PROD__APIKEY=prod-api-key -- dotnet tool execute SeqMcp@0.3.0 --yes -- --stdio
```

- Use `dotnet` as the command, not `dnx`. `dnx` is a `.cmd` file, which Claude Code can't start
  directly on Windows.
- Keep the second `--`: it separates `dotnet tool execute`'s own options from the tool's
  arguments, such as `--stdio`.
- To upgrade, change the version in the config (`claude mcp remove seq -s user`, then add it again
  with the new version) instead of running `dotnet tool update`.

HTTP, run the server yourself, then point Claude at the URL:

```sh
# in one terminal
SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.example.com \
SEQ__ENVIRONMENTS__PROD__APIKEY=your-api-key \
seq-mcp --http
# in another
claude mcp add --transport http seq http://localhost:5250
```

### Use with Claude Code via the CLI (no MCP server)

An MCP server stays running for the whole Claude Code session. The CLI starts a process for each
command and exits when it's done, so nothing sits in memory between queries. To switch from the MCP:

1. Remove the MCP registration if you added one: `claude mcp remove seq`.
2. Give the CLI its Seq settings. The simplest way is the `env` block in `~/.claude/settings.json`,
   which every Bash command Claude runs inherits:

   ```json
   {
     "env": {
       "SEQ__ENVIRONMENTS__PROD__SERVERURL": "https://seq.example.com",
       "SEQ__ENVIRONMENTS__PROD__APIKEY": "prod-api-key",
       "SEQ__ENVIRONMENTS__LOCAL__SERVERURL": "http://localhost:5341",
       "SEQ__DEFAULTENVIRONMENT": "prod"
     }
   }
   ```

   Note that this puts the API key in the environment of *every* command Claude runs, in every
   project, not just `seq-mcp`. If you'd rather scope it, use a read-only Seq API key, or put the
   variables in a small wrapper script on your `PATH` that runs `seq-mcp` (`exec seq-mcp "$@"`).

3. Install the skill so Claude knows the CLI exists in any repo. It loads only when a log question
   comes up:

   ```sh
   mkdir -p ~/.claude/skills/seq && curl -fsSL https://raw.githubusercontent.com/daniellittledev/seq-mcp/main/skills/seq/SKILL.md -o ~/.claude/skills/seq/SKILL.md
   ```

   From a clone of this repo, `cp -r skills/seq ~/.claude/skills/` does the same.

Optionally, allow the CLI without permission prompts by adding `"permissions": { "allow": ["Bash(seq-mcp:*)"] }` to the same settings file.

If the `seq-mcp` launcher is blocked (see [Windows with Defender attack surface reduction rules](#windows-with-defender-attack-surface-reduction-rules)),
run each CLI command through the `dotnet` host instead. The skill knows to fall back to this:

```sh
dotnet tool execute SeqMcp@0.3.0 --yes -- errors --minutes 30
```

The matching permission rule is `Bash(dotnet tool execute SeqMcp@0.3.0:*)`.

### Example MCP client config (HTTP)

```json
{
  "mcpServers": {
    "seq": {
      "type": "http",
      "url": "http://localhost:5250"
    }
  }
}
```

## Claude Desktop extension (.mcpb)

The server is also packaged as a one-click [MCPB](https://github.com/anthropics/mcpb)
desktop extension. The bundle ships a self-contained Windows executable, so the target
machine needs neither the .NET runtime nor `seq-mcp` on its `PATH`.

To install it in Claude Desktop:

1. Download `seq-mcp.mcpb` from the [latest release](https://github.com/daniellittledev/seq-mcp/releases/latest).
2. Open **Settings → Extensions → Advanced settings → Install Extension**.
3. Select the downloaded `seq-mcp.mcpb`.
4. When prompted, enter your **Seq server URL** and (optionally) **Seq API key**.
   These are stored by Claude Desktop and passed to the server as the
   `SEQ__SERVERURL` / `SEQ__APIKEY` environment variables, so no secrets are baked
   into the bundle.

### Building the bundle yourself

Requires the .NET SDK and Node.js:

```pwsh
./build-mcpb.ps1
```

This publishes the self-contained executable and writes `dist/seq-mcp.mcpb`. Pushing a
`v*.*.*` tag runs the same build in CI and attaches the `.mcpb` to a GitHub Release.

## Build from source

```sh
dotnet build seq-mcp.sln -c Release
dotnet run --project src/SeqMcp -- --help          # CLI (default)
dotnet run --project src/SeqMcp -- --stdio         # MCP over stdio
dotnet run --project src/SeqMcp -- --http          # MCP over HTTP
```

## License

MIT
