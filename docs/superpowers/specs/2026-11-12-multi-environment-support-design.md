# Seq MCP — Multiple Environments — Design

Status: draft (2026-11-12)

## Purpose

Today a `seq-mcp` process targets exactly one Seq server: `SeqClient` is a typed
`HttpClient` whose base address and `X-Seq-ApiKey` header are fixed in DI
([Program.fs:13](../../../src/SeqMcp/Program.fs)). To watch more than one Seq (production
vs a local dev instance) you have to configure and run a second MCP server, which is why the
machine currently runs two overlapping Seq servers in every session.

This feature makes a single `seq-mcp` process serve **any number of named Seq environments**,
chosen per query. One MCP entry then covers production, local dev, and any future test/staging
Seq, and the external `seq-mcp-server` can be retired.

## Why

- **One server, two (or more) environments.** The real daily need is checking production logs
  and watching a local Seq while developing or running an app locally — from the same session.
- **Less duplication.** Collapses the two-server setup into one, and removes a redundant
  external dependency.
- **Room to grow.** A test/staging Seq can be added later with config alone, no code change.

## Decisions

Marked **[user]** where the choice was the requester's, **[call]** where it is the author's
to revisit.

- **[user]** One server handles multiple environments natively, selected by a **per-query
  parameter on each tool** — not two MCP entries, and not a stateful "switch environment" mode.
- **[user]** Omitting the parameter uses a **default environment** (production), so everyday
  use stays implicit; you pass `environment: "local"` only when deviating.
- **[user]** Environments are **fully configurable by name** — arbitrary set, so a `test`
  environment can be added later without touching code.
- **[user]** Retire the external `seq-mcp-server`; end state is a single global MCP entry.
- **[call]** Config shape: a named map, `Seq:Environments:<name>:{ServerUrl,ApiKey}`, plus
  `Seq:DefaultEnvironment`. Env-var form (what Claude Code passes):
  `SEQ__ENVIRONMENTS__PROD__SERVERURL`, `SEQ__ENVIRONMENTS__PROD__APIKEY`, … and
  `SEQ__DEFAULTENVIRONMENT`.
- **[call]** Backward compatibility: if no `Seq:Environments` are configured but the old flat
  `Seq:ServerUrl`/`Seq:ApiKey` are present, synthesise a single environment from them so existing
  setups keep working unchanged. The synthesised environment is named `prod` (the same default
  name), so it is selected by `SEQ__DEFAULTENVIRONMENT=prod` and shows as `prod` in errors.
- **[call]** `ApiKey` stays optional per environment (a local Seq often needs none).
- **[call]** The default environment name is `prod` unless overridden.

## Behaviour

### Configuration

| Key | Env var | Notes |
|-----|---------|-------|
| `Seq:Environments:<name>:ServerUrl` | `SEQ__ENVIRONMENTS__<NAME>__SERVERURL` | One block per environment. `<name>` is free-form (`prod`, `local`, `test`). |
| `Seq:Environments:<name>:ApiKey` | `SEQ__ENVIRONMENTS__<NAME>__APIKEY` | Optional. Sent as `X-Seq-ApiKey` for that environment only. |
| `Seq:DefaultEnvironment` | `SEQ__DEFAULTENVIRONMENT` | Which environment is used when a tool call omits `environment`. Defaults to `prod`. |
| `Seq:ServerUrl` / `Seq:ApiKey` | `SEQ__SERVERURL` / `SEQ__APIKEY` | **Legacy fallback.** Used only when no `Seq:Environments` block exists; becomes the sole/default environment. |

Example target config:

```
SEQ__DEFAULTENVIRONMENT=prod
SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.eventsair.com
SEQ__ENVIRONMENTS__PROD__APIKEY=<key>
SEQ__ENVIRONMENTS__LOCAL__SERVERURL=http://localhost:5341
SEQ__ENVIRONMENTS__LOCAL__APIKEY=<optional>
```

### Default resolution (startup)

1. If `Seq:DefaultEnvironment` is set, it must name a configured environment, else fail fast.
2. Else if exactly one environment is configured, that is the default.
3. Else if an environment named `prod` exists, use it.
4. Else fail fast (ambiguous default) with a message listing the configured names.

