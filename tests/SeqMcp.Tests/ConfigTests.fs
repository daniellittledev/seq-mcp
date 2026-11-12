module SeqMcp.Tests.ConfigTests

open Microsoft.Extensions.Configuration
open Xunit
open SeqMcp.SeqConfig

let private build pairs =
    ConfigurationBuilder().AddInMemoryCollection(dict pairs).Build() :> IConfiguration

let private okOrFail =
    function
    | Ok v -> v
    | Error e -> failwithf "expected Ok, got Error: %s" e

let private errOrFail =
    function
    | Error e -> e
    | Ok _ -> failwith "expected Error, got Ok"

let private twoEnvs =
    [ "Seq:Environments:prod:ServerUrl", "https://seq.example.com"
      "Seq:Environments:prod:ApiKey", "secret"
      "Seq:Environments:local:ServerUrl", "http://localhost:5341" ]

[<Fact>]
let ``parse reads named environments with explicit default`` () =
    let envs = build (("Seq:DefaultEnvironment", "local") :: twoEnvs) |> parse |> okOrFail
    Assert.Equal<string list>([ "local"; "prod" ], envs.ByName |> Map.keys |> List.ofSeq)
    Assert.Equal("local", envs.Default)
    Assert.Equal("https://seq.example.com", envs.ByName["prod"].ServerUrl)
    Assert.Equal(Some "secret", envs.ByName["prod"].ApiKey)
    Assert.Equal(None, envs.ByName["local"].ApiKey)

[<Fact>]
let ``parse lower-cases upper-cased section keys`` () =
    let envs = build [ "Seq:Environments:PROD:ServerUrl", "https://seq.example.com" ] |> parse |> okOrFail
    Assert.True(envs.ByName.ContainsKey "prod")

[<Fact>]
let ``parse synthesises prod from legacy flat keys`` () =
    let envs =
        build [ "Seq:ServerUrl", "https://seq.example.com"; "Seq:ApiKey", "k" ] |> parse |> okOrFail
    Assert.Equal<string list>([ "prod" ], envs.ByName |> Map.keys |> List.ofSeq)
    Assert.Equal("prod", envs.Default)
    Assert.Equal(Some "k", envs.ByName["prod"].ApiKey)

[<Fact>]
let ``parse fails when nothing is configured`` () =
    let msg = build [] |> parse |> errOrFail
    Assert.Contains("SEQ__ENVIRONMENTS", msg)

[<Fact>]
let ``parse fails when default is not configured`` () =
    let msg = build (("Seq:DefaultEnvironment", "stage") :: twoEnvs) |> parse |> errOrFail
    Assert.Contains("stage", msg)
    Assert.Contains("local", msg)
    Assert.Contains("prod", msg)

[<Fact>]
let ``parse fails on ambiguous default`` () =
    let cfg =
        build
            [ "Seq:Environments:a:ServerUrl", "http://a"
              "Seq:Environments:b:ServerUrl", "http://b" ]
    let msg = cfg |> parse |> errOrFail
    Assert.Contains("a, b", msg)

[<Fact>]
let ``parse defaults to prod among several`` () =
    Assert.Equal("prod", (build twoEnvs |> parse |> okOrFail).Default)

[<Fact>]
let ``parse defaults to the only environment`` () =
    let envs = build [ "Seq:Environments:local:ServerUrl", "http://localhost:5341" ] |> parse |> okOrFail
    Assert.Equal("local", envs.Default)

let private envs () = build twoEnvs |> parse |> okOrFail

[<Fact>]
let ``resolve None gives default`` () =
    Assert.Equal("prod", (resolve (envs ()) None |> okOrFail).Name)

[<Fact>]
let ``resolve is case-insensitive`` () =
    Assert.Equal("local", (resolve (envs ()) (Some "LOCAL") |> okOrFail).Name)

[<Fact>]
let ``resolve blank gives default`` () =
    Assert.Equal("prod", (resolve (envs ()) (Some "   ") |> okOrFail).Name)

[<Fact>]
let ``resolve unknown is an error`` () =
    let msg = resolve (envs ()) (Some "stage") |> errOrFail
    Assert.Contains("Unknown environment 'stage'", msg)
    Assert.Contains("prod", msg)

[<Fact>]
let ``parse errors naming the environment when a named environment has no ServerUrl`` () =
    let message =
        build [ "Seq:Environments:staging:ServerUrl", "  "; "Seq:Environments:staging:ApiKey", "k" ]
        |> parse
        |> errOrFail

    Assert.Contains("staging", message)

[<Fact>]
let ``parse errors naming the environment when a ServerUrl is not a valid absolute URL`` () =
    let message =
        build [ "Seq:Environments:prod:ServerUrl", "seq.example.com" ]
        |> parse
        |> errOrFail

    Assert.Contains("prod", message)
    Assert.Contains("seq.example.com", message)
