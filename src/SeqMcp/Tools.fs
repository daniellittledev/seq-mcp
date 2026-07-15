namespace SeqMcp

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading.Tasks
open ModelContextProtocol.Server

/// MCP front-end: thin wrappers that expose Commands.* as discoverable tools.
/// The actual work and all defaults live in Commands.fs (shared with the CLI).
[<McpServerToolType>]
type SeqTools(client: SeqClient) =

    /// Wrap a command in a catch-all so an exception becomes a readable tool result
    /// rather than a protocol error.
    let run (work: unit -> Task<string>) : Task<string> =
        task {
            try
                return! work ()
            with ex ->
                return sprintf "Error: %s" ex.Message
        }

    [<McpServerTool; Description("Run a Seq SQL query and return compact tabular results. Supports aggregates and grouping, e.g. \"select count(*) as Count from stream group by @Level\". Time window defaults to the last 24 hours; pass ISO-8601 UTC 'from'/'to' to override. Optionally scope to a saved signal id (from list_signals). Results are capped at 100 rows.")>]
    member _.SeqQuery
        (
            [<Description("Seq SQL query, e.g. 'select count(*) as Count from stream group by @Level'")>] sql: string,
            [<Description("Optional ISO-8601 UTC start of the time window. Defaults to 24h ago."); Optional; DefaultParameterValue(null: string)>] from: string,
            [<Description("Optional ISO-8601 UTC end of the time window. Defaults to now."); Optional; DefaultParameterValue(null: string)>] ``to``: string,
            [<Description("Optional saved signal id (e.g. 'signal-123') to scope the query to. From list_signals."); Optional; DefaultParameterValue(null: string)>] signal: string
        ) : Task<string> =
        run (fun () -> Commands.query client sql from ``to`` signal)

    [<McpServerTool; Description("List the most recent Error and Fatal events (up to 20). Returns compact Id · Time · Level · Message lines, newest first; pass an Id to get_event for full detail. Defaults to the last 30 minutes.")>]
    member _.RecentErrors
        (
            [<Description("Look-back window in minutes. Defaults to 30."); Optional; DefaultParameterValue(30)>] minutes: int
        ) : Task<string> =
        run (fun () -> Commands.recentErrors client minutes)

    [<McpServerTool; Description("Search log events with a Seq filter expression (e.g. \"@Exception like '%timeout%'\" or \"StatusCode = 500\"). Returns compact Id · Time · Level · Message lines, newest first; pass an Id to get_event for full detail. Optionally scope to a saved signal id (from list_signals). Time window defaults to the last 24 hours.")>]
    member _.SearchEvents
        (
            [<Description("Seq filter expression, e.g. \"@Level = 'Warning' and Elapsed > 1000\"")>] filter: string,
            [<Description("Maximum number of events to return. Defaults to 30."); Optional; DefaultParameterValue(30)>] count: int,
            [<Description("Optional saved signal id (e.g. 'signal-123') to scope the search to. From list_signals."); Optional; DefaultParameterValue(null: string)>] signal: string
        ) : Task<string> =
        run (fun () -> Commands.searchEvents client filter count signal)

    [<McpServerTool; Description("Get full detail for a single event by its id (the Id field from recent_errors / search_events): rendered message, exception/stack trace, and all properties.")>]
    member _.GetEvent([<Description("The event id, e.g. 'event-abc123...'")>] id: string) : Task<string> =
        run (fun () -> Commands.getEvent client id)

    [<McpServerTool; Description("List saved Seq signals (named, reusable filters) as Id · Title lines. Pass a name filter to narrow a large list; the returned Id can scope seq_query or search_events via their 'signal' parameter.")>]
    member _.ListSignals
        (
            [<Description("Optional case-insensitive substring to filter signal titles."); Optional; DefaultParameterValue(null: string)>] nameFilter: string
        ) : Task<string> =
        run (fun () -> Commands.listSignals client nameFilter)

    [<McpServerTool; Description("List configured Seq alerts as Id · Title lines (with a [disabled] marker for disabled alerts). Pass a name filter to narrow a large list; the returned Id can be passed to get_alert for full detail.")>]
    member _.ListAlerts
        (
            [<Description("Optional case-insensitive substring to filter alert titles."); Optional; DefaultParameterValue(null: string)>] nameFilter: string
        ) : Task<string> =
        run (fun () -> Commands.listAlerts client nameFilter)

    [<McpServerTool; Description("Get full detail for a single alert by its id (the Id from list_alerts): title, description, enabled/disabled, protected status, owner, condition (where/having/window/level), notification channels, and recent activity.")>]
    member _.GetAlert([<Description("The alert id, e.g. 'alert-123'")>] id: string) : Task<string> =
        run (fun () -> Commands.getAlert client id)

    [<McpServerTool; Description("List Seq alerts that are currently firing as Id · Title · Status · Occurrences · Last check lines, derived from each alert's activity. Shows only alerts whose last evaluation triggered or that are suppressed after recently firing. Returns 'No alerts firing.' when none are active.")>]
    member _.AlertState() : Task<string> =
        run (fun () -> Commands.alertState client)
