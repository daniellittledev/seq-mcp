namespace SeqMcp

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open SeqMcp.SeqConfig

/// Typed wrapper over Seq's HTTP API. The target environment (server URL and API key)
/// is chosen per call, so the HttpClient carries no base address or default headers.
type SeqClient(http: HttpClient) =

    static let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    static let isoUtc (t: DateTime) =
        t.ToUniversalTime().ToString("o")

    let absoluteUrl (env: SeqEnvironment) (relativeUrl: string) =
        env.ServerUrl.TrimEnd('/') + "/" + relativeUrl

    let buildRequest (env: SeqEnvironment) (relativeUrl: string) =
        let req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl env relativeUrl)
        env.ApiKey |> Option.iter (fun key -> req.Headers.Add("X-Seq-ApiKey", key))
        req

    /// Issue a GET against the chosen environment and return the body, turning
    /// transport/HTTP failures into clear, actionable messages that name the environment.
    let send (env: SeqEnvironment) (relativeUrl: string) : Task<string> =
        task {
            try
                use req = buildRequest env relativeUrl
                use! resp = http.SendAsync req
                let! body = resp.Content.ReadAsStringAsync()

                if resp.IsSuccessStatusCode then
                    return body
                else
                    match int resp.StatusCode with
                    | 401
                    | 403 ->
                        let hint =
                            if env.ApiKey.IsSome then
                                "The SEQ__APIKEY is set but was rejected, check the key is valid and has read permission."
                            else
                                "No API key is configured, set the SEQ__APIKEY environment variable (this Seq server requires authentication)."

                        return
                            failwithf
                                "Seq environment '%s' at %s returned %d (unauthorized). %s"
                                env.Name
                                env.ServerUrl
                                (int resp.StatusCode)
                                hint
                    | code ->
                        return
                            failwithf "Seq environment '%s' at %s returned %d: %s" env.Name env.ServerUrl code (body.Trim())
            with :? HttpRequestException as ex ->
                return
                    failwithf
                        "Could not reach Seq environment '%s' at %s, check the SEQ__SERVERURL environment variable and that the server is running. (%s)"
                        env.Name
                        env.ServerUrl
                        ex.Message
        }

    /// Run a Seq SQL query over an optional [rangeStartUtc, rangeEndUtc) window,
    /// optionally scoped to a saved signal.
    member _.QueryAsync
        (
            env: SeqEnvironment,
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
            let! body = send env url
            return JsonSerializer.Deserialize<QueryResult>(body, jsonOptions)
        }

    /// List rendered events matching a Seq filter expression, newest first, over an
    /// optional [from, to) window. Uses GET /api/events (purpose-built for event listing).
    member _.EventsAsync
        (
            env: SeqEnvironment,
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
            let! body = send env url
            return JsonSerializer.Deserialize<SeqEvent[]>(body, jsonOptions)
        }

    /// Fetch a single event by id, with rendered message and full detail.
    member _.GetEventAsync(env: SeqEnvironment, id: string) : Task<SeqEvent> =
        task {
            let url = "api/events/" + Uri.EscapeDataString id + "?render=true"
            let! body = send env url
            return JsonSerializer.Deserialize<SeqEvent>(body, jsonOptions)
        }

    /// List saved signals (shared and personal).
    member _.ListSignalsAsync(env: SeqEnvironment) : Task<Signal[]> =
        task {
            let! body = send env "api/signals?shared=true"
            return JsonSerializer.Deserialize<Signal[]>(body, jsonOptions)
        }
