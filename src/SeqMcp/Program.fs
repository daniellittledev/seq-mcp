module SeqMcp.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SeqMcp

/// Build the environment registry from configuration and register it with the typed Seq
/// HTTP client. The client carries no base address or default headers, so each request is
/// routed and authenticated per environment. Shared by both the HTTP and stdio hosts.
let private tryConfigureSeq (services: IServiceCollection) (config: IConfiguration) : Result<unit, string> =
    SeqConfig.parse config
    |> Result.map (fun envs ->
        services.AddSingleton<SeqConfig.SeqEnvironments>(envs) |> ignore
        services.AddHttpClient<SeqClient>() |> ignore)

/// Config errors go to stderr (stdout is the JSON-RPC stream) and exit non-zero before the host is built.
let private failConfig (msg: string) =
    eprintfn "%s" msg
    1

/// stdio transport: the MCP client (e.g. Claude Code) launches this process and speaks
/// JSON-RPC over stdin/stdout. Logs MUST go to stderr so they don't corrupt the protocol
/// stream on stdout.
let private runStdio (args: string[]) =
    let builder = Host.CreateApplicationBuilder(args)

    builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    match tryConfigureSeq builder.Services builder.Configuration with
    | Error msg -> failConfig msg
    | Ok() ->
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SeqTools>()
        |> ignore

        builder.Build().Run()
        0

/// HTTP (Streamable HTTP) transport: long-running server the MCP client connects to by URL.
let private runHttp (args: string[]) =
    let builder = WebApplication.CreateBuilder(args)

    match tryConfigureSeq builder.Services builder.Configuration with
    | Error msg -> failConfig msg
    | Ok() ->
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SeqTools>()
        |> ignore

        let app = builder.Build()
        app.MapMcp() |> ignore

        // Default the listen address to :5250 for local/dev use; respect an explicit
        // ASPNETCORE_URLS when the host sets one.
        match Environment.GetEnvironmentVariable "ASPNETCORE_URLS" with
        | null
        | "" -> app.Run "http://localhost:5250"
        | _ -> app.Run()

        0

[<EntryPoint>]
let main args =
    // Transport selection:
    //   --stdio  → MCP over stdio (for `claude mcp add ... -- seq-mcp --stdio`)
    //   --http   → MCP over HTTP (for Claude Desktop and other URL clients)
    //   neither  → run as a CLI (see Cli.specs / `seq-mcp --help`)
    if Array.contains "--stdio" args then runStdio args
    elif Array.contains "--http" args then runHttp args
    else Cli.run tryConfigureSeq args
