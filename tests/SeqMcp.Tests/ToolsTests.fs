module SeqMcp.Tests.ToolsTests

open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open SeqMcp
open SeqMcp.SeqConfig

type private RecordingHandler() =
    inherit HttpMessageHandler()
    member val Hosts: string list = [] with get, set

    override this.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> =
        this.Hosts <- this.Hosts @ [ request.RequestUri.Host ]

        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        Task.FromResult response

let private envs =
    let make name url =
        { Name = name
          ServerUrl = url
          ApiKey = None }

    { ByName =
        Map.ofList
            [ "prod", make "prod" "http://prod.seq"
              "local", make "local" "http://local.seq" ]
      Default = "prod" }

let private seqQuery (environment: string) =
    let handler = new RecordingHandler()
    let tools = SeqTools(new SeqClient(new HttpClient(handler)), envs)
    let text = tools.SeqQuery("select 1", null, null, null, environment).GetAwaiter().GetResult()
    handler.Hosts, text

[<Fact>]
let ``no environment routes to the default`` () =
    Assert.Equal<string list>([ "prod.seq" ], fst (seqQuery null))

[<Fact>]
let ``named environment routes to that environment`` () =
    Assert.Equal<string list>([ "local.seq" ], fst (seqQuery "local"))

[<Fact>]
let ``environment name is case-insensitive`` () =
    Assert.Equal<string list>([ "local.seq" ], fst (seqQuery "LOCAL"))

[<Fact>]
let ``unknown environment returns the error text without calling Seq`` () =
    let hosts, text = seqQuery "stage"
    Assert.Empty hosts
    Assert.Contains("Unknown environment 'stage'", text)
