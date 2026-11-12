module SeqMcp.Tests.SeqClientTests

open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open SeqMcp
open SeqMcp.SeqConfig

type private StubHandler(body: string) =
    inherit HttpMessageHandler()
    member val LastRequest: HttpRequestMessage = null with get, set

    override this.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> =
        this.LastRequest <- request

        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        Task.FromResult response

type private FailingHandler(respond: unit -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(_: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> =
        Task.FromResult(respond ())

let private env url key =
    { Name = "test"
      ServerUrl = url
      ApiKey = key }

let private listSignals e =
    let stub = new StubHandler("[]")
    let client = SeqClient(new HttpClient(stub))
    client.ListSignalsAsync(e).GetAwaiter().GetResult() |> ignore
    stub.LastRequest

[<Fact>]
let ``joins server url without trailing slash with exactly one slash`` () =
    let req = listSignals (env "https://seq.x.com" None)
    Assert.Equal("https://seq.x.com/api/signals?shared=true", string req.RequestUri)

[<Fact>]
let ``joins server url with trailing slash without doubling it`` () =
    let req = listSignals (env "https://seq.x.com/" None)
    Assert.Equal("https://seq.x.com/api/signals?shared=true", string req.RequestUri)

[<Fact>]
let ``sends the api key header when the environment has a key`` () =
    let req = listSignals (env "https://seq.x.com" (Some "k"))
    Assert.Equal<string list>([ "k" ], req.Headers.GetValues "X-Seq-ApiKey" |> List.ofSeq)

[<Fact>]
let ``omits the api key header when the environment has no key`` () =
    let req = listSignals (env "https://seq.x.com" None)
    Assert.False(req.Headers.Contains "X-Seq-ApiKey")

[<Fact>]
let ``get event targets the event path with render`` () =
    let stub = new StubHandler("{}")
    let client = SeqClient(new HttpClient(stub))
    client.GetEventAsync(env "https://seq.x.com" None, "event-1").GetAwaiter().GetResult() |> ignore
    Assert.EndsWith("/api/events/event-1?render=true", string stub.LastRequest.RequestUri)

let private errorMessageFrom handler =
    let client = SeqClient(new HttpClient(handler))
    let ex = Assert.ThrowsAny<exn>(fun () -> client.ListSignalsAsync(env "https://seq.x.com" None).GetAwaiter().GetResult() |> ignore)
    ex.Message

[<Fact>]
let ``unauthorized error names the environment and its server url`` () =
    let message = errorMessageFrom (new FailingHandler(fun () -> new HttpResponseMessage(HttpStatusCode.Unauthorized)))
    Assert.Contains("test", message)
    Assert.Contains("https://seq.x.com", message)
    Assert.Contains("SEQ__ENVIRONMENTS__TEST__APIKEY", message)

[<Fact>]
let ``connection failure names the environment and its server url`` () =
    let message = errorMessageFrom (new FailingHandler(fun () -> raise (HttpRequestException "boom")))
    Assert.Contains("test", message)
    Assert.Contains("https://seq.x.com", message)
    Assert.Contains("SEQ__ENVIRONMENTS__TEST__SERVERURL", message)
