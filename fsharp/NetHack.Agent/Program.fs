module NetHack.Agent.Program

open System
open System.Collections.Generic
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
      value: string }

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
    Console.Clear()
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

/// Keep the system message plus the most recent turns so context stays bounded.
let private trim (messages: List<ChatMessage>) =
    while messages.Count > 16 do messages.RemoveAt(1)

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
            let messages = List<ChatMessage>()
            messages.Add(ChatMessage(ChatRole.System, systemPrompt))

            let mutable step = 0
            while not state.Over && step < maxSteps do
                step <- step + 1
                messages.Add(ChatMessage(ChatRole.User, Json.toJson state))
                let! decision =
                    task {
                        try
                            let! resp = chat.GetResponseAsync<AgentAction>(messages)
                            return Some resp.Result
                        with ex ->
                            eprintfn "agent error: %s" ex.Message
                            return None
                    }
                match decision with
                | Some d ->
                    messages.Add(ChatMessage(ChatRole.Assistant, Json.toJson d))
                    trim messages
                    render step state $"{d.kind} {d.value} — {d.reasoning}"
                    state <- engine.Step state (toAction d)
                | None ->
                    render step state "agent error; searching"
                    state <- engine.Step state (Key 's')
                do! Task.Delay 400
            render step state (if state.Over then "GAME OVER" else "step budget reached")
            return 0
    }

[<EntryPoint>]
let main argv =
    (run argv).GetAwaiter().GetResult()
