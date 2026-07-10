namespace NetHack.Agent

open System
open System.ClientModel
open System.ComponentModel
open System.IO
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

    let createView (state : GameState) (aa : AgentAction) =

        use wtr = new StringWriter()

        for row in state.Observation.Rows do
            wtr.WriteLine($"{row}")

        let status = state.Observation.Status
        wtr.WriteLine()
        wtr.WriteLine($"{status.Title} T:{status.Turns} Dlvl:{status.DungeonLevel} \
            HP:{status.HP}/{status.HPMax} Pw:{status.Power}/{status.PowerMax} \
            AC:{status.ArmorClass} $:{status.Gold}")

        for msg in state.Observation.Messages do
            wtr.WriteLine(msg)

        wtr.WriteLine()
        wtr.WriteLine($"Action: {aa.Kind} {aa.Value}")
        wtr.WriteLine($"Reasoning: {aa.Reasoning}")

        wtr.WriteLine()
        wtr.WriteLine($"Note: {aa.Note}")

        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    let render state aa =

        let view = createView state aa

        if not Console.IsOutputRedirected then
            try Console.Clear() with _ -> ()
        Console.WriteLine(view)

        use wtr = new StreamWriter("Agent.log", append = true)
        fprintfn wtr "%s" view

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

    // e.g. ""try again in 3m10.7712s"
    let tryParseWaitTime text =
        let m = Regex.Match(text, @"try again in ([\d.hms]+)")
        if m.Success then
            Regex.Matches(m.Groups[1].Value, @"([\d.]+)(ms|h|m|s)")
                |> Seq.map (fun m ->
                    let value = Double.Parse(m.Groups[1].Value)
                    match m.Groups[2].Value with
                        | "h"  -> TimeSpan.FromHours value
                        | "m"  -> TimeSpan.FromMinutes value
                        | "ms" -> TimeSpan.FromMilliseconds value
                        | _    -> TimeSpan.FromSeconds value)
                |> Seq.reduce (+)
                |> Some
        else None

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

    let rec run state aa waitNum =

        let wait (duration : TimeSpan) =
            let duration = duration + TimeSpan.FromSeconds(1.0)   // add a safety margin
            printfn $"Waiting {duration} ({waitNum})"
            task {
                do! Async.Sleep(duration)
                return! run state aa (waitNum + 1)
            }

        task {
            try
                render state aa

                let messages =
                    [
                        ChatMessage(ChatRole.System, systemPrompt)
                        ChatMessage(ChatRole.User, getUserPrompt state aa.Note)
                    ]
                let! response =
                    chatClient.GetResponseAsync<AgentAction>(
                        messages,
                        useJsonSchemaResponseFormat
                            = useJsonSchemaResponseFormat)
                let aa = response.Result
                
                let state = engine.Step state (toAction aa)
                if state.Over then return ()
                else return! run state aa 0

            with exn ->
                match tryParseWaitTime exn.Message with
                    | Some duration ->
                        return! wait duration
                    | None ->
                        match exn with
                            | :? ClientResultException as exn ->
                                if exn.Status = 429 then
                                    let duration = TimeSpan.FromSeconds(10.0)
                                    return! wait duration
                                else
                                    printfn $"{exn.Message}"

                                    let response = exn.GetRawResponse()

                                    let content = response.Content.ToString()
                                    printfn ""
                                    match tryPrettyJson content with
                                        | Some json -> printfn $"{json}"
                                        | None -> printfn $"{content}"

                                    printfn ""
                                    for header in response.Headers do
                                        printfn $"{header}"
                            | _ ->
                                printfn $"{exn.Message}"
        }
            
    let state = engine.Start NewGame.defaults
    let aa =
        {
            Reasoning = ""
            Kind = ActionKind.Proceed
            Value = ""
            Note = ""
        }
    run state aa 0
        |> Async.AwaitTask
        |> Async.RunSynchronously
