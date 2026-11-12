# Multi-Environment Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a single `seq-mcp` process serve any number of named Seq environments, chosen per query via an `environment` parameter that defaults to production.

**Architecture:** Move the Seq target from DI-fixed to per-call. A new pure config module parses an environments map from `IConfiguration` and resolves a default; `SeqClient` takes a resolved environment on each call and builds the request (absolute URL + per-request `X-Seq-ApiKey`); `SeqTools` injects the registry and resolves the `environment` argument per tool call. Host wiring builds the registry once and fails fast on bad config.

**Tech Stack:** F#, `Microsoft.NET.Sdk.Web`, net8.0, ModelContextProtocol.AspNetCore v1.4.0, xUnit for tests.

**Spec:** [docs/superpowers/specs/2026-11-12-multi-environment-support-design.md](../specs/2026-11-12-multi-environment-support-design.md)

## Global Constraints

- F#, `net8.0`, `Microsoft.NET.Sdk.Web`; MCP SDK `ModelContextProtocol.AspNetCore` v1.4.0.
- F# compile order (in `SeqMcp.fsproj`): `Models.fs` → `Config.fs` → `SeqClient.fs` → `Tools.fs` → `Program.fs`.
- DI registration is shared by the stdio and HTTP hosts (one place builds the registry + client).
- stdio transport: logs and error output go to **stderr only** (stdout is the JSON-RPC stream).
- Config env-var shape, exact: `SEQ__ENVIRONMENTS__<NAME>__SERVERURL`, `SEQ__ENVIRONMENTS__<NAME>__APIKEY`, `SEQ__DEFAULTENVIRONMENT`. Legacy fallback: `SEQ__SERVERURL` / `SEQ__APIKEY`.
- Environment names are matched **case-insensitively** (env-var section keys arrive upper-cased); normalise to lower-case on parse and on lookup.
- Default environment name is `prod`; the legacy-synthesised environment is also named `prod`.
- API key is optional per environment; when absent, send no `X-Seq-ApiKey` header.
- Secrets come from env vars only; never commit a key.

## Review Focus

- **Environment name case / whitespace:** `environment: "prod"` must match a section parsed from `SEQ__ENVIRONMENTS__PROD__…` (upper-cased key); a blank/whitespace value means "use default." → Task 2 (resolve) + Task 4 (tool).
- **ServerUrl trailing slash:** `https://seq.x.com` and `https://seq.x.com/` must both yield exactly one slash before `api/...`. → Task 3.
- **Unknown environment name:** returns a graceful error listing configured names, never a crash. → Task 2 (resolve) + Task 4 (tool).
- **No environment configured at all:** fail fast at startup with a clear message (not a silent localhost default, not a raw stack trace), including in stdio mode (message to stderr, exit non-zero). → Task 2 (parse) + Task 5 (manual).
- **`SEQ__DEFAULTENVIRONMENT` names a missing environment:** fail fast listing configured names. → Task 2 (parse).

---

### Task 1: Test project scaffold

**Files:**
- Create: `tests/SeqMcp.Tests/SeqMcp.Tests.fsproj`
- Create: `tests/SeqMcp.Tests/Smoke.fs`
- Modify: `seq-mcp.sln`

**Interfaces:**
- Produces: a buildable xUnit F# test project referencing `src/SeqMcp/SeqMcp.fsproj`, so later tasks add test files to it.

- [ ] **Step 1: Create the test project** `tests/SeqMcp.Tests/SeqMcp.Tests.fsproj` — `Microsoft.NET.Sdk`, net8.0, `IsPackable=false`. PackageReferences: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.Extensions.Configuration` (for in-memory config in later tasks). ProjectReference: `../../src/SeqMcp/SeqMcp.fsproj`. Compile `Smoke.fs`.

- [ ] **Step 2: Write a trivial passing test** in `Smoke.fs`:

```fsharp
module SeqMcp.Tests.Smoke
open Xunit
[<Fact>]
let ``harness runs`` () = Assert.True(true)
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln seq-mcp.sln add tests/SeqMcp.Tests/SeqMcp.Tests.fsproj`

- [ ] **Step 4: Run the tests to verify the harness works**

Run: `dotnet test`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add tests/SeqMcp.Tests seq-mcp.sln
git commit -m "test: add xUnit test project for seq-mcp"
```

