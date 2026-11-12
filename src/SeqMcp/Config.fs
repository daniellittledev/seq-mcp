module SeqMcp.SeqConfig

open Microsoft.Extensions.Configuration

type SeqEnvironment =
    { Name: string
      ServerUrl: string
      ApiKey: string option }

type SeqEnvironments =
    { ByName: Map<string, SeqEnvironment>
      Default: string }

let private defaultName = "prod"

let private nonBlank (s: string) =
    if System.String.IsNullOrWhiteSpace s then None else Some s

let private orError (message: string) = function Some v -> Ok v | None -> Error message

let private normalise (name: string) = name.Trim().ToLowerInvariant()

let private names (byName: Map<string, SeqEnvironment>) =
    byName |> Map.keys |> String.concat ", "

let private toEnvironment (name: string) (section: IConfiguration) =
    nonBlank section["ServerUrl"]
    |> Option.map (fun url ->
        let key = normalise name
        key, { Name = key; ServerUrl = url; ApiKey = nonBlank section["ApiKey"] })
    |> orError $"Environment '{name}' has no ServerUrl configured."

let private readNamed (config: IConfiguration) =
    config.GetSection("Seq:Environments").GetChildren()
    |> Seq.map (fun child -> toEnvironment child.Key child)
    |> Seq.toList

let private readLegacy (config: IConfiguration) =
    nonBlank config["Seq:ServerUrl"]
    |> Option.map (fun url ->
        [ defaultName,
          { Name = defaultName
            ServerUrl = url
            ApiKey = nonBlank config["Seq:ApiKey"] } ])

let private noneConfigured =
    "No Seq environments configured. Set SEQ__ENVIRONMENTS__<NAME>__SERVERURL (and optionally SEQ__ENVIRONMENTS__<NAME>__APIKEY), e.g. SEQ__ENVIRONMENTS__PROD__SERVERURL."

let private collect (results: Result<'a, string> list) =
    results
    |> List.fold
        (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs)))
        (Ok [])
    |> Result.map List.rev

let private readEnvironments (config: IConfiguration) =
    match readNamed config with
    | [] ->
        readLegacy config
        |> Option.map Ok
        |> Option.defaultValue (Error noneConfigured)
    | named -> collect named

let private chooseDefault (config: IConfiguration) (byName: Map<string, SeqEnvironment>) =
    match nonBlank config["Seq:DefaultEnvironment"] |> Option.map normalise with
    | Some name when byName.ContainsKey name -> Ok name
    | Some name ->
        Error $"Default environment '{name}' is not configured. Configured: {names byName}."
    | None when byName.Count = 1 -> Ok(byName |> Map.keys |> Seq.head)
    | None when byName.ContainsKey defaultName -> Ok defaultName
    | None ->
        Error $"Cannot choose a default environment. Set SEQ__DEFAULTENVIRONMENT to one of: {names byName}."

let parse (config: IConfiguration) : Result<SeqEnvironments, string> =
    readEnvironments config
    |> Result.map Map.ofList
    |> Result.bind (fun byName ->
        chooseDefault config byName
        |> Result.map (fun name -> { ByName = byName; Default = name }))

let resolve (envs: SeqEnvironments) (requested: string option) : Result<SeqEnvironment, string> =
    let name = requested |> Option.bind nonBlank |> Option.map normalise |> Option.defaultValue envs.Default
    envs.ByName
    |> Map.tryFind name
    |> orError $"Unknown environment '{name}'. Configured: {names envs.ByName}."
