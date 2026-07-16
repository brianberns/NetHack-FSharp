namespace NetHack.Web

open System
open Feliz

module View =

    let render (state : State) (dispatch : Message -> unit) =
        Html.p [
            prop.className "dummy"
            prop.text $"{state}"
        ]
