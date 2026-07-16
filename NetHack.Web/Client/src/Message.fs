namespace NetHack.Web

open System
open Fable.Core
open Elmish

type State = Result<Option<GameState>, string>

type Message =
    | Update of State

module Message =

    let private getGameState =
        let get () =
            async {
                match! Remoting.getGameState () with
                    | Ok gameState -> return Ok (Some gameState)
                    | Error error -> return Error error
            }
        Cmd.OfAsync.perform get () Update

    let init () =
        Ok None, getGameState

    let update msg (state : State) =
        match msg with
            | Update state -> state, Cmd.none
