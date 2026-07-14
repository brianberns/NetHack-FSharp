namespace NetHack.Agent

open System.ComponentModel
open System.Text.Json.Serialization

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

module Prompt =

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
    let getActionDesc (aa : AgentAction) =
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
            Json.toJson state.Observation
            "```"

            ""
            "# Instructions"
            "The game is currently waiting for:"
            "```json"
            Json.toJson state.Pending
            "```"
            getGuidance state.Pending

            match prevActionOpt with
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

            if notes.Length > 0 then
                ""
                "# Your notes"
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
