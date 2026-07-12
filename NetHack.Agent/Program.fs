namespace NetHack.Agent

open System
open System.ComponentModel
open System.IO
open System.Reflection
open System.Text
open System.Text.Json.Serialization

open Microsoft.Extensions.Configuration

open NetHack.Api

/// The kinds of action the model may choose. As a string enum this schematizes
/// to a constrained set the structured-output layer enforces exactly.
[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type ActionKind =
    | Move = 0
    | Key = 1
    | Answer = 2
    | Text = 3
    | Number = 4
    | Select = 5
    | Proceed = 6
    | Extended = 7
    | Cancel = 8
    | Run = 9

/// Action DTO the model returns each turn.
type AgentAction =
    {
        [<Description("The kind of action to take.")>]
        Kind : ActionKind

        [<Description("Argument for the action. \
            Move/Run: one of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: a single character. \
            Text: the line to type. \
            Number: an integer. \
            Select: the menu letters (e.g. 'a' or 'ac'). \
            Extended: an extended command name (e.g. loot, pray, etc.). \
            Proceed/Cancel: ignored.")>]
        Value : string

        [<Description("Optional repeat count for a Key command, such \
            as 's' (search) or '.' (rest).")>]
        Count : int

        [<Description("A sentence quantifying the expected result of \
            this action, such as the hero's expected new location.")>]
        Prediction : string

        [<Description("Optional new memory that describes something \
        you've learned or decided on this turn. This memory will carry \
        over to subsequent turns until you delete it.")>]
        MemoryToAdd : string

        [<Description("IDs of memories to delete because they are now \
        incorrect or irrelevant.")>]
        MemoriesToDelete : int[]
    }

module Program =

    /// Provides guidance for responding to a prompt.
    let getGuidance = function
        | Direction _ ->
            "Specify a direction via Kind=Move, or Kind=Cancel to \
            back out."
        | MultiChoice(_, choices, _) ->
            let desc =
                if choices = "" then "one of the characters offered"
                else $"one character from '{choices}'"
            $"Reply Kind=Answer with Value set to {desc}, or Kind=Cancel \
            to back out."
        | Quantity _ ->
            "Specify a quantity via Kind=Number, or Kind=Cancel to \
            back out."
        | TextLine _ ->
            "Reply Kind=Text, or Kind=Cancel to back out."
        | Menu(_, PickNone, _) ->
            "Reply Kind=Proceed to dismiss the menu."
        | Menu _ ->
            "Reply Kind=Select with the item letters, or Kind=Cancel to \
            cancel."
        | More ->
            "Reply Kind=Proceed to continue."
        | Command ->
            "Reply with a command: Kind=Move (one step), Kind=Run (travel \
            a direction until something notable — use this to cross \
            corridors and rooms efficiently), Kind=Key for a command key \
            (with Count to repeat), or Kind=Extended."
        | GameOver _ ->
            "The game is over."

    /// Creates a prompt for the agent based on the current state.
    let getPrompt (state : GameState) prediction (memories : _[]) =
        String.concat "\n" [
            "You are an expert NetHack player controlling a character."
            "The current game state (JSON):"
            Json.toJson state
            getGuidance state.Pending
            if not (String.IsNullOrWhiteSpace(prediction)) then
                "Your prediction from last turn of what the current game \
                state should be:"
                prediction
                "Compare this prediction against reality to determine \
                if your current plan is working."
            if memories.Length > 0 then
                $"Your memories:"
                for i = 0 to memories.Length - 1 do
                    $"ID {i+1}: %s{memories[i]}"
        ]

    let model = Gemini.flash

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let engine = Native.create ()

    /// Expands any ASCII control characters in the given text.
    let expandCtrl (text : string) =
        String.concat "" [
            for c in text do
                let value = int c
                if value >= 1 && value <= 26 then
                    let letter = char (value + 96)
                    $"[Ctrl-{letter}]"
                else
                    string c
        ]

    /// Creates a view of the given state and the action to be
    /// taken in that state.
    let createView state (memories : _[]) aa =

        use wtr = new StringWriter()

            // messages that led to current state
        wtr.WriteLine()
        for msg in state.Observation.Messages do
            wtr.WriteLine($"{msg}")

            // dungeon map
        wtr.WriteLine()
        for row in state.Observation.Rows do
            wtr.WriteLine($"{row}")

            // hero status
        let status = state.Observation.Status
        wtr.WriteLine()
        wtr.WriteLine($"{status.Title} \
            St:{status.Strength} \
            Dx:{status.Dexterity} \
            Co:{status.Constitution} \
            In:{status.Intelligence} \
            Wi:{status.Wisdom} \
            Ch:{status.Charisma} \
            {status.Alignment}")
        wtr.WriteLine($"Dlvl:{status.DungeonLevel} \
            $:{status.Gold} \
            HP:{status.HP}/{status.HPMax} \
            Pw:{status.Power}/{status.PowerMax} \
            AC:{status.ArmorClass} \
            T:{status.Turns}")

            // what the game is waiting for
        wtr.WriteLine()
        match state.Pending with
            | Menu (title, mode, items) ->
                wtr.WriteLine($"Pending: Menu [{title}] {mode}")
                for item in items do
                    wtr.WriteLine($"   {item.Key} - {item.Text}")
            | pending ->
                wtr.WriteLine($"Pending: {pending}")

            // memory
        wtr.WriteLine()
        wtr.WriteLine("Existing memories:")
        for i = 0 to memories.Length - 1 do
            wtr.WriteLine($"ID {i+1}: %s{memories[i]}")
        wtr.WriteLine($"Memory to add: {aa.MemoryToAdd}")
        wtr.WriteLine($"Memories to delete: %A{aa.MemoriesToDelete}")

            // action to take in the given state
        wtr.WriteLine()
        wtr.WriteLine($"Action: {aa.Kind} {expandCtrl aa.Value}")

            // expected result of action
        wtr.WriteLine()
        wtr.WriteLine($"Prediction: {aa.Prediction}")

            // divider
        wtr.WriteLine()
        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    /// Renders a view of the given state.
    let render state memories aa =
        let view = createView state memories aa
        Console.Write(view)

        do
            use wtr = new StreamWriter("Agent.log", append = true)
            fprintf wtr "%s" view

        Console.WriteLine("Press enter to continue")
        Console.ReadLine() |> ignore

    /// Converts the model's action into a strongly-typed NetHack.Api
    /// action.
    let toAction aa =
        let value = (if isNull aa.Value then "" else aa.Value).Trim()
        // Parse a compass/vertical direction; None when unrecognized.
        let direction () =
            match value.ToLowerInvariant() with
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
        match aa.Kind with
            | ActionKind.Move ->
                match direction () with Some d -> Move d | None -> Key 's'
            | ActionKind.Run ->
                match direction () with Some d -> Run d | None -> Key 's'
            | ActionKind.Key ->
                if value.Length = 0 then Proceed
                elif aa.Count >= 2 then RepeatKey(aa.Count, value[0])
                else Key value[0]
            | ActionKind.Answer ->
                Answer (if value.Length > 0 then value[0] else 'y')
            | ActionKind.Text ->
                Text value
            | ActionKind.Extended ->
                Extended value
            | ActionKind.Cancel ->
                Cancel
            | ActionKind.Number ->
                match Int32.TryParse value with
                    | true, n -> Number n
                    | _ -> Number 0
            | ActionKind.Select ->
                value
                    |> Seq.where (Char.IsWhiteSpace >> not)
                    |> Seq.toList
                    |> Choose
            | _ ->
                Proceed

    /// Updates the given memory database.
    let updateMemory aa (memories : _[]) =
        let idxs =
            aa.MemoriesToDelete
                |> Seq.sortDescending
                |> Seq.map (fun id -> id - 1)
        let memories =
            (memories, idxs)
                ||> Seq.fold (fun memories idx ->
                    if idx >= 0 && idx < memories.Length then
                        Array.removeAt idx memories
                    else memories)
        if String.IsNullOrWhiteSpace(aa.MemoryToAdd) then
            memories
        else
            [| yield! memories; aa.MemoryToAdd |]

    /// Runs the game from the given state.
    let rec run state prediction memories =

        async {
            try
                    // get agent's action
                let! aa =
                    let prompt = getPrompt state prediction memories
                    Agent.getResultAsync<AgentAction> prompt agent

                    // display the state prior to applying the action
                render state memories aa

                    // update game state and memories
                let state = engine.Step state (toAction aa)
                let memories = updateMemory aa memories

                    // play another turn?
                if state.Over then return ()
                else return! run state aa.Prediction memories

            with exn ->
                match model.TryParseWaitTime exn with
                    | Some duration ->
                        printfn $"Waiting {duration}"
                        do! Async.Sleep(duration)
                        return! run state prediction memories
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
        run state "" Array.empty
            |> Async.RunSynchronously
