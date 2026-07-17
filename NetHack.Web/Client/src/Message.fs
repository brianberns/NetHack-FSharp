namespace NetHack.Web

open System

open Browser.Dom
open Browser.Types

open Elmish

/// State of the user interface.
type InnerState =
    {
        /// Session state from server.
        SessionState : SessionState

        /// Index of this session state.
        StateIdx : int

        /// Is the server still working on the next state?
        Busy : bool
    }

module InnerState =

    /// Creates an inner state.
    let create sessionState stateIdx busy =
        {
            SessionState = sessionState
            StateIdx = stateIdx
            Busy = busy
        }

/// State of the user interface.
type State =
    Result<
        Option<InnerState>,
        string (*error message*)>

/// Elmish message.
type Message =

    /// Sets the UI state.
    | SetState of State

    /// Requests next UI state from server.
    | GetNextState

module Message =

    /// Requests the given state from the server.
    let private getState stateIdx =
        async {
            match! Remoting.getSessionState stateIdx with
                | Ok sessionState ->
                    return InnerState.create
                        sessionState stateIdx false
                        |> Some
                        |> Ok
                | Error error ->
                    return Error error
        }

    /// Requests the most recent session state available from
    /// the server.
    let private getCurrentState =
        let get () =
            async {
                match! Remoting.getStateCount () with
                    | Ok nStates ->
                        let idx = max (nStates - 1) 0
                        return! getState idx
                    | Error error ->
                        return Error error
            }
        Cmd.OfAsync.perform get () SetState

    /// Gets initial state and triggers request for most recent
    /// server state.
    let init () =
        Ok None, getCurrentState

    /// Requests the next session state from the server.
    let private getNextState stateIdx =
        Cmd.OfAsync.perform
            getState
            (stateIdx + 1)
            SetState

    /// Updates the user interface based on the given message.
    let update msg (state : State) =
        match msg with

                // set the UI state
            | SetState state -> state, Cmd.none

                // request the next UI state from the server
            | GetNextState ->
                match state with

                        // initiate request
                    | Ok (Some inner) when not inner.Busy ->
                        Ok (Some { inner with Busy = true }),
                        getNextState inner.StateIdx

                        // ignore messages while waiting on server
                    | _ -> state, Cmd.none

    /// Subscribes to the Enter key regardless of where the focus is.
    let subscribe (_ : State) : Sub<Message> =

        /// Starts subscription.
        let start dispatch =

            /// Dispatches a key-down event.
            let onKeyDown (evt : Event) =
                let keyEvt = unbox<KeyboardEvent> evt
                match keyEvt.key with
                    | "Enter" | " " -> dispatch GetNextState
                    | _ -> ()

                // listen for keydown events
            window.addEventListener("keydown", onKeyDown)
            {
                new IDisposable with
                    member _.Dispose() =
                        window.removeEventListener("keydown", onKeyDown)
            }

        [ [ "keydown" ], start ]
