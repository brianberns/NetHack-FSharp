namespace NetHack.Agent

open System
open System.Reflection
open System.Text

open Microsoft.Extensions.Configuration

open NetHack.Api

module Program =

    let model = Gemini.flash

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let engine = Native.create ()

    /// Runs the game from the given state.
    let rec run state prevActionOpt notes =
        async {
            try
                    // get agent's action
                let! aa =
                    let prompt =
                        Prompt.getPrompt state prevActionOpt notes
                    Agent.getResultAsync<AgentAction> prompt agent

                    // display the state prior to applying the action
                View.render state notes aa

                    // update game state and notes
                let state =
                    engine.Step state (AgentAction.toAction aa)
                let notes = AgentAction.updateNotes aa notes

                    // play another turn?
                if state.Over then return ()
                else return! run state (Some aa) notes

            with exn ->
                match model.TryParseWaitTime exn with
                    | Some duration ->
                        printfn $"Waiting {duration}"
                        do! Async.Sleep(duration)
                        return! run state prevActionOpt notes
                    | None ->
                        printfn $"{exn.Message}"
        }

    do
        Console.OutputEncoding <- Encoding.UTF8

            // start a new game
        let state =
            { NewGame.defaults with
                Name = Some model.Name }
                |> engine.Start

            // run the game and wait for it to finish
        run state None Array.empty
            |> Async.RunSynchronously
