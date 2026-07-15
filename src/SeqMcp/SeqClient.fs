namespace SeqMcp

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Typed wrapper over Seq's HTTP API. Base address and the X-Seq-ApiKey header
/// are configured in DI (see Program.fs). All tool capabilities are built on the
/// single, well-defined query endpoint: GET /api/data.
type SeqClient(http: HttpClient) =

    static let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    static let isoUtc (t: DateTime) =
        t.ToUniversalTime().ToString("o")

    let serverUrl () =
        if isNull http.BaseAddress then
            "(no server URL — set SEQ__SERVERURL)"
        else
            string http.BaseAddress

    /// Issue a GET and return the body, turning transport/HTTP failures into clear,
    /// actionable messages that name the relevant SEQ__ setting.
    let send (url: string) : Task<string> =
        task {
            try
                use! resp = http.GetAsync url
                let! body = resp.Content.ReadAsStringAsync()

                if resp.IsSuccessStatusCode then
                    return body
                else
                    match int resp.StatusCode with
                    | 401
                    | 403 ->
                        let hint =
                            if http.DefaultRequestHeaders.Contains "X-Seq-ApiKey" then
                                "The SEQ__APIKEY is set but was rejected — check the key is valid and has read permission."
                            else
                                "No API key is configured — set the SEQ__APIKEY environment variable (this Seq server requires authentication)."

                        return
                            failwithf
                                "Seq at %s returned %d (unauthorized). %s"
                                (serverUrl ())
                                (int resp.StatusCode)
                                hint
                    | code -> return failwithf "Seq at %s returned %d: %s" (serverUrl ()) code (body.Trim())
            with :? HttpRequestException as ex ->
                return
                    failwithf
                        "Could not reach Seq at %s — check the SEQ__SERVERURL environment variable and that the server is running. (%s)"
                        (serverUrl ())
                        ex.Message
        }

    /// Run a Seq SQL query over an optional [rangeStartUtc, rangeEndUtc) window,
    /// optionally scoped to a saved signal.
    member _.QueryAsync
        (
            sql: string,
            signal: string option,
            rangeStartUtc: DateTime option,
            rangeEndUtc: DateTime option
        ) : Task<QueryResult> =
        task {
            let parts =
                [ yield "q=" + Uri.EscapeDataString sql
                  match signal with
                  | Some s when not (String.IsNullOrWhiteSpace s) -> yield "signal=" + Uri.EscapeDataString s
                  | _ -> ()
                  match rangeStartUtc with
                  | Some t -> yield "rangeStartUtc=" + Uri.EscapeDataString(isoUtc t)
                  | None -> ()
                  match rangeEndUtc with
                  | Some t -> yield "rangeEndUtc=" + Uri.EscapeDataString(isoUtc t)
                  | None -> () ]

            let url = "api/data?" + String.Join("&", parts)
            let! body = send url
            return JsonSerializer.Deserialize<QueryResult>(body, jsonOptions)
        }

    /// List rendered events matching a Seq filter expression, newest first, over an
    /// optional [from, to) window. Uses GET /api/events (purpose-built for event listing).
    member _.EventsAsync
        (
            filter: string,
            count: int,
            signal: string option,
            fromUtc: DateTime option,
            toUtc: DateTime option
        ) : Task<SeqEvent[]> =
        task {
            let parts =
                [ if not (String.IsNullOrWhiteSpace filter) then
                      yield "filter=" + Uri.EscapeDataString filter
                  match signal with
                  | Some s when not (String.IsNullOrWhiteSpace s) -> yield "signal=" + Uri.EscapeDataString s
                  | _ -> ()
                  yield "count=" + string count
                  yield "render=true"
                  match fromUtc with
                  | Some t -> yield "fromDateUtc=" + Uri.EscapeDataString(isoUtc t)
                  | None -> ()
                  match toUtc with
                  | Some t -> yield "toDateUtc=" + Uri.EscapeDataString(isoUtc t)
                  | None -> () ]

            let url = "api/events?" + String.Join("&", parts)
            let! body = send url
            return JsonSerializer.Deserialize<SeqEvent[]>(body, jsonOptions)
        }

    /// Fetch a single event by id, with rendered message and full detail.
    member _.GetEventAsync(id: string) : Task<SeqEvent> =
        task {
            let url = "api/events/" + Uri.EscapeDataString id + "?render=true"
            let! body = send url
            return JsonSerializer.Deserialize<SeqEvent>(body, jsonOptions)
        }

    /// List saved signals (shared and personal).
    member _.ListSignalsAsync() : Task<Signal[]> =
        task {
            let! body = send "api/signals?shared=true"
            return JsonSerializer.Deserialize<Signal[]>(body, jsonOptions)
        }

    /// List configured alerts (shared and owned).
    member _.ListAlertsAsync() : Task<Alert[]> =
        task {
            let! body = send "api/alerts?shared=true"
            return JsonSerializer.Deserialize<Alert[]>(body, jsonOptions)
        }

    /// Fetch a single alert by id.
    member _.GetAlertAsync(id: string) : Task<Alert> =
        task {
            let url = "api/alerts/" + Uri.EscapeDataString id
            let! body = send url
            return JsonSerializer.Deserialize<Alert>(body, jsonOptions)
        }
