namespace NetHack.Web

open Elmish

type InnerState =
    {
        SessionState : SessionState
        StateIdx : int
    }

type State = Result<Option<InnerState>, string (*error message*)>

type Message =
    | Update of State

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
                                    }
                                return Ok (Some inner)
                            | Error error ->
                                return Error error
                    | Error error ->
                        return Error error
            }
        Cmd.OfAsync.perform get () Update

    let init () =
        Ok None, getInitialState

    let update msg (state : State) =
        match msg with
            | Update state -> state, Cmd.none
