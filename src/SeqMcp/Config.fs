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

/// Build an environment from a name, server URL and optional key, validating the URL so a
/// misconfigured ServerUrl fails fast at startup with an environment-named message, rather
/// than surfacing later as a generic URI error on the first request.
let private makeEnvironment (name: string) (url: string) (apiKey: string option) : Result<string * SeqEnvironment, string> =
    match System.Uri.TryCreate(url, System.UriKind.Absolute) with
    | true, uri when uri.Scheme = System.Uri.UriSchemeHttp || uri.Scheme = System.Uri.UriSchemeHttps ->
        let key = normalise name
        Ok(key, { Name = key; ServerUrl = url; ApiKey = apiKey })
    | _ ->
        Error $"Environment '{name}' has an invalid ServerUrl '{url}'. Use an absolute http(s) URL, e.g. https://seq.example.com."

let private toEnvironment (name: string) (section: IConfiguration) =
    match nonBlank section["ServerUrl"] with
    | Some url -> makeEnvironment name url (nonBlank section["ApiKey"])
    | None -> Error $"Environment '{name}' has no ServerUrl configured."

let private readNamed (config: IConfiguration) =
    config.GetSection("Seq:Environments").GetChildren()
    |> Seq.map (fun child -> toEnvironment child.Key child)
    |> Seq.toList

let private readLegacy (config: IConfiguration) : Result<(string * SeqEnvironment) list, string> option =
    nonBlank config["Seq:ServerUrl"]
    |> Option.map (fun url ->
        makeEnvironment defaultName url (nonBlank config["Seq:ApiKey"]) |> Result.map List.singleton)

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
