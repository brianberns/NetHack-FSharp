# NetHack-FSharp

## LLM plays NetHack

This repository allows a chatbot agent to play <a href="https://www.nethack.org/">NetHack</a>, a classic text-based dungeon exploration game, and provides a web app for humans to watch the action with popcorn buckets in hand. What becomes quickly obvious in this experiment is that while chatbots do well with text, of course, they really struggle with map reading and spatial awareness.

Also, because NetHack can go on for thousands of turns, it's not practical to feed the entire game history to the agent as context. Instead, the agent manages its own working memory in the form of notes that it can create and delete. This allows the agent to remember the past and plan ahead, at least to some extent. The limitations of this approach quickly become obvious as well.

This app was developed in tandem with Claude Code. I wrote the fun parts, integrating the agent with NetHack, while Claude handled the plumbing needed to expose a NetHack API, similar to the <a href="https://github.com/facebookresearch/nle">NetHack Learning Environment</a>. Claude also wrote the web app's UI, and did a bang-up job IMHO.

Claude's description of the project follows below.

## API

An F# API that lets callers play **NetHack 5.0** as a pure-looking function

```fsharp
GameState -> Action -> GameState
```

plus a console app and an LLM agent that play through it. Under the hood, real
NetHack runs headless behind its "shim" window port, driven from .NET via
P/Invoke.

## Repository layout

| Path | What it is |
|------|------------|
| `Core/` | NetHack 5.0 source, as a git **submodule** → [`brianberns/NetHack`](https://github.com/brianberns/NetHack) branch `libnh-shim`. Adds a `NetHackNative` DLL build (the shim window port exposed as a callable library: `nhmain`, `nhglue_set_handler`, glyph/menu helpers) on top of upstream NetHack. |
| `NetHack.Api/` | The API library: domain types (`GameState`, `Observation`, `Action`, `Prompt`, …), JSON, an in-process `Stub` engine, and the real `Native` engine (P/Invoke into `NetHackNative.dll`). |
| `native/` | C glue compiled into the DLL — `nhglue.c` (variadic→fixed callback trampoline), `nhglue_ext.c` (glyph decode + menu building), and `nethack_exports.def`. |
| `NetHack.Cli/` | Interactive and scripted console over the API. |
| `NetHack.Agent/` | An LLM (Microsoft.Extensions.AI + OpenAI) plays via the API. |
| `NetHack.Tests/` | xUnit tests (against the `Stub` engine). |

## Prerequisites

- **Windows x64.**
- **.NET 10 SDK** (`dotnet --version` ≥ 10).
- **Visual Studio 2022+** (or Build Tools for Visual Studio) with the
  **Desktop development with C++** workload — MSVC x64 toolset + a Windows SDK.
  Required to build the native `NetHackNative.dll`.
- Internet access once, to fetch NetHack's third-party prerequisites (Lua and
  PDCursesMod), which upstream does not vendor.

## Clone

```sh
git clone --recurse-submodules https://github.com/brianberns/NetHack-FSharp.git
cd NetHack-FSharp
```

If you already cloned without submodules:

```sh
git submodule update --init
```

## Build

### 1. Fetch NetHack's 3rd-party prerequisites (once)

From the `Core` submodule (these land in `Core/lib/` and are not tracked by git):

```bat
cd Core
sys\windows\fetch.cmd lua
sys\windows\fetch.cmd pdcursesmod
cd ..
```

> **If `fetch.cmd pdcursesmod` reports `tar: This does not look like a tar
> archive`:** the zip downloaded fine, but `fetch.cmd` shells out to `tar`, and
> if a GNU `tar` (e.g. from Git for Windows) is first on `PATH` it can't unpack a
> zip. Run the command from a plain `cmd.exe` (so Windows' bundled bsdtar wins),
> or extract `Core\lib\pdcursesmod.zip` yourself so that `curses.h` lands
> directly in `Core\lib\pdcursesmod\`. For example, in PowerShell:
>
> ```powershell
> Expand-Archive Core\lib\pdcursesmod.zip -DestinationPath $env:TEMP\pdc -Force
> Remove-Item -Recurse -Force Core\lib\pdcursesmod
> Move-Item (Get-ChildItem $env:TEMP\pdc)[0].FullName Core\lib\pdcursesmod
> ```

### 2. Build the native NetHack library (Release | x64)

`NetHackNative.vcxproj` depends on static libs and generated headers produced by
the stock NetHack solution, so build the solution **first**, then the DLL.

Find MSBuild (or open a *Developer Command Prompt for VS*, where `msbuild` is on
`PATH`):

```bat
for /f "usebackq delims=" %M in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find "MSBuild\**\Bin\MSBuild.exe"`) do set MSBUILD=%M
```

Then:

```bat
"%MSBUILD%" Core\sys\windows\vs\NetHack.sln -m -p:Configuration=Release -p:Platform=x64
"%MSBUILD%" Core\sys\windows\vs\NetHack\NetHackNative.vcxproj -m -p:Configuration=Release -p:Platform=x64
```

This produces `Core\binary\Release\x64\NetHackNative.dll` along with the data
files the engine needs at runtime (`nhdat500`, `*.template`, `record`, …).

> Alternatively: open `Core\sys\windows\vs\NetHack.sln` in Visual Studio, build
> the solution (Release | x64), then build the **NetHack/NetHackNative** project.

### 3. Build the F# solution

```sh
dotnet build NetHack-FSharp.slnx
```

The `Native` engine locates the DLL by walking up from the running assembly to
`Core/binary/Release/x64`. Override with the `NETHACK_NATIVE_DIR` environment
variable or `NetHack.Api.Native.dataDirOverride` if it lives elsewhere.

## Run

### CLI

```sh
dotnet run --project NetHack.Cli          # real NetHack (default)
dotnet run --project NetHack.Cli -- --stub    # in-process fake engine, no DLL needed
dotnet run --project NetHack.Cli -- native-demo   # scripted, non-interactive
```

Interactive keys: `hjkl`/`yubn` move, `q` quaff, `s` search, `i` inventory,
`J` dump the current state as JSON, `Q` quit.

### Tests

```sh
dotnet test NetHack.Tests
```

### LLM agent

Credentials are read from user secrets (kept out of the repo):

```sh
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project NetHack.Agent
# optional:
#   OpenAI:Model         (default gpt-4o-mini)
#   OpenAI:BaseUrl       (OpenAI-compatible endpoints, e.g. GitHub Models)
#   OpenAI:StepDelayMs   (pace requests; raise on rate-limited tiers)
#   OpenAI:TimeoutSeconds (per-call hard timeout)
dotnet run --project NetHack.Agent -- 40   # 40 = step budget
```

The agent sends the `GameState` as JSON each turn and gets back a structured
action; it keeps a short self-authored "notes" scratchpad as its only memory,
so each request stays small.

## Notes

- NetHack keeps all game state in C globals, so there is **one live game per
  process**; the API enforces this and a web service would scale across
  processes.
- After rebuilding `NetHackNative.dll`, just rerun — the F# apps stage a fresh
  copy of the DLL and data files next to their executable on startup.
- Build outputs (`Core/binary`, `Core/tools`, `bin`, `obj`, …) are gitignored;
  the DLL is produced by the build above, not stored in the repo.
