namespace NetHack.Web

open System

open Browser.Types
open Elmish

type InnerState =
    {
        SessionState : SessionState
        StateIdx : int

        /// Is the server still working on the next state? The agent's turn is
        /// a round trip to a language model, so it is worth saying so.
        Busy : bool
    }

type State = Result<Option<InnerState>, string (*error message*)>

type Message =
    | SetState of State
    | GetNextState

module Message =

    let private getInitialState =
        let get () =
            async {
                match! Remoting.getStateCount () with
                    | Ok nStates ->
                        let idx = max (nStates - 1) 0
                        match! Remoting.getGameState idx with
                            | Ok sessionState ->
                                let inner =
                                    {
                                        SessionState = sessionState
                                        StateIdx = idx
                                        Busy = false
                                    }
                                return Ok (Some inner)
                            | Error error ->
                                return Error error
                    | Error error ->
                        return Error error
            }
        Cmd.OfAsync.perform get () SetState

    let init () =
        Ok None, getInitialState

    let private getNextState stateIdx =
        let get () =
            async {
                let idx = stateIdx + 1
                match! Remoting.getGameState idx with
                    | Ok sessionState ->
                        let inner =
                            {
                                SessionState = sessionState
                                StateIdx = idx
                                Busy = false
                            }
                        return Ok (Some inner)
                    | Error error ->
                        return Error error
            }
        Cmd.OfAsync.perform get () SetState

    let update msg (state : State) =
        match msg with
            | SetState state -> state, Cmd.none
            | GetNextState ->
                match state with

                        // keep showing this state, marked as busy, until the
                        // server answers with the next one
                    | Ok (Some inner) when not inner.Busy ->
                        Ok (Some { inner with Busy = true }),
                        getNextState inner.StateIdx

                        // already waiting on a turn, so let it finish
                    | _ -> state, Cmd.none

    /// Enter takes the next turn, wherever the focus happens to be. The button
    /// answers to Enter by itself, but only while focused, and taking a turn
    /// disables it, which drops the focus.
    let subscribe (_ : State) : Sub<Message> =
        let start dispatch =
            let onKeyDown (evt : Event) =
                if (unbox<KeyboardEvent> evt).key = "Enter" then
                    dispatch GetNextState
            Browser.Dom.window.addEventListener("keydown", onKeyDown)
            {
                new IDisposable with
                    member _.Dispose() =
                        Browser.Dom.window.removeEventListener(
                            "keydown", onKeyDown)
            }
        [ [ "keydown" ], start ]
