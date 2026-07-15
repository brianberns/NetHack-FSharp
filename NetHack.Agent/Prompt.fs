namespace NetHack.Agent

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

[<AutoOpen>]
module ApiExt =

    type Pos with

        /// Display string.
        member pos.String =
            $"({pos.X},{pos.Y})"

module Prompt =

    /// "Objective" portion of a prompt.
    let private objective =
        [
            "# Objective"

            "You are an expert NetHack player controlling a character. \
            Your objective is to progress through the dungeon and grow \
            stronger. Typically, you should explore each level to find \
            useful items, preferring unexplored areas over places you've \
            already been, then go on to the next level only after you've \
            covered the current level. Make a plan that reflects this \
            objective while also responding to challenges and threats."
        ]

    /// Creates the "Dungeon map" portion of a prompt.
    let private getDungeonMap (observation : Observation) =
        [
            ""
            "# Dungeon map"

            $"The dungeon exists within a {observation.Width}x{observation.Height} \
            rectangle:"
            "```"
            for row in observation.Rows do
                row
            "```"

            "## Legend:"
            "| Symbol | Name |"
            "|--|--|"
            for (symbol, name) in Map.toSeq observation.Legend do
                $"| {symbol} | {name} |"
        ]

    /// Creates the "Hero status" portion of a prompt.
    let private getHeroStatus (observation : Observation) =
        [
            ""
            "# Hero status"

            $"* Location: {observation.Hero.String}"
            $"* Role: {observation.Character.Role}"
            $"* Race: {observation.Character.Race}"
            $"* Gender: {observation.Character.Gender}"

            let status = observation.Status
            $"* Title: {status.Title}"
            $"* Alignment: {status.Alignment}"
            $"* Strength: {status.Strength}"
            $"* Dexterity: {status.Dexterity}"
            $"* Constitution {status.Constitution}"
            $"* Intelligence: {status.Intelligence}"
            $"* Wisdom: {status.Wisdom}"
            $"* Charisma: {status.Charisma}"
            $"* Hit points: {status.HP}/{status.HPMax}"
            $"* Power: {status.Power}/{status.PowerMax}"
            $"* Armor class: {status.ArmorClass}"
            $"* Experience level: {status.ExpLevel}"
            match status.Experience with
                | Some exp -> $"* Experience: {exp}"
                | None -> ()
            $"* Gold: ${status.Gold}"
            if status.Dungeon = "" then ()
            else $"* Dungeon: {status.Dungeon}"
            $"* Dungeon level: {status.DungeonLevel}"
            $"* Depth: {status.Depth}"
            $"* Hunger: {status.Hunger}"
            for cond in status.Conditions do
                $"* Condition: {cond}"
            $"* Turns: {status.Turns}"
            match status.Score with
                | Some score -> $"* Score: {score}"
                | None -> ()
        ]

    /// Creates the "Entities" portion of a prompt.
    let private getEntities entities =
        [
            ""
            "# Entities"
            "| Position | Symbol | Kind | Name | Pile | Viewability |"
            "|--|--|--|--|--|--|"
            for (entity : Entity) in entities do
                let name = Option.defaultValue "" entity.Name
                let pile = if entity.Pile then "Pile" else ""
                let viewable =
                    if entity.InView then "In view"
                    else "Out of view"
                $"| {entity.Pos.String} | {entity.Symbol} | {entity.Kind} | {name} | {pile} | {viewable} |"
            ""
            "When multiple entities occupy the same square (a 'pile'), \
            only the top one is shown on the map and listed here."
        ]

    /// Creates the "Inventory" portion of a prompt.
    let private getInventory items =
        [
            ""
            "# Inventory"
            "| Letter | Description |"
            "|--|--|"
            for (item : InventoryItem) in items do
                $"| {item.Letter} | {item.Text} |"
        ]

    /// Creates the "Messages" portion of a prompt.
    let private getMessages (messages : List<string>) =
        [
            if not (List.isEmpty messages) then
                ""
                "# Messages"
                "```"
                yield! messages
                "```"
        ]

    /// Creates the "Game state" portion of a prompt.
    let private getState observation =
        [
            yield! getDungeonMap observation
            yield! getHeroStatus observation
            yield! getEntities observation.Entities
            yield! getInventory observation.Inventory
            yield! getMessages observation.Messages
        ]

    /// Creates the "Instructions" portion of a prompt.
    let private getInstructions pending =
        [
            ""
            "# Instructions"

            "The game is currently waiting for:"
            "```json"
            Json.toJson pending
            "```"

            match pending with
                | Direction _ ->
                    "Specify a direction via Type=Move or use Type=Cancel to \
                    back out."
                | MultiChoice(_, choices, _) ->
                    let desc =
                        if choices = "" then "one of the characters offered"
                        else $"one character from '{choices}'"
                    $"Reply Type=Answer with Value set to {desc} or use \
                    Type=Cancel to back out."
                | Quantity _ ->
                    "Specify a quantity via Type=Number or use Type=Cancel to \
                    back out."
                | TextLine _ ->
                    "Reply Type=Text or use Type=Cancel to back out."
                | Menu(_, PickNone, _) ->
                    "Reply Type=Proceed to dismiss the menu."
                | Menu _ ->
                    "Reply Type=Select with the item letters or use Type=Cancel \
                    to cancel."
                | More ->
                    "Reply Type=Proceed to continue."
                | Command ->
                    "Reply with a command."
                    "To move, use Type=Run (move multiple steps at once) or \
                    Type=Move (move only one step)."
                    "For a named action, such as kick, loot, pray, apply, \
                    force, or dip, use Type=Extended with the command name."
                    "Use Type=Key only for a simple command, such as 's' \
                    (search) or ',' (pick up), optionally with Count to \
                    repeat."
                | GameOver _ ->
                    "The game is over."
        ]

    /// Expands any ASCII control characters in the given text.
    let private expandCtrl (text : string) =
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
    let getActionDesc (aa : AgentAction) =
        if aa.Count <= 1 then
            $"{aa.Type} {expandCtrl aa.Value}"
        else
            $"{aa.Type} {expandCtrl aa.Value} ({aa.Count})"

    /// Creates the "Prediction" portion of a prompt.
    let private getPrediction aaOpt =
        [
            match aaOpt with
                | Some aa ->

                    ""
                    "# Adjust your plan if necessary"

                    "The action you took on the last turn:"
                    "```"
                    getActionDesc aa
                    "```"

                    "Your prediction from last turn of what the current \
                    game state should be:"
                    "```"
                    aa.Prediction
                    "```"

                    "Compare this prediction against the actual game state \
                    to determine if you need to try something different. \
                    Pay attention to any messages you received."

                | None -> ()
        ]

    /// Creates the "Notes" portion of a prompt.
    let private getNotes (notes : _[]) =
        [
            if notes.Length > 0 then
                ""
                "# Your notes"
                for i = 0 to notes.Length - 1 do
                    $"{i+1}. %s{notes[i].Text}"
        ]

    /// The "Tips" portion of a prompt.
    let private tips =
        [
            ""
            "# Dungeon navigation tips"

            "* Prefer the Run command over the Move command when \
            exploring. Use Move for precise navigation."
            "* Move (or Run) diagonally when applicable. (However, \
            note that you can't move diagonally into or out of an \
            intact doorway.)"
        ]

    /// Creates a prompt for the agent based on the current state.
    let getPrompt (state : GameState) prevActionOpt (notes : _[]) =
        String.concat "\n" [
            yield! objective
            yield! getState state.Observation
            yield! getInstructions state.Pending
            yield! getPrediction prevActionOpt
            yield! getNotes notes
            yield! tips
        ]
