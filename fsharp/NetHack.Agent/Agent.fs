namespace NetHack.Agent

open System
open System.ClientModel

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

/// Chat model.
type Model =
    {
        /// Model name.
        Name : string

        /// Model ID.
        Id : string

        /// Name of API key in config.
        ApiKeyName : string

        /// Modle endpoint.
        Endpoint : string

        /// Model supports the native `json_schema` response
        /// format? If not, wrapper must fall back to embedding
        /// the schema in the prompt.
        SupportsJsonSchema : bool
    }

module Gemini =

    let private supportsJsonSchema = true

    let flash2_5 =
        {
            Name = "Gemini 2.5 Flash"
            Id = "gemini-2.5-flash"
            ApiKeyName = "Gemini:ApiKey"
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"
            SupportsJsonSchema = true
        }

/// Decision-making agent.
type Agent =
    {
        /// .NET wrapper around model API.
        ChatClient : IChatClient

        /// Model-specifc details.
        Model : Model
    }

    /// Cleanup.
    member this.Dispose() =
        this.ChatClient.Dispose()

    interface IDisposable with

        /// Cleanup.
        member this.Dispose() = this.Dispose()

module Agent =

    (*
    // e.g. "try again in 3m10.7712s"
    let private tryParseWaitTime text =
        let m = Regex.Match(text, @"try again in ([\d.hms]+)")
        if m.Success then
            Regex.Matches(m.Groups[1].Value, @"([\d.]+)(ms|h|m|s)")
                |> Seq.map (fun m ->
                    let value = Double.Parse(m.Groups[1].Value)
                    match m.Groups[2].Value with
                        | "h"  -> TimeSpan.FromHours value
                        | "m"  -> TimeSpan.FromMinutes value
                        | "ms" -> TimeSpan.FromMilliseconds value
                        | _    -> TimeSpan.FromSeconds value)
                |> Seq.reduce (+)
                |> Some
        else None

    let private tryPrettyJson (text : string) =
        try
            use doc = JsonDocument.Parse(text)
            let pretty =
                JsonSerializer.Serialize(
                    doc.RootElement,
                    JsonSerializerOptions(
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))   // avoid escaping Unicode characters
            Some pretty
        with :? JsonException -> None
    *)

    /// Creates an agent.
    let create (config : IConfiguration) model =
        let openAIClient =
            OpenAIClient(
                ApiKeyCredential(config[model.ApiKeyName]),
                OpenAIClientOptions(
                    Endpoint = Uri(model.Endpoint)))
        let chatClient =
            openAIClient
                .GetChatClient(model.Id)
                .AsIChatClient()
        {
            ChatClient = chatClient
            Model = model
        }

    /// Prompts the agent to respond with a specific type
    /// of data.
    let getResultAsync<'t> (prompt : string) agent =
        task {
            let! response =
                ChatClientStructuredOutputExtensions
                    .GetResponseAsync<'t>(
                        agent.ChatClient,
                        prompt,
                        useJsonSchemaResponseFormat =
                            agent.Model.SupportsJsonSchema)
            return response.Result
        } |> Async.AwaitTask
