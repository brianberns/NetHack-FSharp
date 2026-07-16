namespace NetHack.Web

open System
open System.Reflection
open System.Threading

open Microsoft.Extensions.Configuration

open Fable.Remoting.Server
open Fable.Remoting.Suave

open NetHack.Agent
open NetHack.Api

type AsyncLock() =

    let semaphore = new SemaphoreSlim(1, 1)
    
    member _.LockAsync() =
        task {
            do! semaphore.WaitAsync()
            return {
                new IDisposable with
                    member _.Dispose() =
                        semaphore.Release() |> ignore }
        } |> Async.AwaitTask

module Api =

    let private model = OpenRouter.model

    let private agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let private engine = Native.create ()

    type private HiddenState =
        {
            GameState : GameState
            AgentAction : AgentAction
            Notes : Note[]
        }

    module private HiddenState =

        let toSessionState hidden =
            {
                Observation = hidden.GameState.Observation
                Pending = hidden.GameState.Pending
                CurrentNotes = hidden.Notes
                RelevantNotes =
                    hidden.AgentAction.RelevantNotes
                        |> Array.map (fun id -> id - 1)
                NotesToDelete =
                    hidden.AgentAction.NotesToDelete
                        |> Array.map (fun id -> id - 1)
                NotesToAdd =
                    hidden.AgentAction.NotesToAdd
                        |> Array.map Note.create
                Action =
                    Prompt.getActionDesc hidden.AgentAction
                Prediction = hidden.AgentAction.Prediction
            }

    let private act state prevActionOpt notes =
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

                return Ok {
                    GameState = state
                    AgentAction = aa
                    Notes = notes
                }

            with exn ->
                printfn $"{exn.Message}"
                return Error exn.Message
        }

    let mutable private hiddenStates = ResizeArray<HiddenState>()

    let private asyncLock = new AsyncLock()

    let private getStateCount () =
        async {
            use! _ = asyncLock.LockAsync()
            return hiddenStates.Count
        }

    let private getSessionState stateIdx =
        async {
            use! _ = asyncLock.LockAsync()

                // need to take an action?
            if stateIdx >= hiddenStates.Count then

                let gameState, prevActionOpt, notes =

                        // start a new game?
                    if hiddenStates.Count = 0 then
                        let gameState =
                            { NewGame.defaults with
                                Name = Some model.Name }
                                |> engine.Start
                        gameState, None, Array.empty

                        // use latest state
                    else
                        let hidden = hiddenStates[hiddenStates.Count - 1]
                        hidden.GameState,
                        Some hidden.AgentAction,
                        hidden.Notes

                    // get agent's action
                match! act gameState prevActionOpt notes with
                    | Ok hidden ->
                        hiddenStates.Add(hidden)
                        let sessionState = HiddenState.toSessionState hidden
                        return Ok sessionState
                    | Error msg ->
                        return Error msg

                // can return an existing state
            else
                let hidden =
                    let stateIdx =
                        stateIdx
                            |> min (hiddenStates.Count - 1)
                            |> max 0
                    hiddenStates[stateIdx]
                let sessionState = HiddenState.toSessionState hidden
                return Ok sessionState
        }

    /// NetHack API.
    let netHackApi =
        {
            GetStateCount = getStateCount
            GetSessionState = getSessionState
        }

module Remoting =

    /// Build API.
    let webPart (dir : string) =
        Remoting.createApi()
            |> Remoting.fromValue Api.netHackApi
            |> Remoting.buildWebPart
