# seq-mcp

F# tool for querying a [Seq](https://datalust.co) log server. One binary, three front-ends over a shared core (`Commands.fs`): CLI (default), MCP stdio (`--stdio`), MCP HTTP (`--http`).

## CLI

Prefer the CLI for one-off log queries from this session:

```sh
seq-mcp query "select count(*) as Count from stream group by @Level"
seq-mcp errors --minutes 30
seq-mcp search "@Exception like '%timeout%'" --count 30
seq-mcp event <id>
seq-mcp signals --filter <substring>
```

`seq-mcp --help` lists commands; `seq-mcp commands` prints the full surface as JSON. Output to stdout, errors to stderr with non-zero exit. Configure via `SEQ__SERVERURL` / `SEQ__APIKEY`.

## Layout

Compile order (F# is order-sensitive): `Models → SeqClient → Render → Commands → Tools → Cli → Program`. Add new capabilities to `Commands.fs`; both `Tools.fs` (MCP) and `Cli.fs` (CLI) are thin front-ends. Keep `Cli.specs` in sync when adding a verb.

## Build

```sh
dotnet build seq-mcp.sln -c Release
```
