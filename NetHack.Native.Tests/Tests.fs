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

    // Opening the inventory yields a menu (exercises the select-menu path).
    let inv = engine.Step start (Key 'i')
    match inv.Pending with
    | Menu (_, _, items) -> Assert.NotEmpty items
    | other -> failwith $"expected an inventory Menu, got {other}"
    let afterInv = engine.Step inv Proceed

    // Wish a chest, find its inventory letter, and drop it so the hero is
    // standing on it.
    let wished = engine.Step (engine.Step afterInv (Extended "wizwish")) (Text "chest")
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
