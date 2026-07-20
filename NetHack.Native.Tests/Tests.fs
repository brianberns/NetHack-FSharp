module NetHack.Native.Tests

open System
open System.IO
open Xunit
open NetHack.Api

// One live NetHack game per process (state lives in C globals), so these tests
// must not run in parallel. They cannot each start their own game either, so a
// single seeded game is driven through one scripted sequence in GameFixture and
// each test asserts on a captured snapshot of that run.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

/// True when the native DLL has been built, so the tests can be skipped (rather
/// than failed) on a machine without the Release|x64 native build.
let private nativeAvailable =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then false
        else
            let dll =
                Path.Combine(
                    dir.FullName, "Core", "binary", "Release", "x64",
                    "NetHackNative.dll")
            if File.Exists dll then true else up dir.Parent
    up (DirectoryInfo(AppContext.BaseDirectory))

/// Fixed seed → a reproducible character and level. If a NetHack upgrade shifts
/// the RNG, update the expected identity below (that's the point: it pins it).
let private seed = 1234

/// Drives one seeded wizard game through a single scripted sequence at
/// construction and captures a GameState snapshot at each milestone. GameState
/// is an immutable value (the native engine leaves Continuation null), so the
/// snapshots stay valid for the tests to read afterwards even though the live
/// game has moved on. Shared across the whole test class via IClassFixture, so
/// the one-game-per-process rule is honoured.
type GameFixture() =

    let run () =
        let engine = Native.create ()
        // Wizard mode lets us wish known objects into being; seed fixes the level.
        let start =
            engine.Start { NewGame.defaults with Name = Some "Tester"; Seed = Some seed; Wizard = true }

        // Inventory menu (exercises the select-menu path).
        let inv = engine.Step start (Key 'i')
        let afterInv = engine.Step inv Proceed

        // 'e'at opens a getobj prompt whose only exit is ESC; Cancel must abort it.
        let eatPrompt = engine.Step afterInv (Key 'e')
        let afterCancel = engine.Step eatPrompt Cancel

        // Wish an (unlocked, so #loot needs no lock-picking) chest and drop it, so
        // the hero stands on it.
        let wished = engine.Step (engine.Step afterCancel (Extended "wizwish")) (Text "unlocked chest")
        let inv2 = engine.Step wished (Key 'i')
        let chestKey =
            match inv2.Pending with
            | Menu (_, _, its) ->
                its |> List.tryPick (fun it -> if it.Text.Contains "chest" then Some it.Key else None)
            | _ -> None
            |> Option.defaultWith (fun () -> failwith "wished chest not in inventory")
        let dropped =
            let afterInv2 = engine.Step inv2 Proceed
            let dropPrompt = engine.Step afterInv2 (Key 'd')  // "What do you want to drop?"
            engine.Step dropPrompt (Answer chestKey)

        // "Look inside" the chest: container_contents writes plain text into a menu
        // window (not add_menu rows), which must still reach Observation.Messages.
        let lootMenu =
            let lootStart = engine.Step dropped (Extended "loot")
            match lootStart.Pending with
            | MultiChoice _ -> engine.Step lootStart (Answer 'y')  // "loot it? [ynq]"
            | _ -> lootStart
        let looked =
            match lootMenu.Pending with
            | Menu _ -> engine.Step lootMenu (Choose [ ':' ])
            | other -> failwith $"expected the 'Do what with the chest?' menu, got {other}"
        let afterLoot =
            match looked.Pending with
            | Menu _ -> engine.Step looked (Choose [ 'q' ])  // leave the loot menu
            | _ -> looked

        // Answer a Direction prompt with a non-direction key; the cmdassist help
        // window must be reported, not silently dropped.
        let badDir =
            let dirPrompt = engine.Step afterLoot (Extended "kick")
            match dirPrompt.Pending with
            | Direction _ -> engine.Step dirPrompt (Key 'w')  // 'w' is not a direction key
            | other -> failwith $"expected a Direction prompt from #kick, got {other}"

        // Wish + drop a second item so the hero's tile holds two stacks (a pile).
        let wished2 = engine.Step (engine.Step badDir (Extended "wizwish")) (Text "ring of protection")
        let inv3 = engine.Step wished2 (Key 'i')
        let ringKey =
            match inv3.Pending with
            | Menu (_, _, its) ->
                its |> List.tryPick (fun it -> if it.Text.Contains "ring" then Some it.Key else None)
            | _ -> None
            |> Option.defaultWith (fun () -> failwith "wished ring not in inventory")
        let twoStacks =
            let afterInv3 = engine.Step inv3 Proceed
            engine.Step (engine.Step afterInv3 (Key 'd')) (Answer ringKey)
        // Open the pickup menu (assert its contents in the test), then cancel so the
        // pile survives; then step off so the vacated square can be checked.
        let pickMenu = engine.Step twoStacks (Key ',')
        let afterPick = engine.Step pickMenu Proceed  // cancel the menu; pile untouched
        let pileCell = afterPick.Observation.Hero
        let mutable stepped = afterPick
        for dir in [ Move East; Move West; Move South; Move North ] do
            if stepped.Observation.Hero = pileCell then stepped <- engine.Step stepped dir

        // RepeatKey types a count prefix ("10s") so one call rests many turns.
        let beforeRest = stepped.Observation.Status.Turns
        let rested = engine.Step stepped (RepeatKey(10, 's'))

        // Run feeds "G" + a direction key.
        let ran = engine.Step rested (Run West)

        // Overlong getlin reply: must be truncated, never overrun the game thread's
        // char[BUFSZ] buffer (which crashes later at an unrelated spot).
        let overlong = String('a', 256 * 3)
        let afterOverlong =
            let wishPrompt = engine.Step ran (Extended "wizwish")
            match wishPrompt.Pending with
            | TextLine _ -> engine.Step wishPrompt (Text overlong)
            | other -> failwith $"expected a wish TextLine prompt, got {other}"
        let afterOverlong2 = engine.Step afterOverlong (Key 'i')

        // Farlook drives getpos -> nh_poskey (the coordxy* out-param write site).
        let afterFarlook =
            let dismissed = engine.Step afterOverlong2 Proceed  // close the inventory menu
            let looking = engine.Step dismissed (Key ';')       // farlook
            engine.Step looking Cancel                           // ESC out of getpos

        // Remembered-unseen-monster 'I': create a stationary lichen adjacent, blind
        // the hero so it becomes unseen (hero stays put, lichen doesn't move), then
        // #wizsmell it -- map_invisible places the 'I'. It must surface as an entity.
        let unseen =
            let withLichen =
                engine.Step (engine.Step afterFarlook (Extended "wizgenesis")) (Text "lichen")
            let hero = withLichen.Observation.Hero
            let lichen =
                withLichen.Observation.Entities
                |> List.tryFind (fun e -> (defaultArg e.Name "").Contains "lichen")
                |> Option.defaultWith (fun () -> failwith "wizgenesis lichen not found on the map")
            let blinded =
                let w = engine.Step (engine.Step withLichen (Extended "wizwish")) (Text "blindfold")
                let invb = engine.Step w (Key 'i')
                let bf =
                    match invb.Pending with
                    | Menu (_, _, its) ->
                        its |> List.tryPick (fun it -> if it.Text.Contains "blindfold" then Some it.Key else None)
                    | _ -> None
                    |> Option.defaultWith (fun () -> failwith "wished blindfold not in inventory")
                engine.Step (engine.Step (engine.Step invb Proceed) (Key 'P')) (Answer bf)
            let sgn n = if n > 0 then 1 elif n < 0 then -1 else 0
            let d =
                match sgn (lichen.Pos.X - hero.X), sgn (lichen.Pos.Y - hero.Y) with
                | 1, 0 -> East  | -1, 0 -> West  | 0, 1 -> South | 0, -1 -> North
                | 1, 1 -> Southeast | 1, -1 -> Northeast | -1, 1 -> Southwest | _ -> Northwest
            // #wizsmell: getpos cursor starts on the hero; move onto the lichen and
            // select it, then ESC out of the smell loop.
            let sm = engine.Step blinded (Extended "wizsmell")
            engine.Step (engine.Step (engine.Step sm (Move d)) (Key '.')) Cancel

        {| Start = start
           Inv = inv
           EatPrompt = eatPrompt
           AfterCancel = afterCancel
           Dropped = dropped
           LootMenu = lootMenu
           Looked = looked
           AfterLoot = afterLoot
           BadDir = badDir
           PickMenu = pickMenu
           PileCell = pileCell
           Stepped = stepped
           BeforeRest = beforeRest
           Rested = rested
           Ran = ran
           AfterOverlong = afterOverlong
           AfterOverlong2 = afterOverlong2
           AfterFarlook = afterFarlook
           Unseen = unseen |}

    let states = if nativeAvailable then Some(run ()) else None

    member _.Available = states.IsSome
    /// The captured snapshots. Only touch after Skip.IfNot(fixture.Available, ...).
    member _.S = states |> Option.defaultWith (fun () -> failwith "native engine unavailable")


