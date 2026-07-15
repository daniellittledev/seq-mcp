/// CLI front-end: maps verbs onto Commands.* (the same core the MCP tools use).
/// A single `specs` list is the source of truth for `--help`, `seq-mcp commands`
/// (machine-readable JSON, the CLI analogue of MCP tools/list), and dispatch.
module SeqMcp.Cli

open System
open System.Text.Json
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// An optional `--flag value` parameter for a command.
type CliOpt =
    { name: string
      desc: string
      ``default``: string } // null when there is no default

/// One CLI verb: its positional args and its options.
type CliSpec =
    { name: string
      summary: string
      args: string list
      opts: CliOpt list }

let private opt name desc dflt = { name = name; desc = desc; ``default`` = dflt }

let specs =
    [ { name = "query"
        summary = "Run a Seq SQL query; compact tabular results (group by / aggregates)."
        args = [ "sql" ]
        opts =
          [ opt "from" "ISO-8601 UTC window start" "24h ago"
            opt "to" "ISO-8601 UTC window end" "now"
            opt "signal" "Saved signal id to scope to (from `signals`)" null ] }
      { name = "errors"
        summary = "Most recent Error/Fatal events (up to 20), newest first."
        args = []
        opts = [ opt "minutes" "Look-back window in minutes" "30" ] }
      { name = "search"
        summary = "Search events by a Seq filter expression, newest first."
        args = [ "filter" ]
        opts =
          [ opt "count" "Max events to return" "30"
            opt "signal" "Saved signal id to scope to (from `signals`)" null ] }
      { name = "event"
        summary = "Full detail for a single event by its id."
        args = [ "id" ]
        opts = [] }
      { name = "signals"
        summary = "List saved signals as Id · Title."
        args = []
        opts = [ opt "filter" "Case-insensitive title substring" null ] }
      { name = "alerts"
        summary = "List configured alerts as Id · Title (with a [disabled] marker)."
        args = []
        opts = [ opt "filter" "Case-insensitive title substring" null ] }
      { name = "alert"
        summary = "Full detail for a single alert by its id."
        args = [ "id" ]
        opts = [] }
      { name = "alert-state"
        summary = "Alerts currently firing (Status · Occurrences · Last check)."
        args = []
        opts = [] } ]

/// Split args into ordered positionals and a `--key value` map.
let private parseArgs (argv: string list) =
    let rec loop pos flags =
        function
        | [] -> List.rev pos, flags
        | (k: string) :: v :: rest when k.StartsWith "--" -> loop pos (Map.add (k.Substring 2) v flags) rest
        | x :: rest -> loop (x :: pos) flags rest

    loop [] Map.empty argv

let private helpText () =
    let line (s: CliSpec) =
        let args =
            s.args |> List.map (sprintf "<%s>") |> String.concat " "

        let opts =
            s.opts
            |> List.map (fun o ->
                let d = if isNull o.``default`` then "" else sprintf " (default: %s)" o.``default``
                sprintf "        --%-8s %s%s" o.name o.desc d)

        sprintf "  seq-mcp %-8s %s\n      %s" s.name args s.summary
        :: opts
        |> String.concat "\n"

    [ "Usage: seq-mcp <command> [<args>] [--option value]"
      ""
      specs |> List.map line |> String.concat "\n"
      ""
      "  seq-mcp commands       full command surface as JSON (for tools/agents)"
      "  seq-mcp --stdio        run as an MCP server over stdio"
      "  seq-mcp --http         run as an MCP server over HTTP" ]
    |> String.concat "\n"

/// Route one verb to the shared Commands core.
let private dispatch (client: SeqClient) cmd (pos: string list) (flags: Map<string, string>) =
    let arg i = List.tryItem i pos |> Option.defaultValue ""
    let flag k = Map.tryFind k flags |> Option.defaultValue null

    let flagInt k d =
        match Map.tryFind k flags with
        | Some v ->
            match Int32.TryParse v with
            | true, n -> n
            | _ -> d
        | None -> d

    match cmd with
    | "query" -> Some(Commands.query client (arg 0) (flag "from") (flag "to") (flag "signal"))
    | "errors" -> Some(Commands.recentErrors client (flagInt "minutes" 30))
    | "search" -> Some(Commands.searchEvents client (arg 0) (flagInt "count" 30) (flag "signal"))
    | "event" -> Some(Commands.getEvent client (arg 0))
    | "signals" -> Some(Commands.listSignals client (flag "filter"))
    | "alerts" -> Some(Commands.listAlerts client (flag "filter"))
    | "alert" -> Some(Commands.getAlert client (arg 0))
    | "alert-state" -> Some(Commands.alertState client)
    | _ -> None

/// Entry point for the CLI path. `configureClient` registers the SeqClient the
/// same way the server hosts do (passed in from Program.fs to keep one wiring).
let run (configureClient: IServiceCollection -> IConfiguration -> unit) (argv: string[]) : int =
    match Array.toList argv with
    | []
    | "--help" :: _
    | "-h" :: _ ->
        printfn "%s" (helpText ())
        0
    | "commands" :: _ ->
        printfn "%s" (JsonSerializer.Serialize(specs, JsonSerializerOptions(WriteIndented = true)))
        0
    | cmd :: rest ->
        let builder = Host.CreateApplicationBuilder()
        builder.Logging.ClearProviders() |> ignore // keep stdout clean for piping
        configureClient builder.Services builder.Configuration
        use host = builder.Build()
        let client = host.Services.GetRequiredService<SeqClient>()
        let pos, flags = parseArgs rest

        match dispatch client cmd pos flags with
        | None ->
            eprintfn "Unknown command '%s'. Run `seq-mcp --help`." cmd
            1
        | Some work ->
            try
                printfn "%s" (work.GetAwaiter().GetResult())
                0
            with ex ->
                eprintfn "Error: %s" ex.Message
                1
