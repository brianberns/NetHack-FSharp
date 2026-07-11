namespace NetHack.Agent

open System
open System.ComponentModel
open System.IO
open System.Reflection
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

/// Action DTO the model returns each turn.
type AgentAction =
    {
        [<Description("The kind of action to take.")>]
        Kind : ActionKind

        [<Description("Argument for the action. \
            Move: one of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: a single character. \
            Text: the line to type. \
            Number: an integer. \
            Select: the menu letters (e.g. 'a' or 'ac'). \
            Extended: an extended command name (e.g. loot, pray, etc.). \
            Proceed: ignored.")>]
        Value : string

        [<Description("A sentence quantifying the expected result of \
            this action, such as the hero's expected new location.")>]
        Prediction : string

        [<Description("Persistent memory that carries to the next \
            turn. Use this to keep track of what you've learned \
            and what you're planning. Be thorough, detailed, and \
            consistent.")>]
        Note : string
    }

module Program =

    /// Provides guidance for responding to a prompt.
    let getGuidance = function
        | Direction _ ->
            "Specify a direction via Kind=Move."
        | MultiChoice(_, choices, _) ->
            $"Reply Kind=Answer, Value one character from \"{choices}\"."
        | Quantity _ ->
            "Specify a quantity via Kind=Number."
        | TextLine _ ->
            "Reply Kind=Text."
        | Menu(_, PickNone, _) ->
            "Reply Kind=Proceed to dismiss the menu."
        | Menu _ ->
            "Reply Kind=Select with the item letters, or Kind=Proceed to cancel."
        | More ->
            "Reply Kind=Proceed to continue."
        | Command ->
            "Reply with a command: Kind=Move, Kind=Key for a command key, \
            or Kind=Extended."
        | GameOver _ ->
            "The game is over."

    /// Creates a prompt for the agent based on the current state.
    let getPrompt (state : GameState) prediction note =
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
            if not (String.IsNullOrWhiteSpace(note)) then
                $"Your note from last turn:"; note
        ]

    let model = Gemini.flash2_5

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let engine = Native.create ()

    /// Creates a view of the given state and the action to be
    /// taken in that state.
    let createView state aa =

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

            // action to take in the given state
        wtr.WriteLine()
        wtr.WriteLine($"{aa.Kind} {aa.Value}")
        wtr.WriteLine()
        wtr.WriteLine(aa.Note)

            // expected result of action
        wtr.WriteLine()
        wtr.WriteLine(aa.Prediction)

            // divider
        wtr.WriteLine()
        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    /// Renders a view of the given state.
    let render state aa =
        let view = createView state aa
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
        match aa.Kind with
            | ActionKind.Move ->
                match value.ToLowerInvariant() with
                    | "n" | "north" -> Move North
                    | "s" | "south" -> Move South
                    | "e" | "east"  -> Move East
                    | "w" | "west"  -> Move West
                    | "ne" | "northeast" -> Move Northeast
                    | "nw" | "northwest" -> Move Northwest
                    | "se" | "southeast" -> Move Southeast
                    | "sw" | "southwest" -> Move Southwest
                    | "up"   | "<" -> Move Up
                    | "down" | ">" -> Move Down
                    | _ -> Key 's'
            | ActionKind.Key ->
                if value.Length > 0 then Key value[0]
                else Proceed
            | ActionKind.Answer ->
                Answer (if value.Length > 0 then value[0] else 'y')
            | ActionKind.Text ->
                Text value
            | ActionKind.Extended ->
                Extended value
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

    /// Runs the game from the given state.
    let rec run state prediction note =

        async {
            try
                    // get agent's action
                let! aa =
                    let prompt = getPrompt state prediction note
                    Agent.getResultAsync<AgentAction> prompt agent

                    // display the state prior to applying the action
                render state aa

                    // update game state
                let state = engine.Step state (toAction aa)

                    // play another turn?
                if state.Over then return ()
                else return! run state aa.Prediction aa.Note

            with exn ->
                match model.TryParseWaitTime exn with
                    | Some duration ->
                        printfn $"Waiting {duration}"
                        do! Async.Sleep(duration)
                        return! run state prediction note
                    | None ->
                        printfn $"{exn.Message}"
        }

        // start a new game
    let state =
        { NewGame.defaults with
            Name = Some model.Name }
            |> engine.Start

        // run the game and wait for it to finish
    run state "" ""
        |> Async.RunSynchronously