Startup also fails fast, with a clear message naming the `SEQ__ENVIRONMENTS__…` pattern, when
**no** environment is configured at all. Reachability is **not** checked at startup — only the
environment a call actually targets needs to be up, at call time.

### Tools

Every tool (`seq_query`, `recent_errors`, `search_events`, `get_event`, `list_signals`) gains
an optional `environment` parameter:

- Omitted / null → the default environment.
- A configured name → that environment.
- An unknown name → a graceful error (via the existing `run` try/catch that returns
  `Error: …` text), e.g. `Unknown environment 'stage'. Configured: prod, local.`

No other tool semantics change (time windows, caps, signal scoping, rendering all stay as-is).

### Errors

The existing clear-error style is kept and made environment-aware: connection and 401/403
messages name the **environment** alongside the URL and the relevant `SEQ__…` setting, so a
failure says which Seq it was talking to.

## Implementation shape (left to the builder to detail in the plan)

The core change is that the Seq target moves from DI-fixed to **per-call**:

- **Config model** (`Models.fs` or a new `Config.fs`, respecting F# compile order): a
  `SeqEnvironment` record (`Name`, `ServerUrl`, `ApiKey option`) and an immutable registry
  (environments map + resolved default). A pure function parses `IConfiguration` into it and
  returns either the registry or a descriptive error.
- **Client** (`SeqClient.fs`): stop relying on `HttpClient.BaseAddress` / a default header.
  Use a factory-provided `HttpClient` with no fixed target; build the absolute URL from the
  chosen environment's `ServerUrl` and attach `X-Seq-ApiKey` per request. Each query method
  takes the resolved `SeqEnvironment`.
- **Tools** (`Tools.fs`): inject the registry instead of a single `SeqClient`; resolve the
  `environment` argument to a `SeqEnvironment` (default when omitted), then call the client.
- **Host wiring** (`Program.fs`): replace `configureSeqClient`'s single-client registration
  with registry construction + an `IHttpClientFactory`-based client, shared by the stdio and
  HTTP hosts. Validation errors surface at startup.

## Testing

No test project exists yet; add one (`tests/SeqMcp.Tests`, xUnit) and drive the feature with
tests first:

- Config parsing: named map, legacy flat fallback, default-resolution precedence, and each
  fail-fast case (no environments; default names a missing environment).
- Environment resolution in a tool call: default when omitted, explicit selection, and the
  unknown-name error text.
- Client URL/header construction per environment, via a stub `HttpMessageHandler` (no live Seq):
  correct base URL joined, `X-Seq-ApiKey` present only when a key is configured.

Existing manual verification (MCP handshake, `tools/list`, `tools/call`) still applies; confirm
the `environment` parameter appears on each tool and routes correctly.

## Rollout

Live reconfiguration of the machine **is in scope** — "done" means the feature is working in
new sessions, not merely packaged.

1. **Release (nuget).** Bump `<Version>` to `0.2.0` and tag `v0.2.0` to drive the existing
   `publish.yml` OIDC publish to nuget.org. Once it propagates, update the global tool:
   `dotnet tool update -g SeqMcp`.
2. **Reconfigure `~/.claude.json`** (back it up first, as in prior edits):
   - Rewrite the single global MCP entry to carry both environments via the new
     `SEQ__ENVIRONMENTS__…` vars (entry name **[call]**: `seq`, since it is no longer
     EventsAir-only).
   - Remove the external `seq-mcp-server` entries: the global `seq` (external) and the home-dir
     `localhost` override.
3. Restart sessions to pick up the new config; confirm the `environment` parameter routes to
   both prod and local.

## Non-goals / anti-goals

- No stateful "current environment" mode or a tool that switches a session's target — selection
  is per call, explicit or defaulted.
- No per-environment duplication of tools — one tool set, parameter-selected.
- No requirement that every configured environment be reachable at startup.
- Do not break existing single-Seq configurations (legacy flat vars keep working).
- No new transport, endpoint, or Seq API surface — this is purely about target selection.