---

### Task 2: Environment config model, parsing, resolution

**Files:**
- Create: `src/SeqMcp/Config.fs`
- Modify: `src/SeqMcp/SeqMcp.fsproj` (add `Config.fs` after `Models.fs`, before `SeqClient.fs`)
- Test: `tests/SeqMcp.Tests/ConfigTests.fs` (add to `SeqMcp.Tests.fsproj`)

**Interfaces:**
- Produces, in `module SeqMcp.SeqConfig`:
  - `type SeqEnvironment = { Name: string; ServerUrl: string; ApiKey: string option }`
  - `type SeqEnvironments = { ByName: Map<string, SeqEnvironment>; Default: string }`
  - `val parse : IConfiguration -> Result<SeqEnvironments, string>` — names lower-cased; legacy flat `Seq:ServerUrl`/`ApiKey` (only when no `Seq:Environments`) → one env named `prod`; default resolved by the spec's precedence; `Error` carries a user-facing message.
  - `val resolve : SeqEnvironments -> string option -> Result<SeqEnvironment, string>` — `None`/blank → default; `Some name` → case-insensitive lookup; unknown → `Error "Unknown environment '<name>'. Configured: <a, b>."`

- [ ] **Step 1: Write the failing tests** in `ConfigTests.fs`. Build `IConfiguration` with `ConfigurationBuilder().AddInMemoryCollection(dict [...]).Build()`. Cover:

```fsharp
// parse: named map, two environments, explicit default
//   -> Ok; ByName has "prod" & "local"; Default = "prod"; prod.ServerUrl correct; local.ApiKey = None when unset
// parse: section key upper-cased ("Seq:Environments:PROD:ServerUrl") -> ByName contains "prod"
// parse: legacy flat Seq:ServerUrl + Seq:ApiKey, no Environments -> Ok; single env named "prod"; Default="prod"
// parse: no Environments and no flat ServerUrl -> Error (message mentions SEQ__ENVIRONMENTS)
// parse: Seq:DefaultEnvironment="stage" not in map -> Error listing configured names
// parse: multiple envs, no DefaultEnvironment, no "prod" -> Error (ambiguous default)
// parse: multiple envs, no DefaultEnvironment, "prod" present -> Default="prod"
// parse: single env named "local", no DefaultEnvironment -> Default="local"
// resolve envs None -> Ok default env
// resolve envs (Some "LOCAL") -> Ok local env (case-insensitive)
// resolve envs (Some "   ") -> Ok default env
// resolve envs (Some "stage") -> Error containing "Unknown environment 'stage'" and "prod"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ConfigTests`
Expected: FAIL (SeqConfig / members not defined).

