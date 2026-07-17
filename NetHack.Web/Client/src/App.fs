namespace NetHack.Web

open System

open Fable.Core

open Feliz
open Elmish
open Elmish.React

module App =

    Program.mkProgram Message.init Message.update View.render
        |> Program.withSubscription Message.subscribe
        |> Program.withReactSynchronous "elmish-app"
        |> Program.withConsoleTrace
        |> Program.run
