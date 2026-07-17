namespace NetHack.Web

open Browser.Dom
open Fable.Remoting.Client

module Remoting =

    /// Prefix routes with /NetHackWeb.
    let routeBuilder typeName methodName =
        sprintf "/NetHack/%s/%s" typeName methodName

    /// Server API.
    let api =
        Remoting.createApi()
            |> Remoting.withRouteBuilder routeBuilder
            |> Remoting.buildProxy<INetHackApi>

    /// Gets the current number of game states.
    let getStateCount () =
        async {
            match! Async.Catch(api.GetStateCount ()) with
                | Choice1Of2 nStates ->
                    return Ok nStates
                | Choice2Of2 exn ->
                    console.log(exn.Message)
                    return Error exn.Message
        }

    /// Gets the session state at the given 0-based index.
    let getSessionState stateIdx =
        async {
            match! Async.Catch(api.GetSessionState stateIdx) with
                | Choice1Of2 (Ok state) ->
                    return Ok state
                | Choice1Of2 (Error msg) ->
                    console.log(msg)
                    return Error msg
                | Choice2Of2 exn ->
                    console.log(exn.Message)
                    return Error exn.Message
        }
