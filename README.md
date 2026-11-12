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

HTTP, run the server yourself, then point Claude at the URL:

```sh
# in one terminal
SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.example.com \
SEQ__ENVIRONMENTS__PROD__APIKEY=your-api-key \
seq-mcp --http
# in another
claude mcp add --transport http seq http://localhost:5250
```

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
