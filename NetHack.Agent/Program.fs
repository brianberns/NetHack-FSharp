namespace NetHack.Agent

open System
open System.IO
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

    /// Parses a compass/vertical direction, if possible.
    let tryParseDirection (text : string) =
        match text.ToLowerInvariant() with
            | "n" | "north" -> Some North
            | "s" | "south" -> Some South
            | "e" | "east"  -> Some East
            | "w" | "west"  -> Some West
            | "ne" | "northeast" -> Some Northeast
            | "nw" | "northwest" -> Some Northwest
            | "se" | "southeast" -> Some Southeast
            | "sw" | "southwest" -> Some Southwest
            | "up"   | "<" -> Some Up
            | "down" | ">" -> Some Down
            | _ -> None

    /// Converts the model's action into a strongly-typed NetHack.Api
    /// action.
    let toAction (aa : AgentAction) =

        let value =
            (if isNull aa.Value then ""
            else aa.Value).Trim()

        match aa.Kind with

            | ActionKind.Move ->
                tryParseDirection value
                    |> Option.map Move
                    |> Option.defaultValue (Key 's')

            | ActionKind.Run ->
                tryParseDirection value
                    |> Option.map Run
                    |> Option.defaultValue (Key 's')

            | ActionKind.Key ->
                if value.Length = 0 then
                    Proceed
                elif aa.Count >= 2 then
                    RepeatKey(aa.Count, value[0])
                else
                    Key value[0]

            | ActionKind.Answer ->
                Answer (
                    if value.Length > 0 then value[0]
                    else 'y')

            | ActionKind.Text ->
                Text value

            | ActionKind.Extended ->
                Extended value

            | ActionKind.Cancel ->
                Cancel

            | ActionKind.Number ->
                match Int32.TryParse(value) with
                    | true, n -> Number n
                    | _ -> Number 0

            | ActionKind.Select ->
                value
                    |> Seq.where (Char.IsWhiteSpace >> not)
                    |> Seq.toList
                    |> Choose

            | _ ->
                Proceed

    /// Updates the given note database.
    let updateNotes (aa : AgentAction) (notes : _[]) =

        let toIdxSet = Seq.map (fun id -> id - 1) >> set
        let deleteIdxs = toIdxSet aa.NotesToDelete
        let relevantIdxs = toIdxSet aa.RelevantNotes

        let kept =
            notes
                |> Array.indexed
                |> Array.choose (fun (idx, note) ->
                    if deleteIdxs.Contains(idx) then       // delete note?
                        None
                    elif relevantIdxs.Contains(idx) then   // reset note's age?
                        Some { note with Age = 0 }
                    elif note.Age < 10 then                // increment note's age?
                        Some { note with Age = note.Age + 1 }
                    else                                   // note aged out
                        None)
        [|
            yield! kept
            yield! Array.map Note.create aa.NotesToAdd
        |]

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
                let state = engine.Step state (toAction aa)
                let notes = updateNotes aa notes

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
