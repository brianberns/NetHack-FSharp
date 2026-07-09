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
    Console.WriteLine()
    Console.WriteLine(
        $"{st.Title}   St:{st.Strength} Dx:{st.Dexterity} Co:{st.Constitution} "
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
    | YesNo _ -> Some(Answer key.KeyChar)
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
    | YesNo(q, choices, _) -> $"{q} [{choices}]"
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

[<EntryPoint>]
let main argv =
    if argv |> Array.contains "dump" then dump () else

    let dumpJson = argv |> Array.contains "--json"
    let engine = Stub.create ()
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
