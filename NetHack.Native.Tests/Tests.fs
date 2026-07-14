module NetHack.Native.Tests

open System
open System.IO
open Xunit
open NetHack.Api

// One live NetHack game per process (state lives in C globals), so these tests
// must not run in parallel and the suite starts exactly one seeded game.
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

/// The whole native suite in one test: only one game can exist per process, so
/// this starts a single seeded wizard game and asserts determinism, structural
/// invariants, the select-menu path, and that objects on the hero's own tile
/// (hidden by the '@' glyph on the map) are still reported as entities.
[<SkippableFact>]
let ``seeded wizard game is deterministic, well-formed, and reports objects under the hero`` () =
    Skip.IfNot(nativeAvailable, "NetHackNative.dll (Release|x64) not built")

    let engine = Native.create ()
    // Wizard mode lets us wish a known object into being; seed fixes the level.
    let start =
        engine.Start { NewGame.defaults with Name = Some "Tester"; Seed = Some seed; Wizard = true }

    // Determinism: seed 1234 always rolls this character (proves the seed took).
    Assert.Equal("Valkyrie", start.Observation.Character.Role)
    Assert.Equal("human", start.Observation.Character.Race)
    Assert.Equal("female", start.Observation.Character.Gender)

    // Structural invariants that hold on any level.
    Assert.Equal(21, List.length start.Observation.Rows)
    Assert.All(start.Observation.Rows, fun row -> Assert.Equal(80, row.Length))
    Assert.Equal(Command, start.Pending)
    Assert.NotEmpty start.Observation.Legend
    Assert.False start.Over

    // Every entity at spawn sits in the hero's current sight — nothing is
    // remembered-but-unseen on the first turn. There is at least one non-hero
    // entity (the Valkyrie's starting pet), so this flag flows through the real
    // cansee() query, not just the hero's hardcoded value. The InView=false
    // (remembered) path is reached in live play once a square is left behind.
    Assert.True(
        start.Observation.Entities |> List.exists (fun e -> e.Kind <> HeroSelf),
        "the seeded start has a non-hero entity (the pet) to exercise cansee")
    Assert.All(start.Observation.Entities, fun e -> Assert.True e.InView)

    // Walls render as box-drawing (the glyph->char remap), so '+' is reserved
    // for a closed door and never masquerades as a room corner. Every room has
    // walls, so the legend maps at least one box char to "wall" and maps '+' to
    // nothing wall-like.
    let boxWallChars = set [ "│"; "─"; "┌"; "┐"; "└"; "┘"; "┼"; "┴"; "┬"; "┤"; "├" ]
    Assert.True(
        start.Observation.Legend
        |> Map.exists (fun sym name -> boxWallChars.Contains sym && name = "wall"),
        "walls should render as box-drawing (a box char legends as \"wall\")")
    Assert.False(
        start.Observation.Legend
        |> Map.exists (fun sym name -> sym = "+" && name.Contains "wall"),
        "'+' must never be a wall/corner (it is reserved for closed doors)")

    // GameId (NetHack's ubirthday) is set once the game is running and stays
    // fixed for the life of the game.
    Assert.NotEqual(0L, start.GameId)

    // Observation.Inventory carries the hero's pack (letter + description); a
    // starting character always has some equipment, and every item is lettered.
    Assert.NotEmpty start.Observation.Inventory
    Assert.All(
        start.Observation.Inventory,
        fun it -> Assert.True(it.Letter <> '\000' && it.Text <> ""))

    // Opening the inventory yields a menu (exercises the select-menu path). Its
    // letters must match Observation.Inventory — same pack, two views of it.
    let inv = engine.Step start (Key 'i')
    match inv.Pending with
    | Menu (_, _, items) ->
        Assert.NotEmpty items
        Assert.Equal<char list>(
            items |> List.map (fun it -> it.Key),
            start.Observation.Inventory |> List.map (fun it -> it.Letter))
    | other -> failwith $"expected an inventory Menu, got {other}"
    Assert.Equal(start.GameId, inv.GameId)   // stable across steps
    let afterInv = engine.Step inv Proceed

    // Cancel backs out of a getobj prompt whose only exit is ESC (no listed
    // quit char): 'e'at opens "What do you want to eat?", Cancel aborts it.
    let eatPrompt = engine.Step afterInv (Key 'e')
    match eatPrompt.Pending with
    | MultiChoice _ -> ()
    | other -> failwith $"expected an eat MultiChoice, got {other}"
    let afterCancel = engine.Step eatPrompt Cancel
    Assert.Equal(Command, afterCancel.Pending)

    // Wish a chest, find its inventory letter, and drop it so the hero is
    // standing on it.
    // "unlocked" so #loot can open it below without a lock-picking detour.
    let wished = engine.Step (engine.Step afterCancel (Extended "wizwish")) (Text "unlocked chest")
    let inv2 = engine.Step wished (Key 'i')
    let chestKey =
        match inv2.Pending with
        | Menu (_, _, its) ->
            its |> List.tryPick (fun it ->
                if it.Text.Contains "chest" then Some it.Key else None)
        | _ -> None
    Assert.True(chestKey.IsSome, "wished chest should be in inventory")
    let dropped =
        let afterInv2 = engine.Step inv2 Proceed
        let dropPrompt = engine.Step afterInv2 (Key 'd')  // "What do you want to drop?"
        engine.Step dropPrompt (Answer chestKey.Value)

    // The chest is under the hero: the '@' glyph hides it in Rows, but it must
    // still appear in Entities at the hero's own position.
    let onHero =
        dropped.Observation.Entities
        |> List.filter (fun e -> e.Pos = dropped.Observation.Hero)
    Assert.True(
        onHero |> List.exists (fun e ->
            e.Kind = GlyphKind.Object && (defaultArg e.Name "").Contains "chest"),
        "a chest under the hero should be reported in Entities")

    // "Look inside" a container. NetHack renders that listing as plain text lines
    // written into a menu window (container_contents), NOT as selectable add_menu
    // rows, so it never arrives as a Prompt.Menu. Those lines must still reach
    // Observation.Messages, else the engine computes the contents and we silently
    // drop them (the caller asks what's in the chest and learns nothing).
    let afterLoot =
        let lootStart = engine.Step dropped (Extended "loot")
        // Standing on the chest: "There is a chest here, loot it? [ynq]" comes first.
        let lootMenu =
            match lootStart.Pending with
            | MultiChoice _ -> engine.Step lootStart (Answer 'y')
            | _ -> lootStart
        match lootMenu.Pending with
        | Menu (_, _, its) ->
            Assert.True(
                its |> List.exists (fun it -> it.Key = ':'),
                "the loot menu should offer ':' (look inside)")
            let looked = engine.Step lootMenu (Choose [ ':' ])
            Assert.True(
                looked.Observation.Messages
                |> List.exists (fun m -> m.Contains "Contents of"),
                $"looking inside must report the contents, but Messages were: \
                  %A{looked.Observation.Messages}")
            // Leave the loot menu so the game is back at a command prompt.
            match looked.Pending with
            | Menu _ -> engine.Step looked (Choose [ 'q' ])
            | _ -> looked
        | other -> failwith $"expected the 'Do what with the chest?' menu, got {other}"
    Assert.Equal(Command, afterLoot.Pending)

    // An invalid direction key must not fail silently. getdir() with cmdassist on
    // (NetHack's default) reports "Invalid direction key!" plus the direction grid
    // via help_dir/show_direction_keys -- putstr into a *text window* -- and only
    // falls back to pline("What a strange direction!") when that window is NOT
    // shown (cmd.c:4096). So the pline never fires, and before we captured menu/
    // text putstr the caller saw nothing at all: the command aborted in silence.
    let badDir =
        let dirPrompt = engine.Step afterLoot (Extended "kick")
        match dirPrompt.Pending with
        | Direction _ -> engine.Step dirPrompt (Key 'w')  // 'w' is not a direction key
        | other -> failwith $"expected a Direction prompt from #kick, got {other}"
    // The reply is the cmdassist help window: "Invalid direction key!" followed by
    // the y/k/u h/./l b/j/n grid -- which also spells out for the caller that 'n'
    // is SOUTHEAST, not north, and that '.' targets yourself.
    Assert.True(
        badDir.Observation.Messages
        |> List.exists (fun m -> m.Contains "Invalid direction key"),
        $"an invalid direction key must be reported, but Messages were: \
          %A{badDir.Observation.Messages}")
    Assert.True(
        badDir.Observation.Messages
        |> List.exists (fun m -> m.Contains "Valid direction keys are"),
        "the direction-key help grid should reach the caller too")

    // Pile flag: wish a second item and drop it, so the hero's tile now holds
    // two stacks; then step off and confirm the vacated square is reported as a
    // pile (top item named, Pile = true, rest hidden — fog-of-war-safe).
    let wished2 = engine.Step (engine.Step badDir (Extended "wizwish")) (Text "ring of protection")
    let inv3 = engine.Step wished2 (Key 'i')
    let ringKey =
        match inv3.Pending with
        | Menu (_, _, its) ->
            its |> List.tryPick (fun it -> if it.Text.Contains "ring" then Some it.Key else None)
        | _ -> None
    Assert.True(ringKey.IsSome, "wished ring should be in inventory")
    let twoStacks =
        let afterInv3 = engine.Step inv3 Proceed
        engine.Step (engine.Step afterInv3 (Key 'd')) (Answer ringKey.Value)

    // Picking up from this two-item pile must open a populated selection menu.
    // Pickup rows carry no preset accelerator (ch=0) and were previously
    // dropped, so the menu came back empty and was silently auto-cancelled —
    // nothing picked up, no message. Open it, assert both items are offered,
    // then cancel without taking anything so the pile survives for the checks
    // below.
    let twoStacks =
        let pickMenu = engine.Step twoStacks (Key ',')
        match pickMenu.Pending with
        | Menu (_, _, its) ->
            Assert.True(List.length its >= 2, "pickup menu should list both pile items")
        | other -> failwith $"expected a 'Pick up what?' Menu, got {other}"
        engine.Step pickMenu Proceed          // cancel the menu; pile untouched
    let pileCell = twoStacks.Observation.Hero
    let mutable stepped = twoStacks
    for dir in [ Move East; Move West; Move South; Move North ] do
        if stepped.Observation.Hero = pileCell then stepped <- engine.Step stepped dir
    Assert.NotEqual(pileCell, stepped.Observation.Hero)   // actually stepped off
    Assert.True(
        stepped.Observation.Entities
        |> List.exists (fun e -> e.Pos = pileCell && e.Kind = GlyphKind.Object && e.Pile),
        "the vacated two-item square should be reported as a pile (Pile = true)")

    // Multi-key commands are fed as a single Step. RepeatKey types a count
    // prefix ("10s") so one call rests many turns: the turn counter must jump by
    // more than the single turn a lone 's' would cost, proving the prefix took.
    let beforeRest = stepped.Observation.Status.Turns
    let rested = engine.Step stepped (RepeatKey(10, 's'))
    Assert.Equal(Command, rested.Pending)
    Assert.True(
        rested.Observation.Status.Turns - beforeRest >= 2L,
        $"RepeatKey(10,'s') should rest several turns in one command, but Turns \
          went {beforeRest} -> {rested.Observation.Status.Turns}")

    // Run feeds "G" + a direction key; the exact distance depends on the map,
    // but it must remain a well-formed Command state (the extra keys don't
    // derail the input stream).
    let ran = engine.Step rested (Run West)
    Assert.Equal(Command, ran.Pending)
    Assert.Equal(21, List.length ran.Observation.Rows)
