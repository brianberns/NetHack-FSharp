module NetHack.Cli.Program

open System
open NetHack.Api

/// Render the current observation to the terminal: map, status, messages, prompt.
let private draw (s: GameState) =
    Console.Clear()
    let o = s.Observation
    for row in o.Rows do
        Console.WriteLine(row)
    let st = o.Status
    let ch = o.Character
    Console.WriteLine()
    Console.WriteLine($"{st.Title}   {ch.Gender} {ch.Race} {ch.Role}")
    Console.WriteLine(
        $"St:{st.Strength} Dx:{st.Dexterity} Co:{st.Constitution} "
        + $"In:{st.Intelligence} Wi:{st.Wisdom} Ch:{st.Charisma} {st.Alignment}")
    Console.WriteLine(
        $"Dlvl:{st.DungeonLevel}  $:{st.Gold}  HP:{st.HP}({st.HPMax})  "
        + $"Pw:{st.Power}({st.PowerMax})  AC:{st.ArmorClass}  Xp:{st.ExpLevel}  T:{st.Turns}")
    if not (List.isEmpty o.Messages) then
        Console.WriteLine()
        for m in o.Messages do Console.WriteLine(m)

/// Map a keystroke to an Action, given what the game is currently asking for.
let private toAction (pending: Prompt) (key: ConsoleKeyInfo) : Action option =
    match pending with
    | MultiChoice _ -> Some(Answer key.KeyChar)
    | More -> Some Proceed
    | Menu _ -> Some(Choose [])
    | _ ->
        match key.KeyChar with
        | 'h' -> Some(Move West)      | 'l' -> Some(Move East)
        | 'j' -> Some(Move South)     | 'k' -> Some(Move North)
        | 'y' -> Some(Move Northwest) | 'u' -> Some(Move Northeast)
        | 'b' -> Some(Move Southwest) | 'n' -> Some(Move Southeast)
        | 'q' -> Some(Key 'q')        | 's' -> Some(Key 's')
        | 'i' -> Some(Key 'i')
        | _ -> None

/// A prompt line describing what input the game expects.
let private promptLine (pending: Prompt) =
    match pending with
    | Command -> "[hjklyubn move | q quaff | s search | i inv | J dump JSON | Q quit]"
    | MultiChoice(q, choices, _) -> $"{q} [{choices}]"
    | More -> "--More--  (press a key)"
    | Menu(title, _, _) -> $"{title}  (press a key to dismiss)"
    | Direction q -> $"{q}  (direction)"
    | TextLine p -> p
    | Quantity q -> $"{q} (number)"
    | GameOver r -> $"Game over: {r}"

/// Non-interactive: print the JSON wire form of the initial state and one step.
let private dump () =
    let engine = Stub.create ()
    let s0 = engine.Start { NewGame.defaults with Name = Some "Ada" }
    printfn "// initial GameState"
    printfn "%s" (Json.toJson s0)
    let onFountain =
        [ Move East; Move East; Move East; Move Southeast; Move Southeast ]
        |> List.fold engine.Step s0
    let asked = engine.Step onFountain (Key 'q')
    printfn "// Pending after quaffing on the fountain"
    printfn "%s" (Json.toJson asked.Pending)
    0

/// Non-interactive: drive the real native engine through a scripted sequence,
/// printing the actual dungeon after each action.
let private nativeDemo () =
    let engine = Native.create ()
    let show label (s: GameState) =
        printfn "\n----- %s -----" label
        for r in s.Observation.Rows do printfn "%s" r
        let st = s.Observation.Status
        let ch = s.Observation.Character
        printfn "%s  [%s %s %s, %s]" st.Title ch.Gender ch.Race ch.Role st.Alignment
        printfn "T:%d Dlvl:%d HP:%d(%d) Pw:%d(%d) AC:%d $:%d Str:%s"
            st.Turns st.DungeonLevel st.HP st.HPMax st.Power st.PowerMax
            st.ArmorClass st.Gold st.Strength
        if not (List.isEmpty s.Observation.Messages) then
            printfn "msg: %s" (String.concat " | " s.Observation.Messages)
        let ents =
            s.Observation.Entities
            |> List.filter (fun e -> e.Kind <> HeroSelf)
            |> List.map (fun e -> sprintf "%A@(%d,%d)=%s[%s]" e.Kind e.Pos.X e.Pos.Y (defaultArg e.Name "?") e.Color)
        if not (List.isEmpty ents) then printfn "entities: %s" (String.concat " " ents)
        let legend =
            s.Observation.Legend |> Map.toList
            |> List.map (fun (sym, name) -> sprintf "%s=%s" sym name)
        if not (List.isEmpty legend) then printfn "legend: %s" (String.concat "  " legend)
        match s.Pending with
        | Menu(title, mode, items) ->
            printfn "MENU [%s] %A:" title mode
            for it in items do printfn "   %c - %s" it.Key it.Text
        | p -> printfn "pending: %A  over:%b" p s.Over
    let mutable s = engine.Start { NewGame.defaults with Name = Some "Ada" }
    show "start" s
    // Adaptively resolve any open menu: dismiss display-only ones, otherwise
    // select the first item (exercises returning a real selection).
    let resolveMenus () =
        while (match s.Pending with Menu _ -> true | _ -> false) do
            let a =
                match s.Pending with
                | Menu(_, PickNone, _) -> Proceed
                | Menu(_, _, it :: _) -> Choose [ it.Key ]
                | _ -> Proceed
            s <- engine.Step s a
            show (sprintf "menu-> %A" a) s
    // 'o' (open) calls getdir() unconditionally; it should surface as
    // Pending = Direction, and the following Move answers it.
    // Extended "pray" should reach dopray and surface its confirm prompt,
    // proving the extended-command path (was previously stubbed to cancel).
    for a in [ Move South; Key 'D'; Move East; Key 'i'; Key 'o'; Move North; Extended "pray" ] do
        resolveMenus ()
        s <- engine.Step s a
        show (sprintf "%A" a) s
    resolveMenus ()
    0

[<EntryPoint>]
let main argv =
    // The map uses non-ASCII glyphs (box-drawing walls, ▫ doorway, ...); render
    // them correctly regardless of the console's default code page.
    Console.OutputEncoding <- System.Text.Encoding.UTF8
    if argv |> Array.contains "native-demo" then nativeDemo () else
    if argv |> Array.contains "dump" then dump () else

    let dumpJson = argv |> Array.contains "--json"
    // Real NetHack (via the DLL) is the default; `--stub` uses the in-process stub.
    let engine =
        if argv |> Array.contains "--stub" then Stub.create ()
        else Native.create ()
    let mutable s = engine.Start { NewGame.defaults with Name = Some "Bob" }

    let mutable running = true
    while running && not s.Over do
        draw s
        Console.WriteLine()
        Console.Write(promptLine s.Pending + " ")
        let key = Console.ReadKey(intercept = true)
        match key.KeyChar with
        | 'Q' -> running <- false
        | 'J' ->
            // Show the JSON wire form of the current state, then wait.
            Console.Clear()
            Console.WriteLine(Json.toJson s)
            Console.WriteLine()
            Console.Write("(press a key to resume) ")
            Console.ReadKey(intercept = true) |> ignore
        | _ ->
            match toAction s.Pending key with
            | Some action ->
                s <- engine.Step s action
                if dumpJson then
                    Console.WriteLine(Json.toJson s.Observation)
            | None -> ()

    Console.WriteLine()
    Console.WriteLine("Goodbye.")
    0
