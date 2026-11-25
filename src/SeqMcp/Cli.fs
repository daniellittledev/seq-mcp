/// CLI front-end: maps verbs onto Commands.* (the same core the MCP tools use).
/// A single `specs` list is the source of truth for `--help`, `seq-mcp commands`
/// (machine-readable JSON, the CLI analogue of MCP tools/list), and dispatch.
/// Every verb accepts a global `--env <name>` to pick a configured Seq environment
/// (omitted selects the configured default).
module SeqMcp.Cli

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Configuration

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

/// A verb with its arguments checked against its spec.
type ParsedCommand =
    { spec: CliSpec
      args: string list
      flags: Map<string, string> }

/// Split args into ordered positionals and a `--key value` map. A `--key` with no value
/// after it is an error rather than a stray positional: a dangling `--env` must not
/// silently fall back to the default environment.
let private splitArgs (argv: string list) : Result<string list * Map<string, string>, string> =
    let rec loop pos flags =
        function
        | [] -> Ok(List.rev pos, flags)
        | (k: string) :: rest when k.StartsWith "--" ->
            match rest with
            | v :: rest' when not (v.StartsWith "--") -> loop pos (Map.add (k.Substring 2) v flags) rest'
            | _ -> Error $"Option '{k}' needs a value."
        | x :: rest -> loop (x :: pos) flags rest

    loop [] Map.empty argv

let private findSpec (cmd: string) =
    specs
    |> List.tryFind (fun s -> s.name = cmd)
    |> Option.map Ok
    |> Option.defaultValue (Error $"Unknown command '{cmd}'. Run `seq-mcp --help`.")

/// Every option must be one the verb declares (or the global `--env`), so a typo such
/// as `--minute` fails loudly instead of being ignored.
let private checkFlags (spec: CliSpec) (flags: Map<string, string>) =
    let allowed = "env" :: (spec.opts |> List.map (fun o -> o.name)) |> Set.ofList

    flags
    |> Map.keys
    |> Seq.tryFind (allowed.Contains >> not)
    |> function
        | Some k -> Error $"Unknown option '--{k}' for '{spec.name}'. Run `seq-mcp --help`."
        | None -> Ok flags

/// Positionals must match the verb's declared args exactly: a missing id or an unquoted
/// multi-word filter (whose tail would otherwise be dropped) is an error.
let private checkArgs (spec: CliSpec) (args: string list) =
    let usage = spec.name :: (spec.args |> List.map (sprintf "<%s>")) |> String.concat " "

    match compare args.Length spec.args.Length with
    | 0 -> Ok args
    | n when n < 0 -> Error $"Missing argument. Usage: seq-mcp {usage}"
    | _ -> Error $"Too many arguments (quote multi-word values). Usage: seq-mcp {usage}"

/// Parse a verb and its arguments against `specs`. Pure, so it is unit-testable.
let parseCommand (argv: string list) : Result<ParsedCommand, string> =
    match argv with
    | [] -> Error "No command given. Run `seq-mcp --help`."
    | cmd :: rest ->
        findSpec cmd
        |> Result.bind (fun spec ->
            splitArgs rest
            |> Result.bind (fun (pos, flags) ->
                checkFlags spec flags
                |> Result.bind (fun flags ->
                    checkArgs spec pos
                    |> Result.map (fun args -> { spec = spec; args = args; flags = flags }))))

/// Read an integer option, rejecting anything that isn't a positive whole number.
let private flagInt (flags: Map<string, string>) (k: string) (dflt: int) : Result<int, string> =
    match Map.tryFind k flags with
    | None -> Ok dflt
    | Some v ->
        match Int32.TryParse v with
        | true, n when n > 0 -> Ok n
        | _ -> Error $"Option '--{k}' must be a positive whole number, got '{v}'."

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
      "  --env <name>           target a configured Seq environment (default: the configured default)"
      "  seq-mcp commands       full command surface as JSON (for tools/agents)"
      "  seq-mcp --stdio        run as an MCP server over stdio"
      "  seq-mcp --http         run as an MCP server over HTTP" ]
    |> String.concat "\n"

/// Route a parsed verb to the shared Commands core. Option values are validated here,
/// before any config is loaded, so usage errors never depend on the environment.
/// `--env` picks the environment (absent → the configured default), resolved inside
/// Commands against the registry.
let private toWork (p: ParsedCommand) : Result<SeqClient -> SeqConfig.SeqEnvironments -> Task<string>, string> =
    let arg i = List.item i p.args // arity already checked by parseCommand
    let flag k = Map.tryFind k p.flags |> Option.defaultValue null
    let env = flag "env"

    match p.spec.name with
    | "query" -> Ok(fun c e -> Commands.query c e env (arg 0) (flag "from") (flag "to") (flag "signal"))
    | "errors" -> flagInt p.flags "minutes" 30 |> Result.map (fun m c e -> Commands.recentErrors c e env m)
    | "search" -> flagInt p.flags "count" 30 |> Result.map (fun n c e -> Commands.searchEvents c e env (arg 0) n (flag "signal"))
    | "event" -> Ok(fun c e -> Commands.getEvent c e env (arg 0))
    | "signals" -> Ok(fun c e -> Commands.listSignals c e env (flag "filter"))
    | "alerts" -> Ok(fun c e -> Commands.listAlerts c e env (flag "filter"))
    | "alert" -> Ok(fun c e -> Commands.getAlert c e env (arg 0))
    | "alert-state" -> Ok(fun c e -> Commands.alertState c e env)
    | other -> Error $"Command '{other}' is in specs but not wired up." // guards specs/dispatch drift

/// The CLI is one-shot, so it skips the generic Host (DI, logging, hosted services) that
/// the MCP transports need and builds the same configuration sources by hand:
/// appsettings.json and appsettings.{DOTNET_ENVIRONMENT}.json from the current directory,
/// then environment variables (which win). The SeqClient gets a plain HttpClient.
/// This cuts per-call startup by about a third.
let private loadEnvironments () =
    let envName =
        match Environment.GetEnvironmentVariable "DOTNET_ENVIRONMENT" with
        | null | "" -> "Production"
        | name -> name

    ConfigurationBuilder()
        .SetBasePath(IO.Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional = true)
        .AddJsonFile($"appsettings.{envName}.json", optional = true)
        .AddEnvironmentVariables()
        .Build()
    |> SeqConfig.parse

let private fail (msg: string) =
    eprintfn "%s" msg
    1

/// Entry point for the CLI path.
let run (argv: string[]) : int =
    match Array.toList argv with
    | []
    | "--help" :: _
    | "-h" :: _ ->
        printfn "%s" (helpText ())
        0
    | "commands" :: _ ->
        printfn "%s" (JsonSerializer.Serialize(specs, JsonSerializerOptions(WriteIndented = true)))
        0
    | args ->
        match parseCommand args |> Result.bind toWork with
        | Error msg -> fail msg
        | Ok work ->
            match loadEnvironments () with
            | Error msg -> fail msg
            | Ok envs ->
                use http = new HttpClient()

                try
                    printfn "%s" ((work (SeqClient http) envs).GetAwaiter().GetResult())
                    0
                with ex ->
                    fail $"Error: {ex.Message}"
