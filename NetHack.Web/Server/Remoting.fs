namespace NetHack.Web

open System.Reflection

open Microsoft.Extensions.Configuration

open Fable.Remoting.Server
open Fable.Remoting.Suave

open NetHack.Agent
open NetHack.Api

module Api =

    let model = OpenRouter.model

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let engine = Native.create ()

    let act state prevActionOpt notes =
        async {
            try
                    // get agent's action
                let! aa =
                    let prompt =
                        Prompt.getPrompt state prevActionOpt notes
                    Agent.getResultAsync<AgentAction> prompt agent

                    // update game state and notes
                let state =
                    engine.Step state (AgentAction.toAction aa)
                let notes = AgentAction.updateNotes aa notes

                return state, (Some aa), notes

            with exn ->
                printfn $"{exn.Message}"
                return state, prevActionOpt, notes
        }

    let mutable gameState =
        { NewGame.defaults with
            Name = Some model.Name }
            |> engine.Start

    let mutable prevActionOpt : Option<AgentAction> = None

    let mutable notesDb : Note[] = Array.empty

    let getSessionState () =
        async {
            (*
            let! state, aaOpt, notes =
                act nativeState prevActionOpt notesDb
            nativeState <- state
            prevActionOpt <- aaOpt
            notesDb <- notes
            *)
            let state = gameState
            return {
                Observation = state.Observation
                Pending = state.Pending
                CurrentNotes = [| Note.create "Test A"; Note.create "Test B" |]
                RelevantNotes = [| 0 |]
                NotesToDelete = [| 1 |]
                NotesToAdd = [| Note.create "Test C" |]
                Action = "Dummy action"
                Prediction = "Dummy prediction"
            }
        }

    /// NetHack API.
    let netHackApi =
        {
            GetSessionState = getSessionState
        }

module Remoting =

    /// Build API.
    let webPart (dir : string) =
        Remoting.createApi()
            |> Remoting.fromValue Api.netHackApi
            |> Remoting.buildWebPart
