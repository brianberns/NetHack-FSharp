namespace NetHack.Agent

open System
open System.ClientModel
open System.ComponentModel
open System.Text.Encodings.Web
open System.Reflection
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Serialization

open Microsoft.Extensions.AI
open Microsoft.Extensions.Configuration

open OpenAI

open NetHack.Api

/// The kinds of action the model may choose. As a string enum this schematizes
/// to a constrained set the structured-output layer enforces exactly.
[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type ActionKind =
    | Move = 0
    | Key = 1
    | Answer = 2
    | Text = 3
    | Number = 4
    | Select = 5
    | Proceed = 6

/// The structured action the model returns each turn: a typed `kind` plus a
/// `value` argument interpreted per kind. `toAction` parses it into the strongly
/// typed NetHack.Api.Action DU immediately, so the rest of the code is DU-based.
type AgentAction =
    {
        [<Description("One short sentence explaining this action.")>]
        Reasoning : string

        [<Description("The kind of action to take.")>]
        Kind : ActionKind

        [<Description("Argument for the action. Move: one of N|S|E|W|NE|NW|SE|SW. \
            Key/Answer: a single character. \
            Text: the line to type. \
            Number: an integer. \
            Select: the menu letters (e.g. 'a' or 'ac'. \
            Proceed: ignored.")>]
        Value : string

        [<Description("Your running memory to carry to the next turn: \
            current goal, discoveries (stairs, shops, dangers), and plan. \
            Be concise. This is your only memory between turns.")>]
        Note : string
    }

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

    let systemPrompt =
        "You are an expert NetHack player controlling a character through a JSON API. \
        Respond with an action: a 'kind' and its 'value'. \
        Always include one short sentence of reasoning."

    let getUserPrompt (state : GameState) (note : string) =
        String.concat "\n\n" [
            $"Current game state (JSON):\n{Json.toJson state}"
            if not (String.IsNullOrWhiteSpace(note)) then
                $"Your note from last turn:\n{note}"
        ]

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

    let render (state : GameState) (reasoning : string) (note : string) =

        // Clear only when attached to a real console (skip when output is piped).
        if not Console.IsOutputRedirected then
            try Console.Clear() with _ -> ()

        for row in state.Observation.Rows do
            Console.WriteLine(row)

        let status = state.Observation.Status
        Console.WriteLine()
        Console.WriteLine(
            $"{status.Title} T:{status.Turns} Dlvl:{status.DungeonLevel} \
            HP:{status.HP}/{status.HPMax} Pw:{status.Power}/{status.PowerMax} \
            AC:{status.ArmorClass} $:{status.Gold}")

        Console.WriteLine(String.concat " | " state.Observation.Messages)
        Console.WriteLine($"Expecting: {state.Pending}")
        Console.WriteLine($"{reasoning}")
        Console.WriteLine($"{note}")
        Console.WriteLine(String('-', 64))

        Console.Out.Flush()   // show progress even when output is piped/redirected

    /// Translate the model's action into the strongly typed NetHack.Api Action DU.
    let toAction (a: AgentAction) : Action =
        let v = (if isNull a.Value then "" else a.Value).Trim()
        match a.Kind with
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

    let rec run state reasoning note =
        task {
            try
                render state reasoning note

                let messages =
                    [
                        ChatMessage(ChatRole.System, systemPrompt)
                        ChatMessage(ChatRole.User, getUserPrompt state note)
                    ]
                let! response =
                    chatClient.GetResponseAsync<AgentAction>(
                        messages,
                        useJsonSchemaResponseFormat
                            = useJsonSchemaResponseFormat)
                let aa = response.Result
                
                let state = engine.Step state (toAction aa)
                if state.Over then return ()
                else return! run state aa.Reasoning aa.Note

            with exn ->
                let m = Regex.Match(exn.Message, @"try again in ([0-9.]+)(ms|s)")
                if m.Success then
                    let duration =
                        let value = Double.Parse(m.Groups[1].Value)
                        match m.Groups[2].Value with
                            | "ms" -> TimeSpan.FromMilliseconds(value)
                            | _ -> TimeSpan.FromSeconds(value)
                    let duration = duration + TimeSpan.FromSeconds(1.0)   // add a safety margin
                    printfn $"Waiting {duration}"
                    do! Async.Sleep(duration)
                    return! run state reasoning note
                else
                    printfn $"{exn.Message}"
        }
            
    let state = engine.Start NewGame.defaults
    run state "" ""
        |> Async.AwaitTask
        |> Async.RunSynchronously
