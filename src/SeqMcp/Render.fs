namespace SeqMcp

open System
open System.Globalization
open System.Text
open System.Text.Json

/// Rendering helpers: turn Seq query results into terse plain text so the LLM
/// (or a CLI reader) spends minimal tokens. Never returns raw JSON.
/// Shared by the MCP tools (Tools.fs) and the CLI (Cli.fs) via Commands.fs.
module internal Render =

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

    /// Render alerts as `Id · Title` lines (with a `· [disabled]` marker),
    /// filtered by an optional title substring. Mirrors `signals`.
    let alerts (nameFilter: string) (items: Alert[]) =
        let matches =
            if isNull (box items) then
                [||]
            elif String.IsNullOrWhiteSpace nameFilter then
                items
            else
                items
                |> Array.filter (fun a ->
                    not (isNull a.Title)
                    && a.Title.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)

        if matches.Length = 0 then
            "No alerts."
        else
            let sb = StringBuilder()

            for a in Array.truncate 50 matches do
                let disabled = if a.IsDisabled then " · [disabled]" else ""
                sb.AppendLine(sprintf "%s · %s%s" a.Id a.Title disabled) |> ignore

            if matches.Length > 50 then
                sb.AppendLine(sprintf "… %d more (use a name filter)" (matches.Length - 50)) |> ignore

            sb.ToString().TrimEnd()

    /// Render a single alert in full: header, metadata, condition, channels, activity.
    let alertDetail (a: Alert) =
        let sb = StringBuilder()
        let disabled = if a.IsDisabled then " · [disabled]" else ""
        sb.AppendLine(sprintf "%s · %s%s" a.Id a.Title disabled) |> ignore

        if not (String.IsNullOrWhiteSpace a.Description) then
            sb.AppendLine(a.Description.Trim()) |> ignore

        let owner = if isNull a.OwnerId then "(none)" else a.OwnerId
        sb.AppendLine(sprintf "Protected: %b · Owner: %s" a.IsProtected owner) |> ignore

        if not (String.IsNullOrWhiteSpace a.Where) then
            sb.AppendLine(sprintf "Where: %s" (a.Where.Trim())) |> ignore

        if not (String.IsNullOrWhiteSpace a.Having) then
            sb.AppendLine(sprintf "Having: %s" (a.Having.Trim())) |> ignore

        if not (String.IsNullOrWhiteSpace a.TimeGrouping) then
            sb.AppendLine(sprintf "Window: %s" a.TimeGrouping) |> ignore

        if not (String.IsNullOrWhiteSpace a.NotificationLevel) then
            sb.AppendLine(sprintf "Level: %s" a.NotificationLevel) |> ignore

        if a.SignalExpression.ValueKind = JsonValueKind.Object then
            sb.AppendLine(sprintf "Signal: %s" (cell a.SignalExpression)) |> ignore

        let channels =
            if isNull (box a.NotificationChannels) then [||] else a.NotificationChannels

        if channels.Length > 0 then
            sb.AppendLine(sprintf "Notification channels (%d):" channels.Length) |> ignore

            for c in channels do
                sb.AppendLine(sprintf "  %s" (cell c)) |> ignore

        if not (isNull (box a.Activity)) then
            let act = a.Activity
            let triggered = if act.LastCheckTriggered then "triggered" else "ok"
            let lastCheck = if isNull act.LastCheck then "" else act.LastCheck

            sb.AppendLine(sprintf "Activity: %s · last check %s · %d total occurrences" triggered lastCheck act.TotalOccurrences)
            |> ignore

            if not (String.IsNullOrWhiteSpace act.SuppressedUntil) then
                sb.AppendLine(sprintf "  suppressed until %s" act.SuppressedUntil) |> ignore

        sb.ToString().TrimEnd()

    /// Render the current firing state of alerts, derived from each alert's embedded
    /// Activity (readable without Project permission). Lists only alerts whose last
    /// evaluation triggered or that are currently suppressed after firing, as
    /// `Id · Title · Status · N occurrences · last check`.
    let alertState (items: Alert[]) =
        // True only while the suppression window is still in the future; a stale
        // timestamp from an elapsed window must not read as "currently firing".
        let suppressedNow (s: string) =
            not (String.IsNullOrWhiteSpace s)
            && (match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                | true, until -> until > DateTimeOffset.UtcNow
                | _ -> false)

        let isActive (a: Alert) =
            not (isNull (box a.Activity))
            && (a.Activity.LastCheckTriggered || suppressedNow a.Activity.SuppressedUntil)

        let active = if isNull (box items) then [||] else Array.filter isActive items

        if active.Length = 0 then
            "No alerts firing."
        else
            active
            |> Array.map (fun a ->
                let act = a.Activity

                let status =
                    if suppressedNow act.SuppressedUntil then
                        sprintf "suppressed until %s" act.SuppressedUntil
                    else
                        "triggered"

                let lastCheck = if isNull act.LastCheck then "" else act.LastCheck

                sprintf "%s · %s · %s · %d occurrences · last check %s" a.Id a.Title status act.TotalOccurrences lastCheck)
            |> String.concat "\n"
