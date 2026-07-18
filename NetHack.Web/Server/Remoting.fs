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

    /// Creates a NetHack-playing agent.
    let private createAgent dir =
        let config =
            ConfigurationBuilder()
                .SetBasePath(dir)                               // web part's own directory
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddJsonFile("secrets.json", optional = true)   // hosted deployment (e.g. Everleap)
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

    /// Last time agent was invoked.
    let mutable private lastAgentCallTime =
        DateTime.MinValue

    /// Minimum time between agent calls.
    let private minAgentDelay =
        TimeSpan.FromMinutes(1.0)

    /// Asynchronous lock.
    let private asyncLock = new AsyncLock()

    /// Gets the current number of session states.
    let private getStateCount () =
        async {
            use! _ = asyncLock.LockAsync()
            return hiddenStates.Count
        }

    /// Invokes agent to generate the next session state.
    let private generateNextState agent =
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
                        |> AgentAction.step engine curGameState

                    // update the agent's database of notes
                curNotes <-
                    assert(curNotes = hidden.Notes)
                    AgentAction.updateNotes
                        hidden.AgentAction hidden.Notes

                lastAgentCallTime <- DateTime.UtcNow

                return Ok (HiddenState.toSessionState hidden)

            with exn ->
                printfn $"{exn.Message}"
                return Error exn.Message
        }

    /// Enforces a wait.
    let private enforceWait waitSecs =
        let msg =
            let plural = if waitSecs = 1 then "" else "s"
            $"!Next turn available in {waitSecs} second{plural}."
        if hiddenStates.Count = 0 then
            Error msg
        else
            let hidden = Seq.last hiddenStates
            let gameState =
                AgentAction.setMessage msg hidden.GameState
            { hidden with GameState = gameState }
                |> HiddenState.toSessionState
                |> Ok

    /// Gets the session state at the given index.
    let private getSessionState agent stateIdx =
        async {
            use! _ = asyncLock.LockAsync()

                // prepare to compute wait time
            let waitSecs =
                lazy ((minAgentDelay
                    - (DateTime.UtcNow - lastAgentCallTime))
                    .TotalSeconds
                    |> int)

                // can return an existing state?
            if stateIdx < hiddenStates.Count then
                let stateIdx = max stateIdx 0
                let hidden = hiddenStates[stateIdx]
                return Ok (HiddenState.toSessionState hidden)

                // game is over?
            elif curGameState.Over then
                return Error "Game is over"

                // must wait?
            elif waitSecs.Value > 0 then
                return enforceWait waitSecs.Value

                // generate next state
            else
                return! generateNextState agent
        }

    /// NetHack API.
    let netHackApi dir =
        let agent = createAgent dir
        {
            GetStateCount = getStateCount
            GetSessionState = getSessionState agent
        }

module Remoting =

    /// Build API.
    let webPart (dir : string) =
        Remoting.createApi()
            |> Remoting.fromValue (Api.netHackApi dir)
            |> Remoting.buildWebPart
