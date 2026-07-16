namespace NetHack.Web

open System

open Fable.Core

open Feliz
open Elmish
open Elmish.React

module App =

    Program.mkProgram Message.init Message.update View.render
        |> Program.withReactSynchronous "elmish-app"
        |> Program.withConsoleTrace
        |> Program.run