- [ ] **Step 3: Implement `SeqConfig` in `Config.fs`** per the Interfaces block. Read environments via `config.GetSection("Seq:Environments").GetChildren()` (each child `Key` = name → lower-case; child.["ServerUrl"], optional child.["ApiKey"]). Default precedence exactly as the spec lists. Add `Config.fs` to the fsproj compile order after `Models.fs`; add `ConfigTests.fs` to the test fsproj.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ConfigTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SeqMcp/Config.fs src/SeqMcp/SeqMcp.fsproj tests/SeqMcp.Tests/ConfigTests.fs tests/SeqMcp.Tests/SeqMcp.Tests.fsproj
git commit -m "feat: parse and resolve named Seq environments"
```

---

### Task 3: SeqClient targets an environment per call

**Files:**
- Modify: `src/SeqMcp/SeqClient.fs`
- Test: `tests/SeqMcp.Tests/SeqClientTests.fs`

**Interfaces:**
- Consumes: `SeqConfig.SeqEnvironment` (Task 2).
- Produces: `SeqClient(http: HttpClient)` whose query methods each take the environment first:
  - `member QueryAsync : env:SeqEnvironment * sql:string * signal:string option * rangeStartUtc:DateTime option * rangeEndUtc:DateTime option -> Task<QueryResult>`
  - `member EventsAsync : env:SeqEnvironment * filter:string * count:int * signal:string option * fromUtc:DateTime option * toUtc:DateTime option -> Task<SeqEvent[]>`
  - `member GetEventAsync : env:SeqEnvironment * id:string -> Task<SeqEvent>`
  - `member ListSignalsAsync : env:SeqEnvironment -> Task<Signal[]>`
  - The `http` client carries no base address; `send` builds the absolute URL from `env.ServerUrl` and attaches `X-Seq-ApiKey` per request.

- [ ] **Step 1: Write the failing tests** in `SeqClientTests.fs`. Use a stub `HttpMessageHandler` that records the last `HttpRequestMessage` and returns a canned `200` JSON body. Construct `SeqClient(new HttpClient(stub))`. Cover:

```fsharp
// ListSignalsAsync with env ServerUrl "https://seq.x.com" (no trailing slash)
//   -> request URI = https://seq.x.com/api/signals?shared=true   (exactly one slash)
// same with ServerUrl "https://seq.x.com/" (trailing slash) -> identical URI (no double slash)
// env.ApiKey = Some "k" -> request has header X-Seq-ApiKey = "k"
// env.ApiKey = None -> request has no X-Seq-ApiKey header
// GetEventAsync(env, "event-1") -> URI path ends /api/events/event-1?render=true
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SeqClientTests`
Expected: FAIL (signatures changed / not yet per-env).

- [ ] **Step 3: Rewrite `SeqClient.fs`** so `send (env: SeqEnvironment) (relativeUrl: string)` builds `env.ServerUrl.TrimEnd('/') + "/" + relativeUrl` into an `HttpRequestMessage`, adds `X-Seq-ApiKey` only when `env.ApiKey` is `Some`, and sends it. Thread `env` through all four methods. Update the 401/403 and connection-failure messages to name `env.Name` and `env.ServerUrl` (keeping the existing "check SEQ__APIKEY / SEQ__SERVERURL" guidance).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SeqClientTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SeqMcp/SeqClient.fs tests/SeqMcp.Tests/SeqClientTests.fs
git commit -m "feat: target Seq per request from the chosen environment"
```

---

### Task 4: `environment` parameter on every tool

**Files:**
- Modify: `src/SeqMcp/Tools.fs`
- Test: `tests/SeqMcp.Tests/ToolsTests.fs`

**Interfaces:**
- Consumes: `SeqClient` (Task 3), `SeqConfig.SeqEnvironments` + `SeqConfig.resolve` (Task 2).
- Produces: `SeqTools(client: SeqClient, envs: SeqEnvironments)`; each tool gains a trailing optional `environment: string` ( `[<Optional; DefaultParameterValue(null: string)>]`, described as *"Which configured Seq environment to query (e.g. 'prod', 'local'). Defaults to the configured default."*). Each tool resolves the environment via `SeqConfig.resolve envs (optional environment)`; on `Error msg` it returns `msg` as the tool's (error) text; on `Ok env` it passes `env` to the client call.

- [ ] **Step 1: Write the failing tests** in `ToolsTests.fs`. Build a stub-handler `SeqClient` that records which `ServerUrl` it was called with, and a `SeqEnvironments` with `prod` (default) + `local`. Cover:

```fsharp
// SeqQuery(sql, environment = null) -> client called against prod (default) ServerUrl
// SeqQuery(sql, environment = "local") -> client called against local ServerUrl
// SeqQuery(sql, environment = "LOCAL") -> client called against local ServerUrl (case-insensitive)
// SeqQuery(sql, environment = "stage") -> returns text containing "Unknown environment 'stage'", client not called
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ToolsTests`
Expected: FAIL (ctor arity / parameter absent).

