module NetHack.Tests

open Xunit
open NetHack.Api

// A fresh engine and starting state for each test.
let private newGame () =
    let e = Stub.create ()
    e, e.Start { NewGame.defaults with Name = Some "Ada" }

/// Walk the hero from the start (35,8) onto the fountain (40,10).
let private walkToFountain (e: IEngine) (s: GameState) =
    // (35,8) -> (40,10): +5x,+2y  ==  3 East + 2 Southeast
    [ Move East; Move East; Move East; Move Southeast; Move Southeast ]
    |> List.fold e.Step s

// ---- transitions ----------------------------------------------------------

[<Fact>]
let ``Start yields a Command prompt and a welcome message`` () =
    let _, s = newGame ()
    Assert.Equal(Command, s.Pending)
    Assert.False(s.Over)
    Assert.Contains("welcome to NetHack", List.head s.Observation.Messages)

[<Fact>]
let ``Moving into a wall reports solid stone and does not move`` () =
    let e, s = newGame ()
    let start = s.Observation.Hero
    // From (35,8), keep going North until we hit the top wall at row 5.
    let s' = [ Move North; Move North; Move North; Move North ] |> List.fold e.Step s
    Assert.Equal({ X = start.X; Y = 6 }, s'.Observation.Hero)   // stopped just inside the wall
    Assert.Contains("solid stone", List.head s'.Observation.Messages)

[<Fact>]
let ``Walking into the jackal kills it and removes it from the map`` () =
    let e, s = newGame ()
    // Jackal sits at (45,12); from (35,8) that is +10x,+4y.
    // 4 Southeast -> (39,12), then 6 East: the 6th steps onto the jackal (45,12).
    let route =
        [ Move Southeast; Move Southeast; Move Southeast; Move Southeast
          Move East; Move East; Move East; Move East; Move East; Move East ]
    let s' = route |> List.fold e.Step s
    let hasJackal =
        s'.Observation.Entities |> List.exists (fun ent -> ent.Name = Some "jackal")
    Assert.False(hasJackal)

[<Fact>]
let ``Quaffing while not on the fountain gives a message and stays in Command`` () =
    let e, s = newGame ()
    let s' = e.Step s (Key 'q')
    Assert.Equal(Command, s'.Pending)
    Assert.Contains("anything to drink", List.head s'.Observation.Messages)

[<Fact>]
let ``Quaffing on the fountain opens a MultiChoice prompt, answered to return to Command`` () =
    let e, s = newGame ()
    let onFountain = walkToFountain e s
    Assert.Equal({ X = 40; Y = 10 }, onFountain.Observation.Hero)

    let asked = e.Step onFountain (Key 'q')
    match asked.Pending with
    | MultiChoice(q, choices, dflt) ->
        Assert.Contains("fountain", q)
        Assert.Equal("yn", choices)
        Assert.Equal(Some 'n', dflt)
    | other -> failwith $"expected MultiChoice, got {other}"

    let answered = e.Step asked (Answer 'y')
    Assert.Equal(Command, answered.Pending)
    Assert.NotEmpty(answered.Observation.Messages)

[<Fact>]
let ``Cancel backs out of a MultiChoice prompt`` () =
    let e, s = newGame ()
    let asked = e.Step (walkToFountain e s) (Key 'q')
    match asked.Pending with
    | MultiChoice _ -> ()
    | other -> failwith $"expected MultiChoice, got {other}"

    let cancelled = e.Step asked Cancel
    Assert.Equal(Command, cancelled.Pending)
    Assert.Contains("Never mind.", cancelled.Observation.Messages)

[<Fact>]
let ``Step is pure: the same state can be branched two ways`` () =
    let e, s = newGame ()
    let east = e.Step s (Move East)
    let west = e.Step s (Move West)
    Assert.Equal({ X = 36; Y = 8 }, east.Observation.Hero)
    Assert.Equal({ X = 34; Y = 8 }, west.Observation.Hero)

// ---- JSON wire format ------------------------------------------------------

[<Fact>]
let ``Fieldless union cases serialize to a bare string`` () =
    Assert.Equal("\"Command\"", Json.toJson Command)
    Assert.Equal("\"North\"", Json.toJson North)

[<Fact>]
let ``Tagged union cases carry a type discriminator and named fields`` () =
    let json = Json.toJson (MultiChoice("Drink from the fountain?", "yn", Some 'n'))
    Assert.Contains("\"type\": \"MultiChoice\"", json)
    Assert.Contains("\"question\": \"Drink from the fountain?\"", json)
    Assert.Contains("\"choices\": \"yn\"", json)

[<Fact>]
let ``None option fields are omitted from JSON`` () =
    let _, s = newGame ()
    let json = Json.toJson s.Observation.Status
    // Encumbrance is None on the starting status, so it should not appear.
    Assert.DoesNotContain("encumbrance", json.ToLowerInvariant ())

[<Fact>]
let ``The opaque continuation is never serialized`` () =
    let _, s = newGame ()
    let json = Json.toJson s
    Assert.DoesNotContain("Continuation", json)
    Assert.DoesNotContain("continuation", json)

[<Fact>]
let ``Observation round-trips through JSON`` () =
    let _, s = newGame ()
    let json = Json.toJson s.Observation
    let back = Json.ofJson<Observation> json
    Assert.Equal(s.Observation.Hero, back.Hero)
    Assert.Equal<string list>(s.Observation.Rows, back.Rows)
    Assert.Equal(s.Observation.Status.HP, back.Status.HP)
