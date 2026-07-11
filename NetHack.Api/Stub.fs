namespace NetHack.Api

/// An in-process fake NetHack: a single lit room with a fountain and a jackal.
/// It is deliberately tiny — its only job is to exercise the real API types and
/// JSON end-to-end so we can iterate on the design before binding the C engine.
/// The transition is pure, so `Step` genuinely realizes GameState -> Action ->
/// GameState (the same GameState could be stepped twice to branch).
module Stub =

    let private width = 80
    let private height = 21

    // Room interior spans columns 30..50 and rows 5..15.
    let private roomL, private roomR = 30, 50
    let private roomT, private roomB = 5, 15

    /// The engine-private world hidden behind GameState.Continuation.
    type private World =
        { Hero     : Pos
          Fountain : Pos
          Jackal   : Pos option
          Turns    : int64
          Hp       : int
          HpMax    : int }

    let private isWall p =
        p.Y = roomT || p.Y = roomB || p.X = roomL || p.X = roomR

    let private inRoom p =
        p.X >= roomL && p.X <= roomR && p.Y >= roomT && p.Y <= roomB

    let private delta =
        function
        | North -> 0, -1     | South -> 0, 1
        | East  -> 1, 0      | West  -> -1, 0
        | Northeast -> 1, -1 | Northwest -> -1, -1
        | Southeast -> 1, 1  | Southwest -> -1, 1
        | Up | Down -> 0, 0

    // ---- rendering ------------------------------------------------------

    let private rows (w: World) : string list =
        [ for y in 0 .. height - 1 ->
            System.String(
                [| for x in 0 .. width - 1 ->
                     let p = { X = x; Y = y }
                     if p = w.Hero then '@'
                     elif Some p = w.Jackal then 'd'
                     elif p = w.Fountain then '{'
                     elif not (inRoom p) then ' '
                     elif isWall p then (if p.Y = roomT || p.Y = roomB then '-' else '|')
                     else '.' |]) ]

    let private entities (w: World) : Entity list =
        [ { Pos = w.Hero; Symbol = '@'; Kind = HeroSelf
            Name = Some "you"; Color = "white" }
          { Pos = w.Fountain; Symbol = '{'; Kind = Feature
            Name = Some "fountain"; Color = "blue" }
          match w.Jackal with
          | Some p ->
              { Pos = p; Symbol = 'd'; Kind = Monster
                Name = Some "jackal"; Color = "yellow" }
          | None -> () ]

    let private status (w: World) : Status =
        { Title = "Newt the Rambler"; Alignment = "neutral"
          Strength = "16"; Dexterity = 12; Constitution = 14
          Intelligence = 10; Wisdom = 11; Charisma = 9
          HP = w.Hp; HPMax = w.HpMax; Power = 3; PowerMax = 3
          ArmorClass = 8; ExpLevel = 1; Experience = Some 0L; Gold = 0L
          Dungeon = "The Dungeons of Doom"; DungeonLevel = 1; Depth = 1
          Hunger = NotHungry; Encumbrance = None; Conditions = []
          Turns = w.Turns; Score = Some 0L }

    let private character : Character =
        { Role = "Valkyrie"; Race = "human"; Gender = "female" }

    let private legend : Map<string, string> =
        Map [ ".", "floor of a room"; "|", "wall"; "-", "wall"; "{", "fountain" ]

    let private observe (w: World) (messages: string list) : Observation =
        { Width = width; Height = height
          Rows = rows w; Legend = legend; Hero = w.Hero
          Character = character; Entities = entities w
          Status = status w; Messages = messages }

    let private state (w: World) (messages: string list) (pending: Prompt) : GameState =
        { Continuation = box w
          Session = "stub"
          Observation = observe w messages
          Pending = pending
          Over = (match pending with GameOver _ -> true | _ -> false) }

    // ---- transitions ----------------------------------------------------

    let private move (w: World) dir : GameState =
        let dx, dy = delta dir
        let target = { X = w.Hero.X + dx; Y = w.Hero.Y + dy }
        if not (inRoom target) || isWall target then
            state w [ "It's solid stone." ] Command
        elif Some target = w.Jackal then
            state { w with Jackal = None; Turns = w.Turns + 1L }
                  [ "You hit the jackal.  The jackal is killed!" ] Command
        else
            state { w with Hero = target; Turns = w.Turns + 1L } [] Command

    let private step (state0: GameState) (action: Action) : GameState =
        let w = state0.Continuation :?> World
        match state0.Pending, action with
        // Answering the "drink from the fountain?" question.
        | MultiChoice _, Answer c when System.Char.ToLower c = 'y' ->
            state { w with Turns = w.Turns + 1L }
                  [ "The water tastes not so good." ] Command
        | MultiChoice _, Answer _ ->
            state w [ "Never mind." ] Command
        // Normal commands.
        | Command, Move dir -> move w dir
        | Command, Key 'q' when w.Hero = w.Fountain ->
            state w [] (MultiChoice("Drink from the fountain?", "yn", Some 'n'))
        | Command, Key 'q' ->
            state w [ "You don't have anything to drink." ] Command
        | Command, Key 's' ->
            state { w with Turns = w.Turns + 1L } [ "You find nothing." ] Command
        | Command, Key 'i' ->
            state w [] (Menu("Inventory", PickNone,
                             [ { Key = 'a'; Text = "a - an uncursed +1 short sword"
                                 Glyph = None; Count = None; Selected = false } ]))
        | Menu _, _ -> state w [] Command   // dismiss any menu
        | More, _ -> state w [] Command
        | _, _ -> state w [ "Unknown command." ] Command

    let private start (opts: NewGame) : GameState =
        let w =
            { Hero = { X = 35; Y = 8 }
              Fountain = { X = 40; Y = 10 }
              Jackal = Some { X = 45; Y = 12 }
              Turns = 0L; Hp = 12; HpMax = 12 }
        let who = defaultArg opts.Name "Newt"
        state w [ $"Hello {who}, welcome to NetHack!" ] Command

    /// Create the in-process stub engine.
    let create () : IEngine =
        { new IEngine with
            member _.Start opts = start opts
            member _.Step s a = step s a }
