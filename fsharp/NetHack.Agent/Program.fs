namespace NetHack.Agent

open System
open System.ClientModel
open System.ComponentModel
open System.Text.Encodings.Web
open System.Reflection
open System.Text.RegularExpressions
open System.Text.Json

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

module Program =

    let tryPrettyJson (text : string) =
        try
            use doc = JsonDocument.Parse(text)
            let pretty =
                JsonSerializer.Serialize(
                    doc.RootElement,
                    JsonSerializerOptions(
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))   // avoid escaping Unicode characters
            Some pretty
        with :? JsonException -> None

    let systemPrompt = """
You are an expert NetHack player controlling a character through a JSON API.

Respond with an action: a "kind" and its "value".
- Move: one of N,S,E,W,NE,NW,SE,SW.
- Key: a single command key (s search, i inventory, p pickup,
    o open, > descend, < ascend).
- Answer: "y" or "n" (only when pending is YesNo).
- Text: the line to type (only when pending is TextLine).
- Number: an integer (when asked for a quantity).
- Select: the menu letters to choose (e.g. "a" or "ac"), when a Menu is open.
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

            with exn ->
                let m = Regex.Match(exn.Message, @"try again in ([0-9.]+)s")
                if m.Success then
                    let duration =
                        m.Groups[1].Value
                            |> Double.Parse
                            |> TimeSpan.FromSeconds
                    printfn $"Waiting {duration}"
                    do! Async.Sleep(duration)
                    return! run stepNum state notes
                else
                    printfn $"{exn.Message}"
        }
            
    let state = engine.Start NewGame.defaults
    run 1 state ""
        |> Async.AwaitTask
        |> Async.RunSynchronously
