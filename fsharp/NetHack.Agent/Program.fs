namespace NetHack.Agent

open System
open System.ClientModel
open System.Reflection
open System.Text.Json

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

open NetHack.Api

module Program =

    /// Matches an exception with JSON content.
    let (|JsonClientError|_|) (exn : exn) =
        match exn with
            | :? ClientResultException as exn ->
                let response = exn.GetRawResponse()
                try
                    use doc =
                        JsonDocument.Parse(
                            response.Content.ToString())
                    let pretty =
                        JsonSerializer.Serialize(
                            doc.RootElement,
                            JsonSerializerOptions(
                                WriteIndented = true))
                    Some pretty
                with :? JsonException ->
                    None
            | _ -> None

    let systemPrompt = """
You are an expert NetHack player controlling a character through a JSON API.

Each turn you receive a JSON GameState:
- observation.rows: 21 strings, the ASCII dungeon map (row 0 is the top).
- observation.hero: your {x,y} position (the '@').
- observation.status: HP, level, gold, hunger, conditions, etc.
- observation.entities: decoded things on the map, each {kind, pos:{x,y}, name,
    color} — kind is HeroSelf, Monster, Pet, Object, or Trap. Use this to know
    exactly what and where the nearby monsters/items are (e.g. name "jackal").
- observation.messages: game messages since your last action.
- pending: what the game is waiting for. "Command" = act freely.
    {type:"YesNo",question,choices} = answer it. {type:"TextLine",prompt} = type
    a line. {type:"Menu",mode,items} = a menu is open; items each have a "key"
    letter and "text". Pick with kind "select" value the letters (e.g. "a" for one,
    "ac" for several), or kind "proceed" to dismiss/cancel a menu.

Map legend: @ you, letters = monsters (d dog, f cat, ...), . floor, # corridor,
| - walls, + door/spellbook, { fountain, $ gold, < up stairs, > down stairs,
) weapon, [ armor, ! potion, ? scroll, / wand, % food, space = unexplored.

Goal: survive, explore the level, fight weak monsters, grab useful items, and
descend the down stairs (>). Avoid obvious death. Keep moving; never stall.

You have NO conversation history. Each turn you see only the current game state
and the short "notes" string you wrote last turn. Always rewrite "notes" with a
concise memory to carry forward: your current goal, discoveries (where the
stairs/shops are, dangers), and your plan. A few lines only — it is not a log.

Respond with an action: a "kind" and its "value".
- Move: value one of N,S,E,W,NE,NW,SE,SW (or up/down while on stairs).
- Key: value a single command key (s search, i inventory, , pickup,
    o open, > descend, < ascend).
- Answer: value "y" or "n" (only when pending is YesNo).
- Text: value the line to type (only when pending is TextLine).
- Number: value an integer (when asked for a quantity).
- Select: value the menu letters to choose (e.g. "a" or "ac"), when a Menu is open.
- Proceed: dismiss a menu or prompt.
Always include one short sentence of reasoning.
"""

    let getUserPrompt (state : GameState) (notes : string) =
        "Current game state (JSON):\n" + Json.toJson state
        + "\n\nYour notes from last turn:\n"
        + (if notes = "" then "(none yet)" else notes)

    let config =
        let assembly = Assembly.GetExecutingAssembly()
        ConfigurationBuilder()
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .AddEnvironmentVariables()
            .Build()

    let openAIClient =
        OpenAIClient(
            ApiKeyCredential(config["OpenAI:ApiKey"]),
            OpenAIClientOptions(
                Endpoint = Uri(config["OpenAI:BaseUrl"])))

    let chatClient =
        openAIClient
            .GetChatClient(config["OpenAI:Model"])
            .AsIChatClient()

    let useJsonSchemaResponseFormat =
        let setting =
            config["OpenAI:UseJsonSchemaResponseFormat"].ToLower()
        match setting with
            | "false" | "f" | "0" -> false
            | "true"  | "t" | "1" -> true
            | _ -> true

    let engine = Native.create ()

    let render (step: int) (s: GameState) (note: string) =
        // Clear only when attached to a real console (skip when output is piped).
        if not Console.IsOutputRedirected then
            try Console.Clear() with _ -> ()
        for r in s.Observation.Rows do Console.WriteLine r
        let st = s.Observation.Status
        Console.WriteLine()
        Console.WriteLine(
            $"{st.Title}  T:{st.Turns} Dlvl:{st.DungeonLevel} "
            + $"HP:{st.HP}/{st.HPMax} Pw:{st.Power}/{st.PowerMax} AC:{st.ArmorClass} $:{st.Gold}")
        if not (List.isEmpty s.Observation.Messages) then
            Console.WriteLine("game: " + String.concat " | " s.Observation.Messages)
        Console.WriteLine($"pending: {s.Pending}")
        if note <> "" then Console.WriteLine($"[step {step}] {note}")
        Console.WriteLine(String('-', 64))
        Console.Out.Flush()   // show progress even when output is piped/redirected

    /// Translate the model's action into the strongly typed NetHack.Api Action DU.
    let toAction (a: AgentAction) : Action =
        let v = (if isNull a.value then "" else a.value).Trim()
        match a.kind with
        | ActionKind.Move ->
            match v.ToLowerInvariant() with
            | "n" | "north" -> Move North
            | "s" | "south" -> Move South
            | "e" | "east" -> Move East
            | "w" | "west" -> Move West
            | "ne" | "northeast" -> Move Northeast
            | "nw" | "northwest" -> Move Northwest
            | "se" | "southeast" -> Move Southeast
            | "sw" | "southwest" -> Move Southwest
            | "up" | "<" -> Move Up
            | "down" | ">" -> Move Down
            | _ -> Key 's'
        | ActionKind.Key -> if v.Length > 0 then Key v.[0] else Proceed
        | ActionKind.Answer -> Answer(if v.Length > 0 then v.[0] else 'y')
        | ActionKind.Text -> Text v
        | ActionKind.Number -> (match Int32.TryParse v with true, n -> Number n | _ -> Number 0)
        | ActionKind.Select -> Choose(v |> Seq.filter (Char.IsWhiteSpace >> not) |> Seq.toList)
        | _ -> Proceed

    let rec run stepNum state notes =
        task {
            try
                render stepNum state notes

                let messages =
                    [
                        ChatMessage(ChatRole.System, systemPrompt)
                        ChatMessage(ChatRole.User, getUserPrompt state notes)
                    ]
                let! response =
                    chatClient.GetResponseAsync<AgentAction>(
                        messages,
                        useJsonSchemaResponseFormat
                            = useJsonSchemaResponseFormat)
                let aa = response.Result
                
                let state = engine.Step state (toAction aa)
                if state.Over then return ()
                else return! run (stepNum + 1) state aa.notes
            with
                | JsonClientError json -> return printfn $"{json}"
                | exn -> return printfn $"{exn.Message}"
        }
            
    let state = engine.Start NewGame.defaults
    run 1 state ""
        |> Async.AwaitTask
        |> Async.RunSynchronously
