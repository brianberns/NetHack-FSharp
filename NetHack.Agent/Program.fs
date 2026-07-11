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

/// Action DTO the model returns each turn.
type AgentAction =
    {
        [<Description("One short sentence explaining this action.")>]
        Reasoning : string

        [<Description("The kind of action to take.")>]
        Kind : ActionKind

        [<Description("Argument for the action. \
            Move: one of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: a single character. \
            Text: the line to type. \
            Number: an integer. \
            Select: the menu letters (e.g. 'a' or 'ac'). \
            Proceed: ignored.")>]
        Value : string

        [<Description("Your running memory to carry to the next turn. \
            Include information about what you've done in the past, \
            what you've learned, your short-term goal, and your \
            overall plan.")>]
        Note : string
    }

module Program =

    /// Creates a prompt for the agent based on the current state.
    let getPrompt (state : GameState) note =
        String.concat "\n" [
            $"You are an expert NetHack player controlling a character. \
            Current game state (JSON):"; Json.toJson state
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
        for msg in state.Observation.Messages do
            wtr.WriteLine($"{msg}")

            // dungeon map
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

            // action to take in the given state
        wtr.WriteLine()
        wtr.WriteLine($"{aa.Kind} {aa.Value}")
        wtr.WriteLine()
        wtr.WriteLine(aa.Reasoning)
        wtr.WriteLine()
        wtr.WriteLine(aa.Note)

            // divider
        wtr.WriteLine()
        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    /// Renders a view of the given state.
    let render state aa =

        let view = createView state aa

        if not Console.IsOutputRedirected then
            Console.Write("\x1b[3J\x1b[H\x1b[2J")   // clear console
        Console.Write(view)

        do
            use wtr = new StreamWriter("Agent.log", append = true)
            fprintfn wtr "%s" view

        if not Console.IsOutputRedirected then
            Console.Write("Press enter to continue")
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
    let rec run state note =

        async {
            try
                    // get agent's action
                let! aa =
                    let prompt = getPrompt state note
                    Agent.getResultAsync<AgentAction> prompt agent

                    // display the state prior to applying the action
                render state aa

                    // update game state
                let state = engine.Step state (toAction aa)

                    // play another turn?
                if state.Over then return ()
                else return! run state aa.Note

            with exn ->
                match model.TryParseWaitTime exn with
                    | Some duration ->
                        printfn $"Waiting {duration}"
                        do! Async.Sleep(duration)
                        return! run state note
                    | None ->
                        printfn $"{exn.Message}"
        }

        // start a new game
    let state =
        { NewGame.defaults with
            Name = Some model.Name }
            |> engine.Start

        // run the game and wait for it to finish
    run state ""
        |> Async.RunSynchronously
