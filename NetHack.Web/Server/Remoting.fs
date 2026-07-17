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

    /// Gets the current number of session states.
    let private getStateCount () =
        async {
            use! _ = asyncLock.LockAsync()
            return hiddenStates.Count
        }

    let private generateNextState () =
        async {
                    // get latest agent action, if any
            let lastActionOpt =
                hiddenStates
                    |> Seq.tryLast
                    |> Option.map _.AgentAction

                // get prompt for the next state
            let prompt =
                Prompt.getPrompt curGameState lastActionOpt curNotes
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

                (*
                 * Apply the agent's action in NetHack.
                 *
                 * Note that this is currently unreversible, so we do
                 * it only after the agent has successfully responded.
                 * This means that the server generates and holds the
                 * next NetHack game state while the client is still
                 * looking at the old game state and the agent's
                 * response to it. In short, all this stateful data is
                 * an off-by-one error waiting to happen.
                 *)
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

    /// Gets the session state at the given index.
    let private getSessionState stateIdx =
        async {
            use! _ = asyncLock.LockAsync()

                // can return an existing state?
            if stateIdx < hiddenStates.Count then
                let stateIdx = max stateIdx 0
                let hidden = hiddenStates[stateIdx]
                return Ok (HiddenState.toSessionState hidden)

                // game is over?
            elif curGameState.Over then
                return Error "Game is over"

                // generate next state
            else
                return! generateNextState ()
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
