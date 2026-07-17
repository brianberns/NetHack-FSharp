namespace NetHack.Web

open System
open Feliz

open NetHack.Agent
open NetHack.Api

module View =

    /// NetHack's 16 colour names, tuned for a dark background. ("black" is
    /// drawn as a dark slate rather than true black, exactly as the tty port
    /// does, or it would be invisible.)
    let private palette =
        Map [
            "black",          "#7d8cbd"
            "red",            "#ef6b6b"
            "green",          "#4ecc60"
            "brown",          "#c78c3d"
            "blue",           "#5f92e4"
            "magenta",        "#c96fd8"
            "cyan",           "#36bcbc"
            "gray",           "#b4bdc7"
            "no-color",       "#d3dae1"
            "orange",         "#ef9a4b"
            "bright-green",   "#6ce478"
            "yellow",         "#f0dc66"
            "bright-blue",    "#8ec4ff"
            "bright-magenta", "#ec8ff4"
            "bright-cyan",    "#75e9e9"
            "white",          "#ffffff"
        ]

    let private colorOf name =
        palette
            |> Map.tryFind name
            |> Option.defaultValue "#d3dae1"

    /// NetHack reports race and gender in lower case, unlike role.
    let private capitalize (str : string) =
        if str.Length = 0 then str
        else string (Char.ToUpper str[0]) + str[1..]

    let private hungerText = function
        | Satiated  -> "Satiated"
        | NotHungry -> "Not hungry"
        | Hungry    -> "Hungry"
        | Weak      -> "Weak"
        | Fainting  -> "Fainting"
        | Fainted   -> "Fainted"
        | Starved   -> "Starved"

    let private conditionText = function
        | Stone           -> "Petrifying"
        | Slime           -> "Sliming"
        | Strangled       -> "Strangled"
        | FoodPoisoning   -> "Food poisoning"
        | TerminalIllness -> "Terminally ill"
        | Blind           -> "Blind"
        | Deaf            -> "Deaf"
        | Stunned         -> "Stunned"
        | Confused        -> "Confused"
        | Hallucinating   -> "Hallucinating"
        | Levitating      -> "Levitating"
        | Flying          -> "Flying"
        | Riding          -> "Riding"
        | Held            -> "Held"
        | Holding         -> "Holding"
        | Trapped         -> "Trapped"
        | Paralyzed       -> "Paralyzed"
        | Sleeping        -> "Sleeping"
        | Unconscious     -> "Unconscious"
        | BareHanded      -> "Bare-handed"

    /// Conditions worth shouting about, as opposed to merely reporting.
    let private isAlarming = function
        | Stone | Slime | Strangled | FoodPoisoning | TerminalIllness
        | Paralyzed | Sleeping | Unconscious | Trapped | Held -> true
        | _ -> false

    let private kindText = function
        | HeroSelf   -> "you"
        | Monster    -> "monster"
        | Pet        -> "pet"
        | Object     -> "object"
        | Terrain    -> "terrain"
        | Feature    -> "feature"
        | Trap       -> "trap"
        | Warning    -> "sensed"
        | Unexplored -> "unexplored"

    let private panel title body =
        Html.div [
            prop.className "panel"
            prop.children [
                Html.div [
                    prop.className "panel-head"
                    prop.text (title : string)
                ]
                body
            ]
        ]

    // -------------------------------------------------------------- messages

    let private renderMessages (obs : Observation) =
        panel "Messages" (
            Html.div [
                prop.className "panel-body messages-box"
                prop.children [
                    if List.isEmpty obs.Messages then
                        Html.div [
                            prop.className "empty"
                            prop.text "None this turn."
                        ]
                    else
                        Html.div [
                            prop.className "messages"
                            prop.children [
                                for i, msg in List.indexed obs.Messages do
                                    Html.div [
                                        prop.key i
                                        prop.className "message"
                                        prop.text msg
                                    ]
                            ]
                        ]
                ]
            ])

    // ------------------------------------------------------------------- map

    /// One character of the map: an entity's glyph if something decoded is
    /// standing here, otherwise plain terrain.
    let private renderCell (legend : Map<string, string>) (x : int) (entity : Entity option) (ch : char) =
        let symbol = string ch
        let className, color, tip =
            match entity with
                | Some ent ->
                    let className =
                        String.concat " " [
                            "cell"
                            if ent.Kind = HeroSelf then "hero" else "ent"
                            if not ent.InView then "faded"
                            if ent.Pile then "pile"
                        ]
                    let name = ent.Name |> Option.defaultValue (kindText ent.Kind)
                    let tip =
                        String.concat "" [
                            name
                            $" ({kindText ent.Kind})"
                            if ent.Pile then " — topmost of a pile"
                            if not ent.InView then " — remembered, not in view"
                        ]
                    className, Some (colorOf ent.Color), Some tip
                | None ->
                    "cell", None, Map.tryFind symbol legend
        Html.span [
            prop.key x
            prop.className className
            match color with
                | Some c -> prop.style [ style.color c ]
                | None -> ()
            match tip with
                | Some t -> prop.title t
                | None -> ()
            prop.text symbol
        ]

    /// A column ruler: tens digits on one line, units on the next, so that a
    /// glance at the map yields an x coordinate.
    let private renderRulers width =
        let ruler (digit : int -> string) =
            Html.div [
                prop.className "map-row map-ruler"
                prop.children [
                    Html.span [ prop.className "map-gutter" ]
                    for x in 0 .. width - 1 do
                        Html.span [
                            prop.key x
                            prop.className "cell"
                            prop.text (digit x)
                        ]
                    Html.span [ prop.className "map-gutter right" ]
                ]
            ]
        [ ruler (fun x -> if x % 10 = 0 then string (x / 10) else " ")
          ruler (fun x -> string (x % 10)) ]

    let private renderMap (obs : Observation) =
        let entities =
            obs.Entities
                |> List.map (fun ent -> (ent.Pos.X, ent.Pos.Y), ent)
                |> Map.ofList
        // no panel head: a map of the dungeon needs no announcing
        Html.div [
            prop.className "panel"
            prop.children [
                Html.div [
                    prop.className "map-scroll"
                    prop.children [
                        Html.div [
                            prop.className "map"
                            prop.children [
                                yield! renderRulers obs.Width
                                Html.div [
                                    prop.className "map-grid"
                                    prop.children [
                                        for y, row in List.indexed obs.Rows do
                                            Html.div [
                                                prop.key y
                                                prop.className "map-row"
                                                prop.children [
                                                    Html.span [
                                                        prop.className "map-gutter"
                                                        prop.text (string y)
                                                    ]
                                                    // pad short rows out to the full width, or
                                                    // the right-hand gutter would not line up
                                                    for x in 0 .. obs.Width - 1 do
                                                        let ch = if x < row.Length then row[x] else ' '
                                                        renderCell obs.Legend x (Map.tryFind (x, y) entities) ch
                                                    Html.span [
                                                        prop.className "map-gutter right"
                                                        prop.text (string y)
                                                    ]
                                                ]
                                            ]
                                    ]
                                ]
                                yield! renderRulers obs.Width
                            ]
                        ]
                    ]
                ]
            ]
        ]

    // ---------------------------------------------------------------- prompt

    /// A short label for the kind of input the game is blocked on, and the
    /// question itself.
    let private promptParts = function
        | Command                  -> "Command", "Ready for a command."
        | Direction q              -> "Direction", q
        | MultiChoice (q, cs, dflt) ->
            let dflt =
                match dflt with
                    | Some c -> $", default {c}"
                    | None -> ""
            "Choice", $"{q} [{cs}{dflt}]"
        | Quantity q               -> "Quantity", q
        | TextLine p               -> "Text", p
        | Menu (title, mode, _)    -> "Menu", $"{title} ({mode})"
        | More                     -> "More", "--More--"
        | GameOver reason          -> "Game over", reason

    let private renderPrompt (pending : Prompt) =
        let kind, question = promptParts pending
        // no panel head: the kind chip already says what is being waited on
        Html.div [
            prop.className "panel"
            prop.children [
                Html.div [
                    prop.className "panel-body"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.span [
                                    prop.className (
                                        match pending with
                                            | GameOver _ -> "prompt-kind over"
                                            | _ -> "prompt-kind")
                                    prop.text kind
                                ]
                                Html.span [
                                    prop.className "prompt-text"
                                    prop.text question
                                ]
                            ]
                        ]
                        match pending with
                            | Menu (_, _, items) ->
                                Html.ul [
                                    prop.className "menu"
                                    prop.children [
                                        for item in items do
                                            Html.li [
                                                prop.key (string item.Key)
                                                prop.className (if item.Selected then "selected" else "")
                                                prop.children [
                                                    Html.span [
                                                        prop.className "menu-key"
                                                        prop.text (string item.Key)
                                                    ]
                                                    Html.span [ prop.text item.Text ]
                                                ]
                                            ]
                                    ]
                                ]
                            | _ -> ()
                    ]
                ]
            ]
        ]

    // ----------------------------------------------------------------- notes

    /// Ticks the cell if the flag is set, and leaves it blank otherwise. The
    /// box reports what the player decided this turn; it is not an input.
    let private noteCheck flag =
        Html.td [
            prop.className "note-check"
            prop.children [
                if flag then
                    Html.input [
                        prop.type' "checkbox"
                        prop.isChecked true
                        prop.readOnly true
                    ]
            ]
        ]

    /// The age at which a note is dropped. Mirrors the rule in
    /// AgentAction.updateNotes, which is where it is actually enforced.
    let private noteMaxAge = 10

    /// Dims a note as it nears the age at which it will be dropped. The floor
    /// is set well short of illegible: this is a hint about what is on its way
    /// out, not a reason to stop reading it.
    let private noteOpacity age =
        let t = float (min age noteMaxAge) / float noteMaxAge
        1.0 - (0.4 * t)

    let private noteRow key id (note : Note) relevant delete =
        Html.tr [
            prop.key (key : string)
            prop.style [ style.opacity (noteOpacity note.Age) ]
            prop.children [
                Html.td [ prop.className "note-id"; prop.text (id : string) ]
                Html.td [ prop.className "note-text"; prop.text note.Text ]
                Html.td [ prop.className "note-age"; prop.text (string note.Age) ]
                noteCheck relevant
                noteCheck delete
            ]
        ]

    /// The notes the player is planning with: the ones carried into this turn,
    /// numbered from 1, followed by the ones written during it, which have no
    /// number until they are carried into the next turn.
    let private renderNotes (session : SessionState) =
        let relevant = Set.ofArray session.RelevantNotes
        let toDelete = Set.ofArray session.NotesToDelete
        panel "Agent's Notes" (
            Html.div [
                prop.className "panel-body scroll notes-box"
                prop.children [
                    if Array.isEmpty session.CurrentNotes
                        && Array.isEmpty session.NotesToAdd then
                        Html.div [ prop.className "empty"; prop.text "Nothing noted." ]
                    else
                        Html.table [
                            prop.className "notes"
                            prop.children [
                                Html.thead [
                                    prop.children [
                                        Html.tr [
                                            prop.children [
                                                for name in [ "ID"; "Text"; "Age"; "Relevant"; "Delete" ] do
                                                    Html.th [ prop.key name; prop.text name ]
                                            ]
                                        ]
                                    ]
                                ]
                                Html.tbody [
                                    prop.children [
                                        for i, note in Array.indexed session.CurrentNotes do
                                            noteRow $"cur{i}" (string (i + 1)) note
                                                (relevant.Contains i) (toDelete.Contains i)
                                        for i, note in Array.indexed session.NotesToAdd do
                                            noteRow $"new{i}" "New" note false false
                                    ]
                                ]
                            ]
                        ]
                ]
            ])

    // ---------------------------------------------------------------- action

    /// What the player did this turn, and what they expected it to do. The
    /// prediction is worth reading back against the next turn's messages.
    let private renderAction (session : SessionState) =
        panel "Agent's Decision" (
            Html.div [
                prop.className "panel-body"
                prop.children [
                    Html.div [
                        prop.className "acts"
                        prop.children [
                            // flat, so that the labels share a grid column and
                            // line up with each other
                            for label, text, className in
                                [ "Action", session.Action, "act-text"
                                  // a sentence the agent wrote, so it reads as
                                  // the notes do rather than as game text
                                  "Expected", session.Prediction, "act-prose" ] do
                                Html.div [
                                    prop.key label
                                    prop.className "act-label"
                                    prop.text label
                                ]
                                Html.div [
                                    prop.key (label + "-v")
                                    prop.className className
                                    prop.text text
                                ]
                        ]
                    ]
                ]
            ])

    // ---------------------------------------------------------------- vitals

    let private meter label value maxValue fillClass =
        let pct =
            if maxValue <= 0 then 0.0
            else float value / float maxValue * 100.0 |> max 0.0 |> min 100.0
        Html.div [
            prop.className "meter"
            prop.children [
                Html.div [
                    prop.className "meter-label"
                    prop.children [
                        Html.span [ prop.text (label : string) ]
                        Html.span [ prop.text $"{value} / {maxValue}" ]
                    ]
                ]
                Html.div [
                    prop.className "meter-track"
                    prop.children [
                        Html.div [
                            prop.className $"meter-fill {fillClass}"
                            prop.style [ style.width (length.percent pct) ]
                        ]
                    ]
                ]
            ]
        ]

    let private stat name value =
        Html.div [
            prop.className "stat"
            prop.children [
                Html.div [ prop.className "stat-name"; prop.text (name : string) ]
                Html.div [ prop.className "stat-value"; prop.text (value : string) ]
            ]
        ]

    let private renderVitals (obs : Observation) =
        let ch = obs.Character
        let st = obs.Status
        let hpClass =
            let frac = if st.HPMax > 0 then float st.HP / float st.HPMax else 0.0
            if frac > 0.5 then "hp-good"
            elif frac > 0.25 then "hp-warn"
            else "hp-bad"
        let facts =
            [ "Role", capitalize ch.Role
              "Race", capitalize ch.Race
              "Gender", capitalize ch.Gender
              "Align", capitalize st.Alignment
              "AC", string st.ArmorClass
              "Exp", $"{st.ExpLevel} ({st.Experience |> Option.defaultValue 0L})"
              "Gold", $"${st.Gold}"
              "Level", string st.DungeonLevel
              "Depth", string st.Depth
              "Turn", string st.Turns
              "Position", $"({obs.Hero.X}, {obs.Hero.Y})"
              "Hunger", hungerText st.Hunger
              match st.Encumbrance with
                  | Some enc -> "Encumbrance", enc
                  | None -> ()
              match st.Score with
                  | Some score -> "Score", string score
                  | None -> () ]
        panel "Vitals" (
            Html.div [
                prop.className "panel-body"
                prop.children [
                    Html.div [
                        prop.className "who"
                        prop.children [
                            Html.div [ prop.className "who-title"; prop.text st.Title ]
                            // The name arrives blank at the start of the game,
                            // and is too long for the facts grid regardless, so
                            // it gets a full-width line of its own when there is
                            // one to show.
                            if not (String.IsNullOrWhiteSpace st.Dungeon) then
                                Html.div [
                                    prop.className "who-sub"
                                    prop.text st.Dungeon
                                ]
                        ]
                    ]
                    meter "HP" st.HP st.HPMax hpClass
                    meter "POWER" st.Power st.PowerMax "pw"
                    Html.div [
                        prop.className "stats"
                        prop.children [
                            stat "STR" st.Strength
                            stat "DEX" (string st.Dexterity)
                            stat "CON" (string st.Constitution)
                            stat "INT" (string st.Intelligence)
                            stat "WIS" (string st.Wisdom)
                            stat "CHA" (string st.Charisma)
                        ]
                    ]
                    Html.dl [
                        prop.className "facts"
                        prop.children [
                            for name, value in facts do
                                Html.dt [ prop.key name; prop.text name ]
                                Html.dd [ prop.key (name + "-v"); prop.text value ]
                        ]
                    ]
                ]
            ])

    let private renderConditions (conditions : Condition list) =
        panel "Conditions" (
            Html.div [
                prop.className "panel-body"
                prop.children [
                    if List.isEmpty conditions then
                        Html.div [ prop.className "empty"; prop.text "None." ]
                    else
                        Html.div [
                            prop.className "chips"
                            prop.children [
                                for cond in conditions do
                                    Html.span [
                                        prop.key (conditionText cond)
                                        prop.className (if isAlarming cond then "chip alert" else "chip")
                                        prop.text (conditionText cond)
                                    ]
                            ]
                        ]
                ]
            ])

    // ------------------------------------------------------------- inventory

    let private renderInventory (items : InventoryItem list) =
        panel "Inventory" (
            Html.div [
                prop.className "panel-body scroll"
                prop.children [
                    if List.isEmpty items then
                        Html.div [ prop.className "empty"; prop.text "Carrying nothing." ]
                    else
                        Html.div [
                            prop.className "rows"
                            prop.children [
                                for item in items do
                                    Html.div [
                                        prop.key (string item.Letter)
                                        prop.className "row-item"
                                        prop.children [
                                            Html.span [
                                                prop.className "row-key"
                                                prop.text (string item.Letter)
                                            ]
                                            Html.span [ prop.text item.Text ]
                                        ]
                                    ]
                            ]
                        ]
                ]
            ])

    // ------------------------------------------------------------------ page

    let private renderGameState (inner : InnerState) (dispatch : Message -> unit) =
        let gameState = inner.SessionState
        let obs = gameState.Observation
        Html.div [
            prop.className (if inner.Busy then "app busy" else "app")
            prop.children [
                Html.div [
                    prop.className "layout"
                    prop.children [
                        Html.div [
                            prop.className "column"
                            prop.children [
                                renderMap obs
                                Html.div [
                                    prop.className "subcolumns"
                                    prop.children [
                                        Html.div [
                                            prop.className "column"
                                            prop.children [
                                                renderNotes gameState
                                            ]
                                        ]
                                        Html.div [
                                            prop.className "column"
                                            prop.children [
                                                renderMessages obs
                                                renderPrompt gameState.Pending
                                                renderAction gameState
                                                Html.footer [
                                                    prop.className "footer"
                                                    prop.children [
                                                        Html.button [
                                                            prop.className "button rewind"
                                                            prop.disabled inner.Busy
                                                            prop.title "Rewind to start"
                                                            prop.onClick (fun _ ->
                                                                dispatch Rewind)
                                                            prop.text "⏮"
                                                        ]
                                                        Html.button [
                                                            prop.className "button"
                                                            prop.disabled inner.Busy
                                                            prop.onClick (fun _ ->
                                                                dispatch GetNextState)
                                                            prop.text (
                                                                if inner.Busy then "Thinking…"
                                                                else "Next")
                                                        ]
                                                    ]
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                        Html.div [
                            prop.className "column"
                            prop.children [
                                renderVitals obs
                                renderConditions obs.Status.Conditions
                                renderInventory obs.Inventory
                            ]
                        ]
                    ]
                ]
                Html.a [
                    prop.className "colophon"
                    prop.href "https://github.com/brianberns/NetHack-FSharp"
                    prop.target "_blank"
                    prop.rel "noopener noreferrer"
                    prop.text "Source code and documentation"
                ]
            ]
        ]

    let render (state : State) (dispatch : Message -> unit) =
        match state with
            | Ok None ->
                Html.div [
                    prop.className "notice"
                    prop.text "Entering the dungeon…"
                ]
            | Ok (Some inner) ->
                renderGameState inner dispatch
            | Error msg ->
                Html.div [
                    prop.className "notice error"
                    prop.text msg
                ]
