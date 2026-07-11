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
/// this starts a single seeded game and asserts determinism, structural
/// invariants, and a couple of modal prompts along the way.
[<SkippableFact>]
let ``seeded native game is deterministic and well-formed`` () =
    Skip.IfNot(nativeAvailable, "NetHackNative.dll (Release|x64) not built")

    let engine = Native.create ()
    let start = engine.Start { NewGame.defaults with Name = Some "Ada"; Seed = Some seed }

    // Determinism: seed 1234 always rolls this character (proves the seed took).
    Assert.Equal("Valkyrie", start.Observation.Character.Role)
    Assert.Equal("human", start.Observation.Character.Race)
    Assert.Equal("female", start.Observation.Character.Gender)

    // Structural invariants that hold on any level.
    Assert.Equal(21, List.length start.Observation.Rows)
    Assert.All(start.Observation.Rows, fun row -> Assert.Equal(80, row.Length))
    Assert.Equal(Command, start.Pending)
    Assert.NotEmpty start.Observation.Messages
    Assert.False start.Over

    // The legend classified real terrain from the map's glyphs.
    Assert.NotEmpty start.Observation.Legend

    // Opening the inventory yields a menu (exercises the select-menu path).
    let inv = engine.Step start (Key 'i')
    match inv.Pending with
    | Menu (_, _, items) -> Assert.NotEmpty items
    | other -> failwith $"expected an inventory Menu, got {other}"
