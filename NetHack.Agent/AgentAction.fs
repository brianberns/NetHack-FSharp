namespace NetHack.Agent

open System
open System.ComponentModel
open System.Text.Json.Serialization

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
    | Cancel = 9

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
            Move/Run: one of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: a single character. \
            Text: a line of text. \
            Number: an integer. \
            Select: the menu letters (e.g. 'a' or 'ac'). \
            Extended: an extended command name (e.g. loot, pray, etc.). \
            Proceed/Cancel: ignored.")>]
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

/// A note the agent uses to plan ahead.
type Note =
    {
        /// Note content.
        Text : string

        /// Note age.
        Age : int
    }

module Note =

    /// Creates a note.
    let create text =
        {
            Text = text
            Age = 0
        }

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
    let toAction (aa : AgentAction) =

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
