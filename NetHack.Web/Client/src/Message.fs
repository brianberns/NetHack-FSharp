namespace NetHack.Web

open Elmish

type InnerState =
    {
        SessionState : SessionState
        StateIdx : int
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
                    | Ok (Some inner) ->
                        state, getNextState inner.StateIdx
                    | _ -> state, Cmd.none
