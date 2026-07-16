namespace NetHack.Web

open Browser.Dom
open Fable.Remoting.Client

module Remoting =

    /// Prefix routes with /NetHackWeb.
    let routeBuilder typeName methodName = 
        sprintf "/NetHackWeb/%s/%s" typeName methodName

    /// Server API.
    let api =
        Remoting.createApi()
            |> Remoting.withRouteBuilder routeBuilder
            |> Remoting.buildProxy<INetHackApi>

    let getGameState () =
        async {
            match! Async.Catch(api.GetSessionState ()) with
                | Choice1Of2 state ->
                    return Ok state
                | Choice2Of2 exn ->
                    console.log(exn.Message)
                    return Error exn.Message
        }