- [ ] **Step 3: Modify `Tools.fs`** — add the `envs` constructor dependency, add the trailing `environment` parameter to `SeqQuery`, `RecentErrors`, `SearchEvents`, `GetEvent`, `ListSignals`, resolve it, and pass the resolved `env` into the matching client call. Keep all existing behaviour (defaults, caps, signal scoping, rendering) unchanged.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ToolsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SeqMcp/Tools.fs tests/SeqMcp.Tests/ToolsTests.fs
git commit -m "feat: add environment parameter to every Seq tool"
```

---

### Task 5: Host wiring, fail-fast, version, README

**Files:**
- Modify: `src/SeqMcp/Program.fs`
- Modify: `src/SeqMcp/SeqMcp.fsproj` (Version → `0.2.0`)
- Modify: `README.md`

**Interfaces:**
- Consumes: `SeqConfig.parse` (Task 2), `SeqClient` (Task 3), `SeqTools` (Task 4).
- Produces: `val tryConfigureSeq : IServiceCollection -> IConfiguration -> Result<unit, string>` — on `Ok` it registers the `SeqEnvironments` singleton and `AddHttpClient<SeqClient>()` (no base address); on `Error` it returns the message. Both `runStdio` and `runHttp` call it and, on `Error msg`, write `msg` to **stderr** and return `1` before building the host.

- [ ] **Step 1: Replace `configureSeqClient`** with `tryConfigureSeq` as above; register `SeqTools` via `.WithTools<SeqTools>()` as today (DI supplies `SeqClient` + `SeqEnvironments`). Wire the `Error` → stderr → exit-1 path into `runStdio` and `runHttp`.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds; `dotnet test` still all-green.

- [ ] **Step 3: Manual verification — routing and fail-fast.**
  - With `SEQ__ENVIRONMENTS__PROD__SERVERURL` + `SEQ__ENVIRONMENTS__LOCAL__SERVERURL` set, run the stdio server and perform the MCP handshake (`initialize` → `notifications/initialized` → `tools/list`); confirm every tool advertises the `environment` parameter. (Reuse the handshake recipe in the prior design's Verification section.)
  - Run the server with **no** `SEQ__…` vars set; confirm it prints the clear config error to stderr and exits non-zero (no stack trace, no silent localhost).

- [ ] **Step 4: Update `README.md`** — document the `SEQ__ENVIRONMENTS__<NAME>__SERVERURL`/`APIKEY` + `SEQ__DEFAULTENVIRONMENT` config and the per-tool `environment` parameter; show a `claude mcp add` example carrying two environments; note the legacy flat vars still work as `prod`. Bump `<Version>` to `0.2.0` in the fsproj.

- [ ] **Step 5: Commit**

```bash
git add src/SeqMcp/Program.fs src/SeqMcp/SeqMcp.fsproj README.md
git commit -m "feat: build environment registry at startup, fail fast on bad config"
```

---

### Task 6: Release 0.2.0 and reconfigure the machine

Deployment task (no unit test); live reconfiguration is in scope per the spec. Do this only after Tasks 1–5 are merged to `main`.

**Files:**
- Modify: `~/.claude.json` (live user config — back up first)

- [ ] **Step 1: Tag the release** — from `main` at the merged commit, create and push tag `v0.2.0` to drive `publish.yml` (OIDC publish to nuget.org). Confirm the workflow succeeds and `SeqMcp 0.2.0` appears on nuget.org.

- [ ] **Step 2: Update the global tool**

Run: `dotnet tool update -g SeqMcp`
Expected: resolves to 0.2.0.

- [ ] **Step 3: Back up and rewrite `~/.claude.json`** — copy to a timestamped backup first. Replace the Seq MCP setup with a single global entry (suggested name `seq`) that launches `seq-mcp --stdio` and carries both environments:

```
SEQ__DEFAULTENVIRONMENT=prod
SEQ__ENVIRONMENTS__PROD__SERVERURL=https://seq.eventsair.com
SEQ__ENVIRONMENTS__PROD__APIKEY=<existing prod key>
SEQ__ENVIRONMENTS__LOCAL__SERVERURL=http://localhost:5341
SEQ__ENVIRONMENTS__LOCAL__APIKEY=<existing local key, if any>
```

Then remove the now-redundant entries: the external `seq-mcp-server` global entry and the home-dir (`C:/Users/DanielLittle`) `localhost` override. Leave `statuscake` untouched. Validate the file parses as JSON.

- [ ] **Step 4: Verify in a fresh session** — start a new Claude session, confirm one Seq server is present, a default (prod) query works, and `environment: "local"` reaches the local Seq. Confirm only the expected Seq process count via `Get-Process seq-mcp,seq-mcp-server`.
