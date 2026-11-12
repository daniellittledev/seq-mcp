namespace SeqMcp

open System
open System.ComponentModel
open System.Globalization
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ModelContextProtocol.Server

/// Rendering helpers: turn Seq query results into terse plain text so the LLM
/// spends minimal tokens. Never returns raw JSON.
module private Render =

    let maxRows = 100

    let cell (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.String -> e.GetString()
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> ""
        | _ -> e.GetRawText()

    /// Render Columns + Rows as `a · b · c` lines with a header, capped.
    let table (cols: string[]) (rows: JsonElement[][]) =
        let cols = if isNull (box cols) then [||] else cols
        let rows = if isNull (box rows) then [||] else rows
        let sb = StringBuilder()

        if cols.Length > 0 then
            sb.AppendLine(String.Join(" · ", cols)) |> ignore

        for row in Array.truncate maxRows rows do
            sb.AppendLine(String.Join(" · ", Array.map cell row)) |> ignore

        if rows.Length > maxRows then
            sb.AppendLine(sprintf "… %d more rows (refine your query)" (rows.Length - maxRows))
            |> ignore

        if rows.Length = 0 then "No rows." else sb.ToString().TrimEnd()

    /// Format a QueryResult, surfacing query errors and time-sliced results clearly.
    let result (r: QueryResult) =
        if not (String.IsNullOrWhiteSpace r.Error) then
            sprintf "Query error: %s" (r.Error.Trim())
        elif isNull (box r.Rows) && r.Slices.ValueKind = JsonValueKind.Array then
            "Query returned time-sliced data (group by time(...)). Use a value grouping such as `group by @Level` for tabular output."
        else
            table r.Columns r.Rows

    let private maxMessageLength = 300

    let private level (e: SeqEvent) =
        if String.IsNullOrWhiteSpace e.Level then "Information" else e.Level

    let private oneLineMessage (m: string) =
        match m with
        | null -> ""
        | m ->
            let s = m.Replace("\r", " ").Replace("\n", " ").Trim()
            if s.Length > maxMessageLength then s.Substring(0, maxMessageLength) + "…" else s

    /// Render events as terse `Id · Timestamp · Level · Message` lines, newest first.
    /// The id can be passed to get_event for full detail.
    let events (es: SeqEvent[]) =
        if isNull (box es) || es.Length = 0 then
            "No events."
        else
            es
            |> Array.map (fun e ->
                sprintf "%s · %s · %s · %s" e.Id e.Timestamp (level e) (oneLineMessage e.RenderedMessage))
            |> String.concat "\n"

    /// Render a single event in full: header, message, exception, and properties.
    let eventDetail (e: SeqEvent) =
        let sb = StringBuilder()
        sb.AppendLine(sprintf "%s · %s · %s" e.Id e.Timestamp (level e)) |> ignore

        if not (isNull e.RenderedMessage) then
            sb.AppendLine(e.RenderedMessage.Trim()) |> ignore

        if not (String.IsNullOrWhiteSpace e.Exception) then
            sb.AppendLine().AppendLine("Exception:").AppendLine(e.Exception.TrimEnd()) |> ignore

        if not (isNull (box e.Properties)) && e.Properties.Length > 0 then
            sb.AppendLine().AppendLine("Properties:") |> ignore

            for p in e.Properties do
                sb.AppendLine(sprintf "  %s = %s" p.Name (cell p.Value)) |> ignore

        sb.ToString().TrimEnd()

    /// Render signals as `Id · Title` lines, filtered by an optional title substring.
    let signals (nameFilter: string) (sigs: Signal[]) =
        let matches =
            if isNull (box sigs) then
                [||]
            elif String.IsNullOrWhiteSpace nameFilter then
                sigs
            else
                sigs
                |> Array.filter (fun s ->
                    not (isNull s.Title)
                    && s.Title.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)

        if matches.Length = 0 then
            "No matching signals."
        else
            let sb = StringBuilder()

            for s in Array.truncate 50 matches do
                sb.AppendLine(sprintf "%s · %s" s.Id s.Title) |> ignore

            if matches.Length > 50 then
                sb.AppendLine(sprintf "… %d more (use a name filter)" (matches.Length - 50)) |> ignore

            sb.ToString().TrimEnd()

[<McpServerToolType>]
type SeqTools(client: SeqClient) =

    // TEMPORARY bridge until Task 4 resolves the environment per tool call.
    let env: SeqConfig.SeqEnvironment =
        let nonBlank v = if String.IsNullOrWhiteSpace v then None else Some v

        { Name = "default"
          ServerUrl = nonBlank (Environment.GetEnvironmentVariable "SEQ__SERVERURL") |> Option.defaultValue "http://localhost:5341"
          ApiKey = nonBlank (Environment.GetEnvironmentVariable "SEQ__APIKEY") }

    let parseDate (s: string) =
        if String.IsNullOrWhiteSpace s then
            None
        else
            match
                DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, dt -> Some dt
            | _ -> None

    let run (work: unit -> Task<string>) : Task<string> =
        task {
            try
                return! work ()
            with ex ->
                return sprintf "Error: %s" ex.Message
        }

    let optional (s: string) =
        if String.IsNullOrWhiteSpace s then None else Some s

    [<McpServerTool; Description("Run a Seq SQL query and return compact tabular results. Supports aggregates and grouping, e.g. \"select count(*) as Count from stream group by @Level\". Time window defaults to the last 24 hours; pass ISO-8601 UTC 'from'/'to' to override. Optionally scope to a saved signal id (from list_signals). Results are capped at 100 rows.")>]
    member _.SeqQuery
        (
            [<Description("Seq SQL query, e.g. 'select count(*) as Count from stream group by @Level'")>] sql: string,
            [<Description("Optional ISO-8601 UTC start of the time window. Defaults to 24h ago."); Optional; DefaultParameterValue(null: string)>] from: string,
            [<Description("Optional ISO-8601 UTC end of the time window. Defaults to now."); Optional; DefaultParameterValue(null: string)>] ``to``: string,
            [<Description("Optional saved signal id (e.g. 'signal-123') to scope the query to. From list_signals."); Optional; DefaultParameterValue(null: string)>] signal: string
        ) : Task<string> =
        run (fun () ->
            task {
                let rangeStart =
                    parseDate from |> Option.defaultValue (DateTime.UtcNow.AddHours(-24.0))

                let rangeEnd = parseDate ``to`` |> Option.defaultValue DateTime.UtcNow
                let! r = client.QueryAsync(env, sql, optional signal, Some rangeStart, Some rangeEnd)
                return Render.result r
            })

    [<McpServerTool; Description("List the most recent Error and Fatal events (up to 20). Returns compact Id · Time · Level · Message lines, newest first; pass an Id to get_event for full detail. Defaults to the last 30 minutes.")>]
    member _.RecentErrors
        (
            [<Description("Look-back window in minutes. Defaults to 30."); Optional; DefaultParameterValue(30)>] minutes: int
        ) : Task<string> =
        run (fun () ->
            task {
                let minutes = if minutes <= 0 then 30 else minutes
                let rangeStart = DateTime.UtcNow.AddMinutes(float -minutes)

                let! events =
                    client.EventsAsync(
                        env,
                        "@Level = 'Error' or @Level = 'Fatal'",
                        20,
                        None,
                        Some rangeStart,
                        Some DateTime.UtcNow
                    )

                return Render.events events
            })

    [<McpServerTool; Description("Search log events with a Seq filter expression (e.g. \"@Exception like '%timeout%'\" or \"StatusCode = 500\"). Returns compact Id · Time · Level · Message lines, newest first; pass an Id to get_event for full detail. Optionally scope to a saved signal id (from list_signals). Time window defaults to the last 24 hours.")>]
    member _.SearchEvents
        (
            [<Description("Seq filter expression, e.g. \"@Level = 'Warning' and Elapsed > 1000\"")>] filter: string,
            [<Description("Maximum number of events to return. Defaults to 30."); Optional; DefaultParameterValue(30)>] count: int,
            [<Description("Optional saved signal id (e.g. 'signal-123') to scope the search to. From list_signals."); Optional; DefaultParameterValue(null: string)>] signal: string
        ) : Task<string> =
        run (fun () ->
            task {
                let count = if count <= 0 || count > 100 then 30 else count
                let rangeStart = DateTime.UtcNow.AddHours(-24.0)
                let! events = client.EventsAsync(env, filter, count, optional signal, Some rangeStart, Some DateTime.UtcNow)
                return Render.events events
            })

    [<McpServerTool; Description("Get full detail for a single event by its id (the Id field from recent_errors / search_events): rendered message, exception/stack trace, and all properties.")>]
    member _.GetEvent([<Description("The event id, e.g. 'event-abc123...'")>] id: string) : Task<string> =
        run (fun () ->
            task {
                let! e = client.GetEventAsync(env, id)
                return Render.eventDetail e
            })

    [<McpServerTool; Description("List saved Seq signals (named, reusable filters) as Id · Title lines. Pass a name filter to narrow a large list; the returned Id can scope seq_query or search_events via their 'signal' parameter.")>]
    member _.ListSignals
        (
            [<Description("Optional case-insensitive substring to filter signal titles."); Optional; DefaultParameterValue(null: string)>] nameFilter: string
        ) : Task<string> =
        run (fun () ->
            task {
                let! signals = client.ListSignalsAsync(env)
                return Render.signals nameFilter signals
            })
