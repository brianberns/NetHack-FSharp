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

/// Action DTO the model returns each turn. The order of these fields
/// drives the model to think first and then act.
type AgentAction =
    {
        [<Description("Your notes from this turn. Use these to record \
        your plan and what you've learned for future use.")>]
        NotesToAdd : string[]

        [<Description("IDs of notes to delete because they are now \
        incorrect or obsolete.")>]
        NotesToDelete : int[]

        [<Description("IDs of notes that were relevant on this turn.")>]
        RelevantNotes : int[]

        [<Description("A sentence quantifying the expected result of \
            the action you are about to take, such as the hero's \
            expected new location.")>]
        Prediction : string

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
    }

module Array =

    let isNullOrEmpty (array : _[]) =
        if isNull array then true
        else array.Length = 0

type Note =
    {
        Text : string
        Age : int
    }

module Note =

    let create text =
        {
            Text = text
            Age = 0
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
            "Reply with a command. To move, use Kind=Run (move multiple \
            steps at once) or Kind=Move (move only one step). For a named \
            action, such as kick, loot, pray, apply, force, or dip, use \
            Kind=Extended with the command name. Use Kind=Key only for a \
            simple command, such as 's' (search), ',' (pick up), or 'i' \
            (inventory), optionally with Count to repeat."
        | GameOver _ ->
            "The game is over."

    /// Expands any ASCII control characters in the given text.
    let expandCtrl (text : string) =
        let text = if isNull text then "" else text
        String.concat "" [
            for c in text do
                let value = int c
                if value >= 1 && value <= 26 then
                    let letter = char (value + 96)
                    $"[Ctrl-{letter}]"
                else
                    string c
        ]

    /// Describes the given agent action.
    let getActionDesc aa =
        if aa.Count <= 1 then
            $"{aa.Kind} {expandCtrl aa.Value}"
        else
            $"{aa.Kind} {expandCtrl aa.Value} ({aa.Count})"

    /// Creates a prompt for the agent based on the current state.
    let getPrompt (state : GameState) prevActionOpt (notes : _[]) =
        String.concat "\n" [

            "# Objective"
            "You are an expert NetHack player controlling a character. \
            Your objective is to progress through the dungeon and grow \
            stronger. Typically, you should explore each level to find \
            useful items, preferring unexplored areas over places you've \
            already been, then go on to the next level only after you've \
            covered the current level. Make a plan that reflects this \
            objective while also responding to challenges and threats."

            ""
            "# Current game state"
            "```json"
            Json.toJson state
            "```"

            ""
            "# Guidance"
            getGuidance state.Pending

            match prevActionOpt with
                | Some aa ->
                    ""
                    "# Prediction vs. reality"
                    $"The action you took on the last turn: {getActionDesc aa}"
                    "Your prediction from last turn of what the current \
                    game state should be:"
                    aa.Prediction
                    "Compare this prediction against reality to determine \
                    if you need to adjust your plan."
                | None -> ()

            if notes.Length > 0 then
                ""
                "#Your notes"
                for i = 0 to notes.Length - 1 do
                    $"{i+1}. %s{notes[i].Text}"

            ""
            "# Dungeon navigation tips"
            "* Take the opportunity to move diagonally when possible."
            "* Prefer Run over Move when exploring. Use Move for precise \
            navigation."
            "* To find new rooms, follow corridors towards blank \
            (unexplored) regions. A corridor that looks like a dead end \
            might continue further."
            "* The dungeon exists within a rectangle of the given width and \
            height. There is nothing outside of this rectangle."
            "When two entities occupy the same square, only the top one is \
            shown on the map."
            "* An object on the ground obscures the floor/corridor symbol \
            underneath it, but doesn’t block the way."
        ]

    let model = OpenRouter.model

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config model

    let engine = Native.create ()

    let truncateRuler len (chunks : seq<string>) : string =
        chunks
            |> Seq.collect id
            |> Seq.truncate len
            |> Seq.toArray
            |> String

    /// Creates a view of the given state and the action to be
    /// taken in that state.
    let createView state notes aa =

        use wtr = new StringWriter()

            // messages that led to current state
        wtr.WriteLine()
        for msg in state.Observation.Messages do
            wtr.WriteLine($"{msg}")

            // dungeon map
        let rulerTens =
            Seq.initInfinite (fun i -> $"{i}         ")
                |> truncateRuler state.Observation.Width
        let rulerUnits =
            Seq.initInfinite (fun _ -> "0123456789")
                |> truncateRuler state.Observation.Width
        wtr.WriteLine()
        wtr.WriteLine($"  {rulerTens}")
        wtr.WriteLine($"  {rulerUnits}")
        for (i, row) in Seq.indexed state.Observation.Rows do
            let toChar n = char n + '0'
            let cTens = if i % 10 = 0 then toChar (i / 10) else ' '
            let cUnits = toChar (i % 10)
            wtr.WriteLine($"{cTens}{cUnits}{row}{cUnits}{cTens}")
        wtr.WriteLine($"  {rulerUnits}")
        wtr.WriteLine($"  {rulerTens}")

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

            // notes
        if not (Array.isNullOrEmpty notes) then
            wtr.WriteLine()
            wtr.WriteLine("Existing notes:")
            for i = 0 to notes.Length - 1 do
                let note = notes[i]
                wtr.WriteLine($"   {i+1}({note.Age}): {note.Text}")
        if not (Array.isNullOrEmpty aa.NotesToAdd) then
            wtr.WriteLine()
            wtr.WriteLine("Notes to add:")
            for note in aa.NotesToAdd do
                wtr.WriteLine($"   {note}")
        if not (Array.isNullOrEmpty aa.NotesToDelete) then
            wtr.WriteLine()
            wtr.WriteLine($"Notes to delete: %A{aa.NotesToDelete}")
        if not (Array.isNullOrEmpty aa.RelevantNotes) then
            wtr.WriteLine()
            wtr.WriteLine($"Relevant notes: %A{aa.RelevantNotes}")

            // action to take in the given state
        wtr.WriteLine()
        wtr.WriteLine($"Action: {getActionDesc aa}")

            // expected result of action
        wtr.WriteLine()
        wtr.WriteLine($"Prediction: {aa.Prediction}")

            // divider
        wtr.WriteLine()
        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    /// Renders a view of the given state.
    let render state notes aa =
        let view = createView state notes aa
        Console.Write(view)

        do
            use wtr =
                new StreamWriter(
                    $"Agent{state.GameId}.log",
                    append = true)
            fprintf wtr "%s" view

        Console.WriteLine("Press enter to continue")
        Console.ReadLine() |> ignore

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
    let toAction aa =

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
    let updateNotes aa (notes : _[]) =

        let toIdxSet arr =
            if Array.isNullOrEmpty arr then Set.empty
            else arr |> Seq.map (fun id -> id - 1) |> set

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
        if Array.isNullOrEmpty aa.NotesToAdd then
            kept
        else [|
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
                        getPrompt state prevActionOpt notes
                    Agent.getResultAsync<AgentAction> prompt agent

                    // display the state prior to applying the action
                render state notes aa

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
