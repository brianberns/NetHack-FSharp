module NetHack.Agent.Program

open System
open System.ComponentModel
open System.ClientModel
open System.Reflection
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration
open OpenAI
open NetHack.Api

/// The structured action we ask the model to return each turn. Kept flat and
/// string-typed so it produces a clean JSON schema the model can satisfy.
[<CLIMutable>]
type AgentAction =
    { [<Description("One short sentence explaining the choice.")>]
      reasoning: string
      [<Description("One of: move, key, answer, text, number, select, proceed.")>]
      kind: string
      [<Description("move: N,S,E,W,NE,NW,SE,SW,up,down; key/answer: a single character; text: the line; number: an integer; select: the menu letters to pick (e.g. \"a\" or \"ac\").")>]
      value: string
      [<Description("Your running memory to carry to the next turn: current goal, what you've discovered (stairs, shops, dangers), and your plan. Keep it concise, a few lines. This is your only memory between turns.")>]
      notes: string }

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

Respond with an action:
- kind "move", value one of N,S,E,W,NE,NW,SE,SW (or up/down while on stairs).
- kind "key", value a single command key (s search, i inventory, , pickup,
  o open, > descend, < ascend).
- kind "answer", value "y" or "n" (only when pending is YesNo).
- kind "text", value the line to type (only when pending is TextLine).
- kind "proceed" to dismiss a menu/prompt.
Always include one short sentence of reasoning.
"""

/// Translate the model's action into a NetHack.Api Action.
let toAction (a: AgentAction) : Action =
    let v = (if isNull a.value then "" else a.value).Trim()
    match (if isNull a.kind then "" else a.kind).Trim().ToLowerInvariant() with
    | "move" ->
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
    | "key" -> if v.Length > 0 then Key v.[0] else Proceed
    | "answer" -> Answer(if v.Length > 0 then v.[0] else 'y')
    | "text" -> Text v
    | "number" -> (match Int32.TryParse v with true, n -> Number n | _ -> Number 0)
    | "select" -> Choose(v |> Seq.filter (Char.IsWhiteSpace >> not) |> Seq.toList)
    | "proceed" -> Proceed
    | _ -> Proceed

/// Read a setting from user secrets / env vars, falling back to a flat env var
/// (e.g. OPENAI_API_KEY) and then a default.
let private setting (config: IConfiguration) key envFallback deflt =
    match config[key] with
    | null | "" ->
        match Environment.GetEnvironmentVariable envFallback with
        | null | "" -> deflt
        | v -> v
    | v -> v

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

/// A safe action to take when the model call keeps failing, matched to whatever
/// the game is currently asking for.
let private fallback (s: GameState) : Action =
    match s.Pending with
    | YesNo(_, _, dflt) -> Answer(defaultArg dflt 'n')
    | Menu _ | More -> Proceed
    | TextLine _ -> Text ""
    | _ -> Key 's'

let run (argv: string[]) : Task<int> =
    task {
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly(), true)
                .AddEnvironmentVariables()
                .Build()
        let apiKey = setting config "OpenAI:ApiKey" "OPENAI_API_KEY" ""
        if apiKey = "" then
            eprintfn "No API key. Set it with:"
            eprintfn "  dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\" --project fsharp/NetHack.Agent"
            eprintfn "(or the OPENAI_API_KEY environment variable). Optional: OpenAI:Model, OpenAI:BaseUrl."
            return 1
        else
            let model = setting config "OpenAI:Model" "OPENAI_MODEL" "gpt-4o-mini"
            let baseUrl = setting config "OpenAI:BaseUrl" "OPENAI_BASE_URL" ""
            let clientOptions = OpenAIClientOptions()
            if baseUrl <> "" then clientOptions.Endpoint <- Uri baseUrl
            let openAi = OpenAIClient(ApiKeyCredential apiKey, clientOptions)
            let chat: IChatClient = openAi.GetChatClient(model).AsIChatClient()

            let maxSteps =
                argv
                |> Array.tryPick (fun a -> match Int32.TryParse a with true, n -> Some n | _ -> None)
                |> Option.defaultValue 40

            let engine = Native.create ()
            let mutable state = engine.Start { NewGame.defaults with Name = Some "Aiven" }
            // The agent's only memory across turns: a short scratchpad it rewrites.
            let mutable notes = ""

            let mutable step = 0
            while not state.Over && step < maxSteps do
                step <- step + 1
                // Each request is self-contained: system prompt + the current
                // state and the agent's own notes. No accumulated history, so the
                // request size stays bounded regardless of how long the game runs.
                let user =
                    "Current game state (JSON):\n" + Json.toJson state
                    + "\n\nYour notes from last turn:\n"
                    + (if notes = "" then "(none yet)" else notes)
                let messages =
                    [ ChatMessage(ChatRole.System, systemPrompt)
                      ChatMessage(ChatRole.User, user) ]
                // Ask the model, retrying a couple of times on transient failures.
                let mutable decision : Result<AgentAction, string> = Error "no attempt"
                let mutable attempt = 0
                while (match decision with Ok _ -> false | _ -> true) && attempt < 3 do
                    attempt <- attempt + 1
                    try
                        let! resp = chat.GetResponseAsync<AgentAction>(messages)
                        decision <- Ok resp.Result
                    with ex ->
                        let inner =
                            if isNull ex.InnerException then "" else " <- " + ex.InnerException.Message
                        decision <- Error $"{ex.GetType().Name}: {ex.Message}{inner}"
                        if attempt < 3 then do! Task.Delay 1000
                match decision with
                | Ok d ->
                    notes <- (if isNull d.notes then "" else d.notes)
                    render step state $"{d.kind} {d.value} — {d.reasoning}\n[notes] {notes}"
                    state <- engine.Step state (toAction d)
                | Error msg ->
                    let fb = fallback state
                    // persist the full error so it survives the screen clears
                    try IO.File.AppendAllText("agent-errors.log", $"step {step}: {msg}\n")
                    with _ -> ()
                    render step state $"agent error ({attempt} tries): {msg}  |  fallback: {fb}"
                    do! Task.Delay 1500   // leave it on screen long enough to read
                    state <- engine.Step state fb
                do! Task.Delay 400
            render step state (if state.Over then "GAME OVER" else "step budget reached")
            return 0
    }

[<EntryPoint>]
let main argv =
    (run argv).GetAwaiter().GetResult()
