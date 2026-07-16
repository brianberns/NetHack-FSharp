namespace NetHack.Web

open System
open Feliz

open NetHack.Api

module View =

    /// NetHack's 16 colour names, tuned for a dark background. ("black" is
    /// drawn as a dark slate rather than true black, exactly as the tty port
    /// does, or it would be invisible.)
    let private palette =
        Map [
            "black",          "#6272a4"
            "red",            "#e05252"
            "green",          "#3fb950"
            "brown",          "#a9762f"
            "blue",           "#4a7fd4"
            "magenta",        "#b45cc4"
            "cyan",           "#29a8a8"
            "gray",           "#a8b1bb"
            "no-color",       "#c9d1d9"
            "orange",         "#e08a3c"
            "bright-green",   "#56d364"
            "yellow",         "#e6d155"
            "bright-blue",    "#79b8ff"
            "bright-magenta", "#e07ce8"
            "bright-cyan",    "#5fdede"
            "white",          "#f0f6fc"
        ]

    let private colorOf name =
        palette
            |> Map.tryFind name
            |> Option.defaultValue "#c9d1d9"

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

    // ---------------------------------------------------------------- banner

    let private renderBanner (obs : Observation) =
        let ch = obs.Character
        let st = obs.Status
        Html.header [
            prop.className "banner"
            prop.children [
                Html.span [
                    prop.className "banner-mark"
                    prop.text "NETHACK"
                ]
                Html.span [
                    prop.className "banner-title"
                    prop.text st.Title
                ]
                Html.span [
                    prop.className "banner-sub"
                    prop.text $"{ch.Gender} {ch.Race} {ch.Role} · {st.Alignment}"
                ]
                Html.span [ prop.className "banner-spacer" ]
                Html.span [
                    prop.className "banner-sub"
                    prop.text $"{st.Dungeon} · level {st.DungeonLevel} · turn {st.Turns}"
                ]
            ]
        ]

    // -------------------------------------------------------------- messages

    let private renderMessages (obs : Observation) =
        panel "Messages" (
            Html.div [
                prop.className "panel-body"
                prop.children [
                    if List.isEmpty obs.Messages then
                        Html.div [
                            prop.className "empty"
                            prop.text "Nothing to report."
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
    let private renderCell (legend : Map<string, string>) (entity : Entity option) (ch : char) =
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
                ]
            ]
        [ ruler (fun x -> if x % 10 = 0 then string (x / 10) else " ")
          ruler (fun x -> string (x % 10)) ]

    let private renderMap (obs : Observation) =
        let entities =
            obs.Entities
                |> List.map (fun ent -> (ent.Pos.X, ent.Pos.Y), ent)
                |> Map.ofList
        panel "Dungeon" (
            Html.div [
                prop.className "map-scroll"
                prop.children [
                    Html.div [
                        prop.className "map"
                        prop.children [
                            yield! renderRulers obs.Width
                            for y, row in List.indexed obs.Rows do
                                Html.div [
                                    prop.key y
                                    prop.className "map-row"
                                    prop.children [
                                        Html.span [
                                            prop.className "map-gutter"
                                            prop.text (string y)
                                        ]
                                        for x in 0 .. row.Length - 1 do
                                            renderCell obs.Legend (Map.tryFind (x, y) entities) row[x]
                                    ]
                                ]
                            yield! renderRulers obs.Width
                        ]
                    ]
                ]
            ])

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
        panel "Waiting for" (
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
        let st = obs.Status
        let hpClass =
            let frac = if st.HPMax > 0 then float st.HP / float st.HPMax else 0.0
            if frac > 0.5 then "hp-good"
            elif frac > 0.25 then "hp-warn"
            else "hp-bad"
        let facts =
            [ "AC", string st.ArmorClass
              "Exp", $"{st.ExpLevel} ({st.Experience |> Option.defaultValue 0L})"
              "Gold", $"${st.Gold}"
              "Depth", string st.Depth
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

    // -------------------------------------------------------------- entities

    /// Everything decoded on the map bar the hero, in view first, since the
    /// out-of-view ones are only recalled and may have moved on.
    let private renderEntities (entities : Entity list) =
        let interesting =
            entities
                |> List.filter (fun ent ->
                    ent.Kind <> HeroSelf && ent.Kind <> Unexplored)
                |> List.sortBy (fun ent ->
                    (not ent.InView), ent.Name |> Option.defaultValue "~")
        panel "On the map" (
            Html.div [
                prop.className "panel-body scroll"
                prop.children [
                    if List.isEmpty interesting then
                        Html.div [ prop.className "empty"; prop.text "Nothing of note." ]
                    else
                        Html.div [
                            prop.className "rows"
                            prop.children [
                                for ent in interesting do
                                    Html.div [
                                        prop.key $"{ent.Pos.X},{ent.Pos.Y}"
                                        prop.className (if ent.InView then "row-item" else "row-item faded")
                                        prop.children [
                                            Html.span [
                                                prop.className "row-glyph"
                                                prop.style [ style.color (colorOf ent.Color) ]
                                                prop.text (string ent.Symbol)
                                            ]
                                            Html.span [
                                                prop.text (
                                                    match ent.Name with
                                                        | Some name when ent.Pile -> $"{name} (pile)"
                                                        | Some name -> name
                                                        | None -> kindText ent.Kind)
                                            ]
                                            Html.span [
                                                prop.className "row-note"
                                                prop.text $"{ent.Pos.X},{ent.Pos.Y}"
                                            ]
                                        ]
                                    ]
                            ]
                        ]
                ]
            ])

    // ---------------------------------------------------------------- legend

    let private renderLegend (legend : Map<string, string>) =
        panel "Terrain" (
            Html.div [
                prop.className "panel-body scroll"
                prop.children [
                    if Map.isEmpty legend then
                        Html.div [ prop.className "empty"; prop.text "Nothing mapped yet." ]
                    else
                        Html.div [
                            prop.className "legend"
                            prop.children [
                                for symbol, name in Map.toList legend do
                                    Html.div [
                                        prop.key symbol
                                        prop.className "legend-item"
                                        prop.children [
                                            Html.span [ prop.className "legend-sym"; prop.text symbol ]
                                            Html.span [ prop.className "legend-name"; prop.text name ]
                                        ]
                                    ]
                            ]
                        ]
                ]
            ])

    // ------------------------------------------------------------------ page

    let private renderGameState (gameState : GameStateWeb) =
        let obs = gameState.Observation
        Html.div [
            prop.className "app"
            prop.children [
                renderBanner obs
                Html.div [
                    prop.className "layout"
                    prop.children [
                        Html.div [
                            prop.className "column"
                            prop.children [
                                renderMessages obs
                                renderMap obs
                                renderPrompt gameState.Pending
                            ]
                        ]
                        Html.div [
                            prop.className "column"
                            prop.children [
                                renderVitals obs
                                renderConditions obs.Status.Conditions
                                renderInventory obs.Inventory
                                renderEntities obs.Entities
                                renderLegend obs.Legend
                            ]
                        ]
                    ]
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
            | Ok (Some gameState) ->
                renderGameState gameState
            | Error msg ->
                Html.div [
                    prop.className "notice error"
                    prop.text msg
                ]
