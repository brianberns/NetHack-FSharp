namespace NetHack.Web

open Fable.Remoting.Server
open Fable.Remoting.Suave

open NetHack.Api

module Api =

    let engine = Native.create ()
    let nativeState =
        engine.Start {
            NewGame.defaults
                with Name = Some "Gemini"
        }

    /// NetHack API.
    let netHackApi dir =
        {
            GetGameState =
                fun () ->
                    async {
                        return {
                            Observation = nativeState.Observation
                            Pending = nativeState.Pending
                            Over = nativeState.Over
                        }
                    }
        }

module Remoting =

    /// Build API.
    let webPart dir =
        Remoting.createApi()
            |> Remoting.fromValue (Api.netHackApi dir)
            |> Remoting.buildWebPart
