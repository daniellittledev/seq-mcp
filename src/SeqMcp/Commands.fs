/// Shared command core: the actual work behind each capability, owned by neither
/// transport. Both the MCP tools (Tools.fs) and the CLI (Cli.fs) call these,
/// so behaviour and defaults stay identical across front-ends.
module SeqMcp.Commands

open System
open System.Globalization
open System.Threading.Tasks

let private parseDate (s: string) =
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

let private optional (s: string) =
    if String.IsNullOrWhiteSpace s then None else Some s

/// Run a Seq SQL query. Time window defaults to the last 24 hours; pass ISO-8601
/// UTC 'from'/'to' to override. Optionally scope to a saved signal id.
let query (client: SeqClient) (sql: string) (from: string) (to_: string) (signal: string) : Task<string> =
    task {
        let rangeStart =
            parseDate from |> Option.defaultValue (DateTime.UtcNow.AddHours(-24.0))

        let rangeEnd = parseDate to_ |> Option.defaultValue DateTime.UtcNow
        let! r = client.QueryAsync(sql, optional signal, Some rangeStart, Some rangeEnd)
        return Render.result r
    }

/// Most recent Error and Fatal events (up to 20), newest first. Defaults to the
/// last 30 minutes.
let recentErrors (client: SeqClient) (minutes: int) : Task<string> =
    task {
        let minutes = if minutes <= 0 then 30 else minutes
        let rangeStart = DateTime.UtcNow.AddMinutes(float -minutes)

        let! events =
            client.EventsAsync("@Level = 'Error' or @Level = 'Fatal'", 20, None, Some rangeStart, Some DateTime.UtcNow)

        return Render.events events
    }

/// Search events by a Seq filter expression, newest first. Time window defaults
/// to the last 24 hours. Optionally scope to a saved signal id.
let searchEvents (client: SeqClient) (filter: string) (count: int) (signal: string) : Task<string> =
    task {
        let count = if count <= 0 || count > 100 then 30 else count
        let rangeStart = DateTime.UtcNow.AddHours(-24.0)
        let! events = client.EventsAsync(filter, count, optional signal, Some rangeStart, Some DateTime.UtcNow)
        return Render.events events
    }

/// Full detail for a single event by its id.
let getEvent (client: SeqClient) (id: string) : Task<string> =
    task {
        let! e = client.GetEventAsync id
        return Render.eventDetail e
    }

/// List saved signals as Id · Title lines, optionally filtered by title substring.
let listSignals (client: SeqClient) (nameFilter: string) : Task<string> =
    task {
        let! signals = client.ListSignalsAsync()
        return Render.signals nameFilter signals
    }

/// List configured alerts as Id · Title lines, optionally filtered by title substring.
let listAlerts (client: SeqClient) (nameFilter: string) : Task<string> =
    task {
        let! alerts = client.ListAlertsAsync()
        return Render.alerts nameFilter alerts
    }

/// Full detail for a single alert by its id.
let getAlert (client: SeqClient) (id: string) : Task<string> =
    task {
        let! a = client.GetAlertAsync id
        return Render.alertDetail a
    }

/// Current firing state of alerts, derived from each alert's embedded Activity
/// (readable with a plain read key, unlike /api/alertstate which needs Project).
let alertState (client: SeqClient) : Task<string> =
    task {
        let! alerts = client.ListAlertsAsync()
        return Render.alertState alerts
    }