type NativeTests(fixture: GameFixture) =
    let skipUnlessNative () =
        Skip.IfNot(fixture.Available, "NetHackNative.dll (Release|x64) not built")

    interface IClassFixture<GameFixture>

    // Determinism: seed 1234 always rolls this character (proves the seed took).
    [<SkippableFact>]
    member _.``seed pins a reproducible character``() =
        skipUnlessNative ()
        let start = fixture.S.Start
        Assert.Equal("Valkyrie", start.Observation.Character.Role)
        Assert.Equal("human", start.Observation.Character.Race)
        Assert.Equal("female", start.Observation.Character.Gender)

    // Structural invariants that hold on any level.
    [<SkippableFact>]
    member _.``observation is structurally well-formed``() =
        skipUnlessNative ()
        let start = fixture.S.Start
        Assert.Equal(21, List.length start.Observation.Rows)
        Assert.All(start.Observation.Rows, fun row -> Assert.Equal(80, row.Length))
        Assert.Equal(Command, start.Pending)
        Assert.NotEmpty start.Observation.Legend
        Assert.False start.Over

    // Every entity at spawn is in the hero's current sight -- nothing is
    // remembered-but-unseen on turn one. The seeded start places a non-hero
    // entity in view, so InView flows through the real cansee() query, not just
    // the hero's hardcoded true. (The hero starts petless -- see pettype:none in
    // Native.prepareEnvironment -- so this leans on the seeded level, not a pet.)
    [<SkippableFact>]
    member _.``spawn entities are all in view``() =
        skipUnlessNative ()
        let start = fixture.S.Start
        Assert.True(
            start.Observation.Entities |> List.exists (fun e -> e.Kind <> HeroSelf),
            "the seeded start has a non-hero entity to exercise cansee")
        Assert.All(start.Observation.Entities, fun e -> Assert.True e.InView)

    // Walls render as box-drawing (the glyph->char remap), so '+' stays reserved
    // for a closed door and never masquerades as a room corner.
    [<SkippableFact>]
    member _.``walls render as box-drawing, never as '+'``() =
        skipUnlessNative ()
        let legend = fixture.S.Start.Observation.Legend
        let boxWallChars = set [ "│"; "─"; "┌"; "┐"; "└"; "┘"; "┼"; "┴"; "┬"; "┤"; "├" ]
        Assert.True(
            legend |> Map.exists (fun sym name -> boxWallChars.Contains sym && name = "wall"),
            "a box char should legend as \"wall\"")
        Assert.False(
            legend |> Map.exists (fun sym name -> sym = "+" && name.Contains "wall"),
            "'+' must never be a wall/corner")

    // GameId (NetHack's ubirthday) is set once the game runs and stays fixed.
    [<SkippableFact>]
    member _.``GameId is set and stable across steps``() =
        skipUnlessNative ()
        let s = fixture.S
        Assert.NotEqual(0L, s.Start.GameId)
        Assert.Equal(s.Start.GameId, s.Inv.GameId)

    // Observation.Inventory carries the hero's pack, and its letters match the
    // inventory menu -- same pack, two views of it.
    [<SkippableFact>]
    member _.``inventory is reported and matches the inventory menu``() =
        skipUnlessNative ()
        let s = fixture.S
        Assert.NotEmpty s.Start.Observation.Inventory
        Assert.All(
            s.Start.Observation.Inventory,
            fun it -> Assert.True(it.Letter <> '\000' && it.Text <> ""))
        match s.Inv.Pending with
        | Menu (_, _, items) ->
            Assert.NotEmpty items
            Assert.Equal<char list>(
                items |> List.map (fun it -> it.Key),
                s.Start.Observation.Inventory |> List.map (fun it -> it.Letter))
        | other -> failwith $"expected an inventory Menu, got {other}"

    // Cancel backs out of a getobj prompt whose only exit is ESC.
    [<SkippableFact>]
    member _.``Cancel aborts a getobj prompt``() =
        skipUnlessNative ()
        let s = fixture.S
        match s.EatPrompt.Pending with
        | MultiChoice _ -> ()
        | other -> failwith $"expected an eat MultiChoice, got {other}"
        Assert.Equal(Command, s.AfterCancel.Pending)

    // Objects on the hero's own tile are hidden by the '@' glyph in Rows, but must
    // still appear in Entities at the hero's position.
    [<SkippableFact>]
    member _.``object under the hero is reported as an entity``() =
        skipUnlessNative ()
        let dropped = fixture.S.Dropped
        let onHero =
            dropped.Observation.Entities
            |> List.filter (fun e -> e.Pos = dropped.Observation.Hero)
        Assert.True(
            onHero |> List.exists (fun e ->
                e.Kind = GlyphKind.Object && (defaultArg e.Name "").Contains "chest"),
            "a chest under the hero should be reported in Entities")

    // "Look inside" writes its listing via putstr into a menu window (not add_menu
    // rows), so it never arrives as a Prompt.Menu; the lines must still reach
    // Observation.Messages, else the caller learns nothing about the contents.
    [<SkippableFact>]
    member _.``looking inside a container reports its contents``() =
        skipUnlessNative ()
        let s = fixture.S
        match s.LootMenu.Pending with
        | Menu (_, _, its) ->
            Assert.True(its |> List.exists (fun it -> it.Key = ':'), "loot menu should offer ':'")
        | other -> failwith $"expected the loot menu, got {other}"
        Assert.True(
            s.Looked.Observation.Messages |> List.exists (fun m -> m.Contains "Contents of"),
            $"looking inside must report contents, but Messages were: %A{s.Looked.Observation.Messages}")
        Assert.Equal(Command, s.AfterLoot.Pending)

    // An invalid direction key must be reported (the cmdassist help window),
    // not aborted in silence.
    [<SkippableFact>]
    member _.``invalid direction key is reported``() =
        skipUnlessNative ()
        let msgs = fixture.S.BadDir.Observation.Messages
        Assert.True(
            msgs |> List.exists (fun m -> m.Contains "Invalid direction key"),
            $"an invalid direction key must be reported, but Messages were: %A{msgs}")
        Assert.True(
            msgs |> List.exists (fun m -> m.Contains "Valid direction keys are"),
            "the direction-key help grid should reach the caller too")

    // Picking up from a two-item pile must open a populated menu; the vacated
    // square is then reported as a pile (top item named, Pile = true).
    [<SkippableFact>]
    member _.``pickup menu is populated and the pile flag is set``() =
        skipUnlessNative ()
        let s = fixture.S
        match s.PickMenu.Pending with
        | Menu (_, _, its) ->
            Assert.True(List.length its >= 2, "pickup menu should list both pile items")
        | other -> failwith $"expected a 'Pick up what?' Menu, got {other}"
        Assert.NotEqual(s.PileCell, s.Stepped.Observation.Hero)  // actually stepped off
        Assert.True(
            s.Stepped.Observation.Entities
            |> List.exists (fun e -> e.Pos = s.PileCell && e.Kind = GlyphKind.Object && e.Pile),
            "the vacated two-item square should be reported as a pile (Pile = true)")

    // RepeatKey types a count prefix so one command rests several turns.
    [<SkippableFact>]
    member _.``RepeatKey rests several turns in one command``() =
        skipUnlessNative ()
        let s = fixture.S
        Assert.Equal(Command, s.Rested.Pending)
        Assert.True(
            s.Rested.Observation.Status.Turns - s.BeforeRest >= 2L,
            $"RepeatKey(10,'s') should rest several turns, but Turns went \
              {s.BeforeRest} -> {s.Rested.Observation.Status.Turns}")

    // Run feeds "G" + a direction; the extra key must not derail the input stream.
    [<SkippableFact>]
    member _.``Run stays a well-formed Command state``() =
        skipUnlessNative ()
        let ran = fixture.S.Ran
        Assert.Equal(Command, ran.Pending)
        Assert.Equal(21, List.length ran.Observation.Rows)

    // An overlong getlin reply must be truncated, not overrun the game thread's
    // fixed buffer; the game must survive it intact and keep stepping.
    [<SkippableFact>]
    member _.``overlong getlin reply does not corrupt the game``() =
        skipUnlessNative ()
        let s = fixture.S
        Assert.False s.AfterOverlong.Over
        Assert.Equal(21, List.length s.AfterOverlong.Observation.Rows)
        Assert.All(s.AfterOverlong.Observation.Rows, fun row -> Assert.Equal(80, row.Length))
        Assert.False s.AfterOverlong2.Over

    // The getpos/nh_poskey path (farlook) must survive its coordxy* out-param write.
    [<SkippableFact>]
    member _.``farlook getpos path survives``() =
        skipUnlessNative ()
        let f = fixture.S.AfterFarlook
        Assert.False f.Over
        Assert.Equal(21, List.length f.Observation.Rows)
        Assert.All(f.Observation.Rows, fun row -> Assert.Equal(80, row.Length))

    // A remembered-unseen-monster 'I' must surface as an entity (Warning), not a
    // bare symbol on the map with nothing in the entity list to explain it. It is
    // out of the (blind) hero's sight, so InView is false.
    [<SkippableFact>]
    member _.``unseen-monster 'I' is surfaced as a Warning entity``() =
        skipUnlessNative ()
        let ents = fixture.S.Unseen.Observation.Entities
        let warning = ents |> List.filter (fun e -> e.Kind = Warning)
        Assert.True(
            not (List.isEmpty warning),
            $"the 'I' should be a Warning entity, but entities were: %A{ents}")
        Assert.All(warning, fun e ->
            Assert.False e.InView
            Assert.Contains("unseen", (defaultArg e.Name "")))
