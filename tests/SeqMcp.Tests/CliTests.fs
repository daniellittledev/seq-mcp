module SeqMcp.Tests.CliTests

open Xunit
open SeqMcp

let private parse = Cli.parseCommand

let private error =
    function
    | Error (msg: string) -> msg
    | Ok _ -> failwith "expected a parse error"

[<Fact>]
let ``parses positionals and options`` () =
    match parse [ "search"; "StatusCode = 500"; "--count"; "5"; "--env"; "local" ] with
    | Ok p ->
        Assert.Equal("search", p.spec.name)
        Assert.Equal<string list>([ "StatusCode = 500" ], p.args)
        Assert.Equal(Some "5", Map.tryFind "count" p.flags)
        Assert.Equal(Some "local", Map.tryFind "env" p.flags)
    | Error msg -> failwith msg

[<Fact>]
let ``options may come before positionals`` () =
    match parse [ "event"; "--env"; "local"; "event-1" ] with
    | Ok p -> Assert.Equal<string list>([ "event-1" ], p.args)
    | Error msg -> failwith msg

[<Fact>]
let ``trailing option without a value is an error, not the default environment`` () =
    Assert.Contains("'--env' needs a value", parse [ "errors"; "--env" ] |> error)

[<Fact>]
let ``option followed by another option is an error`` () =
    Assert.Contains("'--env' needs a value", parse [ "errors"; "--env"; "--minutes"; "5" ] |> error)

[<Fact>]
let ``unknown option is rejected`` () =
    Assert.Contains("Unknown option '--minute'", parse [ "errors"; "--minute"; "5" ] |> error)

[<Fact>]
let ``option from another verb is rejected`` () =
    Assert.Contains("Unknown option '--count'", parse [ "errors"; "--count"; "5" ] |> error)

[<Fact>]
let ``unknown command is rejected`` () =
    Assert.Contains("Unknown command 'bogus'", parse [ "bogus" ] |> error)

[<Fact>]
let ``missing positional is rejected`` () =
    Assert.Contains("Missing argument. Usage: seq-mcp event <id>", parse [ "event" ] |> error)

[<Fact>]
let ``unquoted multi-word filter is rejected rather than truncated`` () =
    Assert.Contains("Too many arguments", parse [ "search"; "StatusCode"; "="; "500" ] |> error)

[<Fact>]
let ``non-numeric integer option fails the CLI`` () =
    Assert.Equal(1, Cli.run [| "errors"; "--minutes"; "abc" |])

[<Fact>]
let ``zero is not a valid count`` () =
    Assert.Equal(1, Cli.run [| "search"; "x"; "--count"; "0" |])
