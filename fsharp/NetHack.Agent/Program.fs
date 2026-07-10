namespace NetHack.Agent

open System
open System.ClientModel
open System.ComponentModel
open System.IO
open System.Reflection
open System.Text.Json.Serialization

open Microsoft.Extensions.Configuration

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

    let getPrompt (state : GameState) (note : string) =
        String.concat "\n" [
            $"You are an expert NetHack player controlling a character. \
            Respond with:
            * An action kind and value, and \
            * One short sentence of reasoning for this action, and \
            * A note that contains anything you want to remember across turns. \
            Current game state (JSON):"; Json.toJson state
            if not (String.IsNullOrWhiteSpace(note)) then
                $"Your note from last turn:"; note
        ]

    let agent =
        let config =
            ConfigurationBuilder()
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .AddEnvironmentVariables()
                .Build()
        Agent.create config Gemini.flash2_5

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
        Console.Write(view)

        use wtr = new StreamWriter("Agent.log", append = true)
        fprintfn wtr "%s" view

        if not Console.IsOutputRedirected then
            Console.ReadLine() |> ignore

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

    let rec run state aa waitNum =

        let wait (duration : TimeSpan) =
            printfn ""
            printfn $"Waiting {duration} ({waitNum})"
            async {
                do! Async.Sleep(duration)
                return! run state aa (waitNum + 1)
            }

        async {
            try
                render state aa

                let! aa =
                    let prompt = getPrompt state aa.Note
                    Agent.getResultAsync<AgentAction> prompt agent
                
                let state = engine.Step state (toAction aa)
                if state.Over then return ()
                else return! run state aa 0

            with 
                | :? ClientResultException as exn ->
                    printfn $"{exn.Message}"

                    let response = exn.GetRawResponse()

                    let content = response.Content.ToString()
                    printfn ""
                    printfn $"{content}"

                    printfn ""
                    for header in response.Headers do
                        printfn $"{header}"
                | exn ->
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
        |> Async.RunSynchronously
