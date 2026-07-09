module NetHack.Agent.Program

open System
open System.ComponentModel
open System.ClientModel
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration
open OpenAI
open NetHack.Api

/// The kinds of action the model may choose. As a string enum this schematizes
/// to a constrained set the structured-output layer enforces exactly. (A full
/// F# discriminated union cannot be used here: System.Text.Json reports it an
/// "unsupported type", and with the FSharp.SystemTextJson converter the schema
/// collapses to "any", which strict structured output rejects.)
[<System.Text.Json.Serialization.JsonConverter(typeof<System.Text.Json.Serialization.JsonStringEnumConverter>)>]
type ActionKind =
    | Move = 0 | Key = 1 | Answer = 2 | Text = 3 | Number = 4 | Select = 5 | Proceed = 6

/// The structured action the model returns each turn: a typed `kind` plus a
/// `value` argument interpreted per kind. `toAction` parses it into the strongly
/// typed NetHack.Api.Action DU immediately, so the rest of the code is DU-based.
[<CLIMutable>]
type AgentAction =
    { [<Description("One short sentence explaining the choice.")>]
      reasoning: string
      [<Description("The kind of action to take.")>]
      kind: ActionKind
      [<Description("Argument for the action. Move: one of N,S,E,W,NE,NW,SE,SW,up,down. Key/Answer: a single character. Text: the line to type. Number: an integer. Select: the menu letters (e.g. \"a\" or \"ac\"). Proceed: ignored.")>]
      value: string
      [<Description("Your running memory to carry to the next turn: current goal, discoveries (stairs, shops, dangers), and plan. Concise; this is your only memory between turns.")>]
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

/// Read a setting from user secrets / env vars, falling back to a flat env var
/// (e.g. OPENAI_API_KEY) and then a default.
let private setting (config: IConfiguration) key envFallback deflt =
    match config[key] with
    | null | "" ->
        match Environment.GetEnvironmentVariable envFallback with
        | null | "" -> deflt
        | v -> v
    | v -> v

let intSetting config key deflt =
    match Int32.TryParse(setting config key "" (string deflt)) with
    | true, n -> n | _ -> deflt

let boolSetting config key deflt =
    match (setting config key "" "").ToLower() with
        | "false" | "f" | "0" -> false
        | "true"  | "t" | "1" -> true
        | _ -> deflt

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
            // Per-call timeout so a stalled/rate-limited request can never hang;
            // step delay paces requests (free tiers like GitHub Models are rate
            // limited, so raise it if you see 429s).
            let timeoutSec = intSetting config "OpenAI:TimeoutSeconds" 60
            let stepDelayMs = intSetting config "OpenAI:StepDelayMs" 1500
            let useJsonSchemaResponseFormat = boolSetting config "OpenAI:UseJsonSchemaResponseFormat" true
            let clientOptions = OpenAIClientOptions(NetworkTimeout = TimeSpan.FromSeconds(float timeoutSec))
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
                    // Enforce the timeout ourselves via WhenAny: the SDK has been
                    // observed to ignore cancellation on a hung connection, so we
                    // never rely on it to unblock us.
                    let cts = new CancellationTokenSource()
                    let callTask =
                        chat.GetResponseAsync<AgentAction>(
                            messages,
                            useJsonSchemaResponseFormat = useJsonSchemaResponseFormat,
                            cancellationToken = cts.Token)
                    let! winner = Task.WhenAny(callTask :> Task, Task.Delay(timeoutSec * 1000))
                    let mutable errMsg = ""
                    if obj.ReferenceEquals(winner, callTask) then
                        try
                            let! resp = callTask
                            decision <- Ok resp.Result
                        with ex ->
                            let inner =
                                if isNull ex.InnerException then "" else " <- " + ex.InnerException.Message
                            errMsg <- $"{ex.GetType().Name}: {ex.Message}{inner}"
                    else
                        cts.Cancel()   // best-effort; abandon the hung call regardless
                        callTask.ContinueWith(fun (t: Task<_>) -> t.Exception |> ignore) |> ignore
                        errMsg <- $"Timeout: no model response within {timeoutSec}s"
                    if errMsg <> "" then
                        decision <- Error errMsg
                        if attempt < 3 then
                            let rateLimited = errMsg.Contains "429" || errMsg.ToLowerInvariant().Contains "rate"
                            do! Task.Delay(if rateLimited then 20000 else 1500)
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
                do! Task.Delay stepDelayMs
            render step state (if state.Over then "GAME OVER" else "step budget reached")
            return 0
    }

[<EntryPoint>]
let main argv =
    (run argv).GetAwaiter().GetResult()
