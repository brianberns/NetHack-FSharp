namespace NetHack.Agent

open System
open System.ComponentModel
open System.Text.Json.Serialization
open System.Text.RegularExpressions

open NetHack.Api

/// The types of action the model may choose.
[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type ActionType =
    | Run = 0
    | Move = 1
    | Key = 2
    | Answer = 3
    | Text = 4
    | Number = 5
    | Select = 6
    | Proceed = 7
    | Extended = 8
    | SymAbs = 9
    | SymRel = 10
    | Cancel = 11

/// Action DTO the model returns each turn. The order of these fields
/// drives the model to think first and then act.
type AgentAction =
    {
        [<Description("Your notes from this turn. Use these to record \
        your plan and what you've learned for future use.")>]
        [<JsonPropertyName("NotesToAdd")>]
        _NotesToAdd : string[]

        [<Description("IDs of notes to delete because they are now \
        incorrect or obsolete.")>]
        [<JsonPropertyName("NotesToDelete")>]
        _NotesToDelete : int[]

        [<Description("IDs of notes that were relevant on this turn.")>]
        [<JsonPropertyName("RelevantNotes")>]
        _RelevantNotes : int[]

        [<Description("A sentence quantifying the expected result of \
            the action you are about to take, such as the hero's \
            expected new location.")>]
        [<JsonPropertyName("Prediction")>]
        _Prediction : string

        [<Description("The type of action to take.")>]
        Type : ActionType

        [<Description("Argument for the action. \
            Move/Run: One of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: A single character. \
            Text: A line of text. \
            Number: An integer. \
            Select: The menu letters (e.g. 'a' or 'ac'). \
            Extended: An extended command name (e.g. loot, pray, etc.). \
            SymAbs: Absolute (x,y) coordinates. \
            SymRel: (x,y) coordinates relative to hero. \
            Proceed/Cancel: Ignored.")>]
        [<JsonPropertyName("Value")>]
        _Value : string

        [<Description("Optional repeat count for a Key command, such \
            as 's' (search) or '.' (rest).")>]
        Count : int
    }

    /// Notes added this turn.
    [<JsonIgnore>]
    member this.NotesToAdd =
        if isNull this._NotesToAdd then Array.empty
        else this._NotesToAdd

    /// Notes deleted this turn.
    [<JsonIgnore>]
    member this.NotesToDelete =
        if isNull this._NotesToDelete then Array.empty
        else this._NotesToDelete

    /// Notes deemed relevant this turn.
    [<JsonIgnore>]
    member this.RelevantNotes =
        if isNull this._RelevantNotes then Array.empty
        else this._RelevantNotes

    /// This turn's prediction.
    [<JsonIgnore>]
    member this.Prediction =
        if isNull this._Prediction then ""
        else this._Prediction

    /// This turn's value.
    [<JsonIgnore>]
    member this.Value =
        if isNull this._Value then ""
        else this._Value

module AgentAction =

    /// Parses a compass/vertical direction, if possible.
    let private tryParseDirection (text : string) =
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
    let private toAction (aa : AgentAction) =

        let value =
            (if isNull aa.Value then ""
            else aa.Value).Trim()

        match aa.Type with

            | ActionType.Move ->
                tryParseDirection value
                    |> Option.map Move
                    |> Option.defaultValue (Key 's')

            | ActionType.Run ->
                tryParseDirection value
                    |> Option.map Run
                    |> Option.defaultValue (Key 's')

            | ActionType.Key ->
                if value.Length = 0 then
                    Proceed
                elif aa.Count >= 2 then
                    RepeatKey(aa.Count, value[0])
                else
                    Key value[0]

            | ActionType.Answer ->
                Answer (
                    if value.Length > 0 then value[0]
                    else 'y')

            | ActionType.Text ->
                Text value

            | ActionType.Extended ->
                Extended value

            | ActionType.Cancel ->
                Cancel

            | ActionType.Number ->
                match Int32.TryParse(value) with
                    | true, n -> Number n
                    | _ -> Number 0

            | ActionType.Select ->
                value
                    |> Seq.where (Char.IsWhiteSpace >> not)
                    |> Seq.toList
                    |> Choose

            | _ ->
                Proceed

    /// Parses "(x,y)" coordinates.
    let private tryParseCoordinates text =
        let pattern = @"^\s*\(?\s*(-?\d+)\s*,\s*(-?\d+)\s*\)?\s*$"
        let m = Regex.Match(text, pattern)
        if m.Success then
            let x = int m.Groups.[1].Value
            let y = int m.Groups.[2].Value
            Some (x, y)
        else
            None

    /// Sets the message in the given state.
    let private setMessage message state =
        { state with
            Observation =
                { state.Observation with
                    Messages = [message] } }

    /// Gets a symbol on the map.
    let private getSymbol state value isAbs =
        match tryParseCoordinates value with
            | Some (x, y) ->
                let x, y =
                    if isAbs then x, y
                    else
                        let hero = state.Observation.Hero
                        hero.X + x, hero.Y + y
                let sym = state.Observation.Rows[y][x]
                setMessage $"Symbol: {sym}" state
            | None ->
                setMessage "Could not parse coordinates" state

    /// Applies the given action to the given state using the
    /// given NetHack engine.
    let step (engine : IEngine) state aa =
        match aa.Type with
            | ActionType.SymAbs ->
                getSymbol state aa.Value true
            | ActionType.SymRel ->
                getSymbol state aa.Value false
            | _ -> engine.Step state (toAction aa)

    /// Updates the given note database.
    let updateNotes (aa : AgentAction) (notes : Note[]) =

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
