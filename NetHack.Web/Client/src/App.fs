namespace NetHack.Web

open Elmish
open Elmish.React

module App =

        // start Elmish message loop
    Program.mkProgram Message.init Message.update View.render
        |> Program.withSubscription Message.subscribe
        |> Program.withReactSynchronous "elmish-app"
        |> Program.run
