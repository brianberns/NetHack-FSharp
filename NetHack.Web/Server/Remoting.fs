namespace NetHack.Web

open System
open System.Reflection
open System.Threading

open Microsoft.Extensions.Configuration

open Fable.Remoting.Server
open Fable.Remoting.Suave

open NetHack.Agent
open NetHack.Api

/// Asynchronous lock.
type AsyncLock() =

    let semaphore = new SemaphoreSlim(1, 1)

    /// Obtains a disposable lock. 
    member _.LockAsync() =
        task {
            do! semaphore.WaitAsync()
            return {
                new IDisposable with
                    member _.Dispose() =
                        semaphore.Release() |> ignore }
        } |> Async.AwaitTask

module Api =

    /// LLM driving the agent.
    let private model = OpenRouter.model

    /// NetHack-playing agent.
    let private agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    /// NetHack engine.
    let private engine = Native.create ()

    /// Hidden state maintained on the server.
    type private HiddenState =
        {
            /// Game state prior to agent's action.
            GameState : GameState

            /// Agent's notes prior to this game state.
            Notes : Note[]

            /// Agent's response to this game state.
            AgentAction : AgentAction
        }

    module private HiddenState =

        /// Creates a DTO from hidden state.
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

    /// Hidden state for each step.
    let mutable private hiddenStates =
        ResizeArray<HiddenState>()

    /// Current game state.
    let mutable private curGameState =
        { NewGame.defaults with
            Name = Some model.Name }
            |> engine.Start

    /// Current notes database.
    let mutable private curNotes =
        Array.empty<Note>

    /// Asynchronous lock.
    let private asyncLock = new AsyncLock()

    /// Answers the current number of steps taken.
    let private getStateCount () =
        async {
            use! _ = asyncLock.LockAsync()
            return hiddenStates.Count
        }

    /// Gets the session state at the given step index.
    let private getSessionState stateIdx =
        async {
            use! _ = asyncLock.LockAsync()

                // can return an existing state?
            if stateIdx < hiddenStates.Count then
                let stateIdx = max stateIdx 0
                let hidden = hiddenStates[stateIdx]
                return Ok (HiddenState.toSessionState hidden)

                // generate next state
            else
                    // get prompt for the next state
                let prompt =
                    let prevActionOpt =
                        hiddenStates
                            |> Seq.tryLast
                            |> Option.map _.AgentAction
                    Prompt.getPrompt curGameState prevActionOpt curNotes
                try
                        // request action from agent
                    let! aa =
                        Agent.getResultAsync<AgentAction> prompt agent

                        // save the current game state and the agent's action in that state
                    let hidden =
                        {
                            GameState = curGameState
                            Notes = curNotes
                            AgentAction = aa
                        }
                    hiddenStates.Add(hidden)

                        // apply the agent's action in NetHack
                    curGameState <-
                        hidden.AgentAction
                            |> AgentAction.toAction
                            |> engine.Step curGameState

                        // update the agent's database of notes
                    curNotes <-
                        assert(curNotes = hidden.Notes)
                        AgentAction.updateNotes
                            hidden.AgentAction hidden.Notes

                    return Ok (HiddenState.toSessionState hidden)

                with exn ->
                    printfn $"{exn.Message}"
                    return Error exn.Message
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
