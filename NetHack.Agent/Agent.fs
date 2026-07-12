namespace NetHack.Agent

open System
open System.ClientModel
open System.Text.RegularExpressions

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

open FSharp.Data
open FSharp.Data.JsonExtensions

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

        /// Looks for an embedded wait time when a 429 (too many
        /// requests) exception occurs.
        TryParseWaitTime : Exception -> Option<TimeSpan>
    }

module Gemini =

    let tryParseTime text =
        let mtch = Regex.Match(text, @"([\d.]+)(ms|h|m|s)")
        if mtch.Success then
            let value = Double.Parse(mtch.Groups[1].Value)
            match mtch.Groups[2].Value with
                | "h"  -> Some (TimeSpan.FromHours(value))
                | "m"  -> Some (TimeSpan.FromMinutes(value))
                | "ms" -> Some (TimeSpan.FromMilliseconds(value))
                | "s"  -> Some (TimeSpan.FromSeconds(value))
                | _    -> None
        else None

    let rec private tryParseWaitTime (exn : Exception) =
        match exn with

            | :? ClientResultException as exn ->
                try
                    let root =
                        exn.GetRawResponse()
                            .Content
                            .ToString()
                            |> JsonValue.Parse
                    root.AsArray()
                        |> Array.tryPick (fun value ->
                            value?error?details.AsArray()
                                |> Array.tryPick (fun detail ->
                                    detail.Properties
                                        |> Array.tryPick (fun (key, value) ->
                                            if key = "retryDelay" then Some value
                                            else None)))
                        |> Option.bind (fun value ->
                            value.ToString() |> tryParseTime)
                with _ -> None

            | _ ->
                if exn.InnerException = null then None
                else tryParseWaitTime exn.InnerException

    let flash =
        {
            Name = "Gemini"
            Id = "gemini-3-flash-preview"
            ApiKeyName = "Gemini:ApiKey"
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/"
            SupportsJsonSchema = true
            TryParseWaitTime = tryParseWaitTime
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
