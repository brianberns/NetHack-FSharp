namespace NetHack.Api

open System
open System.IO
open System.Reflection
open System.Threading
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Text.RegularExpressions

/// The real engine: drives the NetHackNative DLL. NetHack runs its own
/// moveloop on a background thread and pulls input through the shim callbacks;
/// this module turns that inversion of control into GameState -> Action ->
/// GameState by parking the game thread on each input request and handing an
/// Observation back to the caller.
///
/// NetHack keeps all game state in C globals, so there can be only ONE live
/// session per process. `create` enforces that.
module Native =

    // ---- interop -------------------------------------------------------

    [<Literal>]
    let private Dll = "NetHackNative.dll"

    // void handler(const char *name, const char *fmt, void *ret_ptr,
    //              unsigned long long *args, int nargs)
    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private Handler =
        delegate of nativeint * nativeint * nativeint * nativeint * int -> unit

    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern void private nhglue_set_handler(Handler handler)

    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern int private nhmain(int argc,
                              [<MarshalAs(UnmanagedType.LPArray,
                                          ArraySubType = UnmanagedType.LPStr)>] string[] argv)

    // Decode the remembered map cell (x,y) into a category (0 skip, 1 monster,
    // 2 pet, 3 object, 4 trap) and a name; safe only between turns.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern int private nhglue_describe_at(int x, int y, System.Text.StringBuilder buf, int buflen)

    // Concise terrain name for the known background glyph at a cell (else 0).
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern int private nhglue_feature_at(int x, int y, System.Text.StringBuilder buf, int buflen)

    // Custom disambiguating char (a Unicode code point) for a glyph that NetHack
    // would otherwise draw ambiguously (doorway vs floor, tree vs corridor, box-
    // drawing walls, ...), or 0 to keep the engine's normal ttychar.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int private nhglue_map_char(int glyph)

    // Name + drawn char of the index-th floor object at a cell (0 when none),
    // read from the object chain so objects hidden under the hero are reported.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern int private nhglue_floor_object_at(int x, int y, int index, System.Text.StringBuilder buf, int buflen)

    // 1 when the displayed glyph at a cell marks a pile (>1 object stack), else 0.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int private nhglue_is_pile_at(int x, int y)

    // Report the hero's role / race / gender into three buffers.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern void private nhglue_hero_ident(
        System.Text.StringBuilder role, int rolelen,
        System.Text.StringBuilder race, int racelen,
        System.Text.StringBuilder gender, int genderlen)

    // program_state.input_state at the moment of the call (getdirInp == 3, etc.).
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int private nhglue_input_state()

    // NetHack's `ubirthday` (game creation time): a per-game id, fixed for the
    // life of a game.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int64 private nhglue_game_id()

    // Index of an extended command (e.g. "loot") in extcmdlist, or -1.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern int private nhglue_ext_cmd_index(string name)

    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int private nhglue_anything_size()

    // Build a menu_item[] (NetHack-allocated) from packed identifier bytes.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint private nhglue_build_menu(byte[] anythings, int[] counts, int count)

    // ---- locating the DLL / data (#5) ---------------------------------

    /// Explicit directory holding NetHackNative.dll + data files. When None
    /// (default), the directory is discovered: the NETHACK_NATIVE_DIR
    /// environment variable if set, otherwise a `Core/binary/Release/x64`
    /// found by walking up from the running assembly.
    let mutable dataDirOverride : string option = None

    let private hasDll (dir: string) =
        not (String.IsNullOrWhiteSpace dir) && File.Exists(Path.Combine(dir, Dll))

    let private discover () : string option =
        let env = Environment.GetEnvironmentVariable "NETHACK_NATIVE_DIR"
        if hasDll env then Some env
        else
            let start =
                let loc = Assembly.GetExecutingAssembly().Location
                if String.IsNullOrEmpty loc then AppContext.BaseDirectory
                else Path.GetDirectoryName loc
            let rec up (dir: DirectoryInfo) =
                if isNull dir then None
                else
                    let candidate = Path.Combine(dir.FullName, "Core", "binary", "Release", "x64")
                    if hasDll candidate then Some candidate else up dir.Parent
            up (DirectoryInfo start)

    let private dataDir () : string =
        match dataDirOverride with
        | Some d -> d
        | None ->
            match discover () with
            | Some d -> d
            | None ->
                failwith $"Could not locate {Dll}. Build the NetHackNative project, \
                           or set Native.dataDirOverride to its directory."

    let mutable private resolverInstalled = false

    let private installResolver () =
        if not resolverInstalled then
            resolverInstalled <- true
            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(),
                fun name _ _ ->
                    if name = Dll then NativeLibrary.Load(Path.Combine(dataDir (), Dll))
                    else IntPtr.Zero)

    // ---- window / status constants ------------------------------------

    let private NHW_MESSAGE = 1
    let private NHW_MAP = 3

    let private BL_GOLD = 10
    let private BL_HP = 18
    let private BL_HHUNGER = 17
    let private BL_CONDITION = 22

    // program_state.input_state value while getdir() is reading a direction
    // (hack.h: getdirInp). yn_function is overloaded — this distinguishes the
    // "In what direction?" prompt from a genuine yes/no.
    let private GetDirInp = 3

    let private COLNO = 80
    let private ROWNO = 21

    /// One step must settle (or end) within this budget, else it is reported as
    /// a fault rather than hanging the caller forever.
    [<Literal>]
    let private StepTimeoutMs = 30000

    let private colorName =
        [| "black"; "red"; "green"; "brown"; "blue"; "magenta"; "cyan"; "gray"
           "no-color"; "orange"; "bright-green"; "yellow"; "bright-blue"
           "bright-magenta"; "bright-cyan"; "white" |]

    /// Map a BL_CONDITION mask bit to our Condition, following botl.h ordering.
    let private conditionBits =
        [ 0x00000001, BareHanded; 0x00000002, Blind; 0x00000008, Confused
          0x00000010, Deaf; 0x00000080, FoodPoisoning; 0x00000400, Hallucinating
          0x00000800, Held; 0x20000000, Holding; 0x00004000, Levitating
          0x00000040, Flying; 0x00008000, Paralyzed; 0x00010000, Riding
          0x00020000, Sleeping; 0x00040000, Slime; 0x00100000, Stone
          0x00200000, Strangled; 0x00400000, Stunned; 0x04000000, Trapped
          0x08000000, Unconscious ]

    // ---- decode helpers -----------------------------------------------

    let private argAt (args: nativeint) (i: int) : uint64 =
        uint64 (Marshal.ReadInt64(args, i * 8))

    let private strAt (args: nativeint) (i: int) : string =
        let p = nativeint (argAt args i)
        if p = IntPtr.Zero then ""
        else (Marshal.PtrToStringAnsi p |> Option.ofObj |> Option.defaultValue "")

    let private hunger (s: string) : Hunger =
        match s.Trim() with
        | "Satiated" -> Satiated
        | "Hungry" -> Hungry
        | "Weak" -> Weak
        | "Fainting" -> Fainting
        | "Fainted" -> Fainted
        | "Starved" -> Starved
        | _ -> NotHungry

    let private digits (s: string) : int64 =
        let d = String(s |> Seq.filter Char.IsDigit |> Seq.toArray)
        match Int64.TryParse d with true, v -> v | _ -> 0L

    let private tryInt (s: string) =
        match Int32.TryParse(s.Trim()) with true, v -> v | _ -> 0

    /// Strip NetHack "glyph in string" escapes (\G<hex>, from encglyph) that the
    /// shim port does not render — e.g. the gold field arrives as "\G...:123".
    let private glyphEscape = Regex(@"\\G[0-9A-Fa-f]+", RegexOptions.Compiled)
    let private clean (s: string) = glyphEscape.Replace(s, "")

    let private emptyStatus : Status =
        { Title = ""; Alignment = ""; Strength = ""; Dexterity = 0; Constitution = 0
          Intelligence = 0; Wisdom = 0; Charisma = 0; HP = 0; HPMax = 0; Power = 0
          PowerMax = 0; ArmorClass = 0; ExpLevel = 0; Experience = None; Gold = 0L
          Dungeon = ""; DungeonLevel = 0; Depth = 0; Hunger = NotHungry
          Encumbrance = None; Conditions = []; Turns = 0L; Score = None }

    let private emptyCharacter : Character =
        { Role = ""; Race = ""; Gender = "" }

    let private emptyObservation : Observation =
        { Width = COLNO; Height = ROWNO
          Rows = [ for _ in 1 .. ROWNO -> String(' ', COLNO) ]
          Legend = Map.empty
          Hero = { X = 0; Y = 0 }; Character = emptyCharacter
          Entities = []; Status = emptyStatus; Messages = [] }

    // ---- environment & playground (#6) --------------------------------

    let private dataFiles =
        [ "sysconf.template"; "symbols.template"; "nethackrc.template"
          "nhdat500"; "record"; "opthelp"; "license"; "nethack.txt"
          "Guidebook.txt"; Dll ]

    /// NetHack derives its data directory from the host exe's folder, so stage
    /// the data files next to it (only when newer) and run from there.
    let private prepareEnvironment () =
        let baseDir = AppContext.BaseDirectory
        let src0 = dataDir ()
        for f in dataFiles do
            let dst = Path.Combine(baseDir, f)
            let src = Path.Combine(src0, f)
            let stale =
                not (File.Exists dst)
                || File.GetLastWriteTimeUtc src > File.GetLastWriteTimeUtc dst
            if File.Exists src && stale then
                try File.Copy(src, dst, true) with _ -> ()
        // Sandbox the configuration: write our own rc and point NetHack at it via
        // NETHACKOPTIONS=@<file>. rcfile() treats a leading '@' as a config file
        // name and reads THAT instead of the user's ~/.nethackrc, so behaviour is
        // deterministic and never inherits personal settings (autopickup, fruit,
        // symset, tiles, ...). vi-keys (number_pad:0) are what the API assumes.
        // 'time' puts the turn counter on the status line; without it Status.Turns
        // is never reported (defaults off). 'hilite_pile' marks squares holding
        // more than one object (a pile) — NetHack always tracks this internally
        // but only surfaces it to a player with this opt-in option; enabling it
        // makes reporting a pile flag fair. 'mention_decor' announces furniture
        // (altar, fountain, stairs, ...) as the hero steps onto it; without it the
        // step-on is silent and, since the hero's glyph hides the feature and
        // terrain is not reported as an entity, the caller has no way to tell it
        // is standing on, e.g., an altar. 'mention_walls' announces blocked moves
        // ("You can't move diagonally into an intact doorway.", "It's solid
        // stone.", ...); without it a move that fails is completely silent (no
        // message, no turn spent), so the caller can't tell an action had no
        // effect. All are standard opt-in options a human can enable, so they
        // surface only fair, UI-available information.
        // No symset is set on purpose: nhglue_map_char re-renders the map from the
        // glyph itself (box-drawing walls, disambiguated terrain), so the compiled
        // default ASCII symset is fine — and we avoid symset:plain, whose only
        // effect is to draw wall corners as '+', colliding with closed doors.
        let baseOpts =
            "OPTIONS=time,hilite_pile,mention_decor,mention_walls\nOPTIONS=number_pad:0\n"
        File.WriteAllText(Path.Combine(baseDir, "sandbox.nethackrc"), baseOpts)
        // Wizard games (integration tests) additionally start with no pet, so
        // scripted scenarios are deterministic — a pet follows the hero and swaps
        // onto the very tiles under test. Normal play keeps its pet.
        File.WriteAllText(Path.Combine(baseDir, "sandbox-wizard.nethackrc"),
            baseOpts + "OPTIONS=pettype:none\n")
        Environment.SetEnvironmentVariable(
            "NETHACKOPTIONS", "@" + Path.Combine(baseDir, "sandbox.nethackrc"))
        Directory.SetCurrentDirectory(baseDir)

    /// Remove ONLY this player's leftover level files (e.g. "Ada.0") from the
    /// NetHack playground so getlock() doesn't prompt to recover an interrupted
    /// run. Deliberately narrow: never touches save files or other players'
    /// files, so it cannot destroy an unrelated game.
    let private cleanPlayground (playerName: string) =
        let enc =
            String(playerName |> Seq.filter Char.IsLetterOrDigit |> Seq.toArray)
        if enc <> "" then
            let dirs =
                [ Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData
                  Environment.GetFolderPath Environment.SpecialFolder.UserProfile ]
                |> List.map (fun d -> Path.Combine(d, "NetHack", "5.0"))
            // level files are "<name>.<digits>"; leave the bare "<name>" (a save).
            let levelFile = Regex($@"^{Regex.Escape enc}\.\d+$", RegexOptions.IgnoreCase)
            for d in dirs do
                if Directory.Exists d then
                    for f in Directory.GetFiles d do
                        if levelFile.IsMatch(Path.GetFileName f) then
                            try File.Delete f with _ -> ()

    // ---- the session --------------------------------------------------

    /// What the game thread hands back to the API thread at each rendezvous.
    type private Signal =
        | Settled of Observation * Prompt
        | Ended
        | Faulted of exn

    /// Everything the callback thread and the API thread share. One instance
    /// per process while a game is live.
    type private Session() =
        // A single-slot channel each way. The game thread publishes a Signal
        // when it parks on an input request (or ends/faults); the API thread
        // replies with the Action to feed that request. Bounded to 1 because
        // the two sides strictly alternate.
        let outbox = new BlockingCollection<Signal>(1)
        let inbox = new BlockingCollection<NetHack.Api.Action>(1)

        // screen buffers, mutated only on the game thread
        let glyphs = Array2D.create ROWNO COLNO ' '
        let cellColor = Array2D.create ROWNO COLNO "gray"
        let winType = System.Collections.Generic.Dictionary<int, int>()
        let statusText = System.Collections.Generic.Dictionary<int, string>()
        let messages = ResizeArray<string>()
        // menu being built: display items, their selector->identifier bytes, title
        let menuItems = ResizeArray<MenuItem>()
        let menuIdents = System.Collections.Generic.Dictionary<char, byte[]>()
        let mutable menuTitle = ""
        let mutable anythingSize = 0

        let mutable nextWin = 1
        let mutable condMask = 0
        let mutable heroX = 0
        let mutable heroY = 0
        // during startup we auto-answer pre-game prompts until the first command
        let mutable initializing = true
        let mutable over = false
        // NetHack's `ubirthday`, read once the game is running; a per-game id.
        let mutable gameId = 0L
        // set by KeyFor(Extended cmd); consumed by the next get_ext_cmd callback
        let mutable pendingExtCmd : string option = None
        // keystrokes still to feed for a single multi-character command (a count
        // prefix like "20s", or a run "G" + direction). Filled when a Command
        // action expands to several keys; drained by successive nhgetch requests
        // before the engine settles and asks the API thread for the next action.
        let pendingKeys = System.Collections.Generic.Queue<int>()
        // last published screen, so an Ended/Faulted state still carries a map
        let mutable lastObs = emptyObservation

        member val Handler : Handler = Unchecked.defaultof<Handler> with get, set
        member val Thread : Thread = null with get, set

        member private _.BuildStatus() : Status =
            let txt k = match statusText.TryGetValue k with true, v -> v | _ -> ""
            let conds =
                conditionBits
                |> List.choose (fun (bit, c) -> if condMask &&& bit <> 0 then Some c else None)
            { Title = (txt 0).Trim()
              Alignment = (txt 7).Trim()
              Strength = (txt 1).Trim()
              Dexterity = tryInt (txt 2); Constitution = tryInt (txt 3)
              Intelligence = tryInt (txt 4); Wisdom = tryInt (txt 5)
              Charisma = tryInt (txt 6)
              HP = tryInt (txt BL_HP); HPMax = tryInt (txt 19)
              Power = tryInt (txt 11); PowerMax = tryInt (txt 12)
              ArmorClass = tryInt (txt 14)
              ExpLevel = tryInt (txt 13); Experience = Some(digits (txt 21))
              Gold = digits (txt BL_GOLD)
              Dungeon = ""; DungeonLevel = int (digits (txt 20)); Depth = int (digits (txt 20))
              Hunger = hunger (txt BL_HHUNGER); Encumbrance = None
              Conditions = conds
              Turns = digits (txt 16); Score = Some(digits (txt 8)) }

        member private _.BuildCharacter() : Character =
            let role   = System.Text.StringBuilder(64)
            let race   = System.Text.StringBuilder(64)
            let gender = System.Text.StringBuilder(32)
            nhglue_hero_ident(role, role.Capacity, race, race.Capacity,
                              gender, gender.Capacity)
            { Role = role.ToString().Trim(); Race = race.ToString().Trim()
              Gender = gender.ToString().Trim() }

        /// Build the map legend: for each terrain symbol drawn this turn, the
        /// distinct NetHack feature name(s) it stands for (collisions joined with
        /// "or", e.g. "#" -> "corridor or tree"). Only cells whose terrain the
        /// hero actually knows are classified; unexplored blanks are left out.
        member private _.BuildLegend() : Map<string, string> =
            let acc =
                System.Collections.Generic.Dictionary<
                    char, System.Collections.Generic.SortedSet<string>>()
            let sb = System.Text.StringBuilder(48)
            for y in 0 .. ROWNO - 1 do
                for x in 0 .. COLNO - 1 do
                    sb.Clear() |> ignore
                    if nhglue_feature_at(x, y, sb, sb.Capacity) <> 0 then
                        let name = sb.ToString().Trim()
                        if name <> "" then
                            let ch = glyphs[y, x]
                            match acc.TryGetValue ch with
                            | true, set -> set.Add name |> ignore
                            | _ ->
                                let set = System.Collections.Generic.SortedSet<string>()
                                set.Add name |> ignore
                                acc[ch] <- set
            acc
            |> Seq.map (fun kv -> string kv.Key, String.concat " or " kv.Value)
            |> Map.ofSeq

        member private this.BuildObservation() : Observation =
            // Latch the game id once the game is running (ubirthday is set during
            // character creation, well before the first observation).
            if gameId = 0L then gameId <- nhglue_game_id ()
            let rows =
                [ for y in 0 .. ROWNO - 1 ->
                    String(Array.init COLNO (fun x -> glyphs[y, x])) ]
            let hero = { X = heroX; Y = heroY }
            let entities = ResizeArray<Entity>()
            entities.Add { Pos = hero; Symbol = '@'; Kind = HeroSelf
                           Name = Some "you"; Color = "white"; Pile = false }
            // Decode monsters / objects / traps at each drawn cell into named
            // entities (features/terrain stay in the ASCII map).
            let sb = System.Text.StringBuilder(96)
            for y in 0 .. ROWNO - 1 do
                for x in 0 .. COLNO - 1 do
                    if glyphs[y, x] <> ' ' && not (x = heroX && y = heroY) then
                        sb.Clear() |> ignore
                        match nhglue_describe_at(x, y, sb, sb.Capacity) with
                        | 0 -> ()
                        | cat ->
                            let kind =
                                match cat with
                                | 1 -> Monster | 2 -> Pet | 3 -> GlyphKind.Object
                                | 4 -> Trap | _ -> Unexplored
                            entities.Add
                                { Pos = { X = x; Y = y }; Symbol = glyphs[y, x]
                                  Kind = kind; Name = Some(sb.ToString())
                                  Color = cellColor[y, x]
                                  Pile = (nhglue_is_pile_at(x, y) <> 0) }
            // Objects on the hero's own tile are hidden by the '@' glyph (and skipped
            // above), so read them from the object chain and report them explicitly —
            // otherwise a caller can't tell it is standing on, e.g., a chest.
            let mutable oi = 0
            let mutable more = true
            while more do
                sb.Clear() |> ignore
                match nhglue_floor_object_at(heroX, heroY, oi, sb, sb.Capacity) with
                | 0 -> more <- false
                | ch ->
                    entities.Add
                        { Pos = hero; Symbol = char ch; Kind = GlyphKind.Object
                          Name = Some(sb.ToString()); Color = "gray"; Pile = false }
                    oi <- oi + 1
            { Width = COLNO; Height = ROWNO; Rows = rows; Hero = hero
              Legend = this.BuildLegend()
              Character = this.BuildCharacter()
              Entities = List.ofSeq entities; Status = this.BuildStatus()
              Messages = List.ofSeq messages }

        /// Called from the game thread when it needs input: publish the current
        /// screen + prompt, block until the API thread supplies an Action.
        member private this.Settle(prompt: Prompt) : NetHack.Api.Action =
            let obs = this.BuildObservation()
            lastObs <- obs
            messages.Clear()          // messages are per-step
            outbox.Add(Settled(obs, prompt))
            inbox.Take()

        /// The single callback for every window operation.
        member this.Dispatch(name: string, fmt: string, retPtr: nativeint, args: nativeint) =
            let writeInt (v: int) = if retPtr <> IntPtr.Zero then Marshal.WriteInt32(retPtr, v)
            let writeChar (c: char) = if retPtr <> IntPtr.Zero then Marshal.WriteByte(retPtr, byte c)
            match name with
            | "shim_create_nhwindow" ->
                let ty = int (argAt args 0)
                let id = nextWin
                nextWin <- nextWin + 1
                winType[id] <- ty
                writeInt id
            | "shim_clear_nhwindow" ->
                let w = int (argAt args 0)
                if (match winType.TryGetValue w with true, t -> t = NHW_MAP | _ -> false) then
                    Array2D.iteri (fun y x _ -> glyphs[y, x] <- ' ') glyphs
            | "shim_print_glyph" ->
                let x = int (argAt args 1)
                let y = int (argAt args 2)
                let gi = nativeint (argAt args 3)
                if gi <> IntPtr.Zero && y >= 0 && y < ROWNO && x >= 0 && x < COLNO then
                    // glyph_info layout (wintype.h): int glyph; int ttychar;
                    // uint32 framecolor; glyph_map gm { ...; classic { color; } }
                    let glyph = Marshal.ReadInt32(gi, 0)
                    let ttychar = Marshal.ReadInt32(gi, 4)
                    let color = Marshal.ReadInt32(gi, 16)
                    // Prefer our disambiguating char (box-drawing walls, doorway,
                    // tree, lava, spellbook, ...); fall back to NetHack's ttychar.
                    let mapped = nhglue_map_char glyph
                    glyphs[y, x] <- if mapped <> 0 then char mapped else char ttychar
                    if color >= 0 && color < colorName.Length then
                        cellColor[y, x] <- colorName[color]
            | "shim_curs" ->
                let w = int (argAt args 0)
                if (match winType.TryGetValue w with true, t -> t = NHW_MAP | _ -> false) then
                    heroX <- int (argAt args 1)
                    heroY <- int (argAt args 2)
            | "shim_putstr" ->
                let w = int (argAt args 0)
                if (match winType.TryGetValue w with true, t -> t = NHW_MESSAGE | _ -> false) then
                    let s = clean (strAt args 2)
                    if s <> "" then messages.Add s
            | "shim_raw_print" | "shim_raw_print_bold" ->
                let s = clean (strAt args 0)
                if s <> "" then messages.Add s
            | "shim_status_update" ->
                let fld = int (argAt args 0)
                if fld = BL_CONDITION then
                    condMask <- Marshal.ReadInt32(nativeint (argAt args 1))
                elif fld >= 0 then
                    statusText[fld] <- clean (strAt args 1)
            | "shim_start_menu" ->
                menuItems.Clear(); menuIdents.Clear(); menuTitle <- ""
            | "shim_add_menu" ->
                // args: window,glyphinfo,identifier(p),ch(0),gch,attr,clr,str,itemflags
                let ch0 = char (int (argAt args 3))
                let identPtr = nativeint (argAt args 2)
                if identPtr <> IntPtr.Zero then
                    if anythingSize = 0 then anythingSize <- nhglue_anything_size ()
                    let bytes = Array.zeroCreate<byte> anythingSize
                    Marshal.Copy(identPtr, bytes, 0, anythingSize)
                    // A selectable row carries a non-zero identifier; non-selectable
                    // headers/separators pass a pointer to a zeroed `anything`.
                    if bytes |> Array.exists (fun b -> b <> 0uy) then
                        // Inventory-style menus supply the item's own accelerator in
                        // ch; pickup and other object menus pass ch=0 and let the
                        // menu library auto-letter them — which never runs in this
                        // headless shim, so assign the next free letter ourselves.
                        let ch =
                            if ch0 <> '\000' then ch0
                            else
                                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
                                |> Seq.tryFind (fun c -> not (menuIdents.ContainsKey c))
                                |> Option.defaultValue '\000'
                        if ch <> '\000' then
                            menuIdents[ch] <- bytes
                            menuItems.Add { Key = ch; Text = strAt args 7; Glyph = None
                                            Count = None; Selected = false }
            | "shim_end_menu" -> menuTitle <- strAt args 1
            // ---- input requests: settle points ----
            | "shim_nhgetch" | "shim_nh_poskey" ->
                // nh_poskey has out params (x,y,mod); leave them zero.
                if name = "shim_nh_poskey" then
                    for i in 0 .. 2 do
                        let p = nativeint (argAt args i)
                        if p <> IntPtr.Zero then Marshal.WriteInt32(p, 0)
                if initializing then
                    // first command request => hand control to the caller
                    initializing <- false
                // Still feeding a multi-key command (count prefix / run)? Supply
                // the next queued key without asking the API thread again, so the
                // whole "20s" or "Gl" reaches NetHack as one uninterrupted command.
                if pendingKeys.Count > 0 then
                    writeInt (pendingKeys.Dequeue())
                else
                    match this.KeysFor(this.Settle Command) with
                    | first :: rest ->
                        for k in rest do pendingKeys.Enqueue k
                        writeInt first
                    | [] -> writeInt (int ' ')
            | "shim_yn_function" ->
                let query = strAt args 0
                let choices = strAt args 1
                let def = char (int (argAt args 2))
                if initializing then
                    // auto-accept pre-game prompts (e.g. "pick a character?")
                    writeChar (if def <> '\000' then def else 'y')
                elif nhglue_input_state () = GetDirInp then
                    // getdir() reads its direction through yn_function; surface it
                    // as a Direction prompt, not a bogus yes/no. A Move is the
                    // canonical reply (translated to its movement key); a raw Key
                    // or Answer char is honoured too; anything else cancels (ESC).
                    match this.Settle(Direction query) with
                    | Move _ as m -> writeChar (char (this.KeyFor m))
                    | Key c -> writeChar c
                    | Answer c -> writeChar c
                    | _ -> writeChar '\027'
                else
                    let dflt = if def = '\000' then None else Some def
                    match this.Settle(MultiChoice(query, choices, dflt)) with
                    | Answer c -> writeChar c
                    | Cancel -> writeChar '\027'    // ESC: back out of the prompt
                    | _ -> writeChar (defaultArg dflt 'q')
            | "shim_getlin" ->
                let buf = nativeint (argAt args 1)
                let reply =
                    if initializing then ""
                    else match this.Settle(TextLine(strAt args 0)) with Text s -> s | _ -> "\027"
                let bytes = Text.Encoding.ASCII.GetBytes(reply)
                if buf <> IntPtr.Zero then
                    Marshal.Copy(bytes, 0, buf, bytes.Length)
                    Marshal.WriteByte(buf, bytes.Length, 0uy)
            | "shim_select_menu" ->
                let how = int (argAt args 1)
                let outp = nativeint (argAt args 2)
                let cancel code =
                    if outp <> IntPtr.Zero then Marshal.WriteIntPtr(outp, IntPtr.Zero)
                    writeInt code
                if initializing || menuItems.Count = 0 then
                    cancel (if how = 0 then 0 else -1)
                else
                    let mode = match how with 0 -> PickNone | 1 -> PickOne | _ -> PickAny
                    let action = this.Settle(Menu(menuTitle, mode, List.ofSeq menuItems))
                    // how=0 is display-only (e.g. inventory): shown, then dismissed
                    match (if how = 0 then Proceed else action) with
                    | Choose keys when not (List.isEmpty keys) ->
                        let picks =
                            keys
                            |> List.choose (fun k ->
                                match menuIdents.TryGetValue k with true, b -> Some b | _ -> None)
                            |> (if how = 1 then List.truncate 1 else id)
                        if List.isEmpty picks then cancel -1
                        else
                            let packed = Array.zeroCreate<byte> (anythingSize * picks.Length)
                            picks |> List.iteri (fun i b -> Array.blit b 0 packed (i * anythingSize) anythingSize)
                            let counts = Array.create picks.Length -1
                            let arr = nhglue_build_menu(packed, counts, picks.Length)
                            if outp <> IntPtr.Zero then Marshal.WriteIntPtr(outp, arr)
                            writeInt picks.Length
                    | _ -> cancel (if how = 0 then 0 else -1)
            | "shim_get_ext_cmd" ->
                // Answer a pending Extended command by index; a bare '#' with
                // nothing queued cancels (returns -1), as before.
                match pendingExtCmd with
                | Some name -> pendingExtCmd <- None; writeInt (nhglue_ext_cmd_index name)
                | None -> writeInt -1
            | "shim_message_menu" -> writeChar '\033'
            | _ -> ()   // notifications with nothing to return

        /// Expand a Command-level Action into the full keystroke sequence NetHack
        /// should receive. Most actions are a single key; Run and RepeatKey expand
        /// to several (a "G"+direction, or a count prefix followed by a command),
        /// which the nhgetch handler feeds one at a time before settling again.
        member private this.KeysFor(a: NetHack.Api.Action) : int list =
            match a with
            | Run dir ->
                // 'G' + direction: travel that way until something notable.
                [ int 'G'; this.KeyFor(Move dir) ]
            | RepeatKey(n, c) when n >= 2 ->
                // Type the count's digits, then the command key: e.g. "20" + 's'.
                let n = min n 9999
                [ for d in string n -> int d ] @ [ int c ]
            | RepeatKey(_, c) -> [ int c ]
            | other -> [ this.KeyFor other ]

        /// Translate an Action into the keystroke NetHack expects from nhgetch.
        member private _.KeyFor(a: NetHack.Api.Action) : int =
            match a with
            | Move North -> int 'k' | Move South -> int 'j'
            | Move West -> int 'h'  | Move East -> int 'l'
            | Move Northeast -> int 'u' | Move Northwest -> int 'y'
            | Move Southeast -> int 'n' | Move Southwest -> int 'b'
            | Move Up -> int '<' | Move Down -> int '>'
            | Key c -> int c
            | Answer c -> int c
            | Proceed -> int ' '
            | Cancel -> int '\027'   // ESC: abort the current command/prompt
            | Number n -> int (string n).[0]
            | Extended cmd ->
                // Enter extended-command mode with '#'; the name is resolved to
                // an index when the get_ext_cmd callback fires next.
                pendingExtCmd <- Some(cmd.TrimStart('#'))
                int '#'
            | _ -> int ' '

        /// Record a fault raised inside a callback so a waiting API thread wakes
        /// up, rather than swallowing it (and possibly deadlocking). Best-effort:
        /// never throws back across the native boundary.
        member _.Fault(ex: exn) =
            try
                if not (outbox.TryAdd(Faulted ex)) then ()
            with _ -> ()

        // ---- API-thread side ----

        /// Wait for the next Signal (with a timeout) and turn it into a GameState.
        member private _.Await() : GameState =
            let mutable item = Unchecked.defaultof<Signal>
            let signal =
                if outbox.TryTake(&item, StepTimeoutMs) then item
                else Faulted(TimeoutException "NetHack did not respond within the step timeout")
            let state obs pending isOver =
                { Continuation = null; GameId = gameId
                  Observation = obs; Pending = pending; Over = isOver }
            match signal with
            | Settled(o, p) -> state o p false
            | Ended ->
                over <- true
                state lastObs (GameOver "the game ended") true
            | Faulted ex ->
                over <- true
                state lastObs (GameOver $"internal error: {ex.Message}") true

        member this.Start(opts: NewGame) : GameState =
            // windmain's authorize_wizard_mode only grants debug mode to the
            // player named "wizard", so force that name when Wizard is requested.
            let name =
                if opts.Wizard then "wizard" else defaultArg opts.Name "Player"
            cleanPlayground name
            // Fix the RNG for reproducible runs when a seed is given; windsys.c's
            // sys_random_seed honours NETHACK_SEED. Cleared otherwise so normal
            // play stays random.
            Environment.SetEnvironmentVariable(
                "NETHACK_SEED",
                match opts.Seed with Some s -> string s | None -> null)
            // Wizard games use the pet-free rc for deterministic scenarios.
            if opts.Wizard then
                Environment.SetEnvironmentVariable(
                    "NETHACKOPTIONS",
                    "@" + Path.Combine(AppContext.BaseDirectory, "sandbox-wizard.nethackrc"))
            // "-D" requests debug (wizard) mode, which windmain's
            // authorize_wizard_mode grants only to the player named "wizard".
            let args =
                [| "nethack"
                   if opts.Wizard then "-D"
                   "-u"; name
                   null |]
            let argc = args.Length - 1   // argv is null-terminated
            // First P/Invoke: loads the DLL now that the environment is final.
            nhglue_set_handler this.Handler
            let run () =
                let ending = try nhmain(argc, args) |> ignore; Ended with ex -> Faulted ex
                over <- true
                try outbox.Add ending with _ -> ()
            let t = Thread(ThreadStart run, 16 * 1024 * 1024)
            t.IsBackground <- true
            this.Thread <- t
            t.Start()
            this.Await()

        member this.Step(a: NetHack.Api.Action) : GameState =
            if over then
                { Continuation = null; GameId = gameId
                  Observation = lastObs; Pending = GameOver "the game ended"; Over = true }
            else
                inbox.Add a
                this.Await()

    // ---- factory ------------------------------------------------------

    let mutable private live = false
    let private gate = obj ()

    /// Create the native engine. Only one live session per process is allowed.
    let create () : IEngine =
        installResolver ()
        lock gate (fun () ->
            if live then invalidOp "A native NetHack session is already live in this process."
            live <- true)
        prepareEnvironment ()
        let session = Session()
        // keep the delegate alive for the life of the session; faults inside a
        // callback are captured and surfaced, never swallowed silently.
        session.Handler <-
            Handler(fun namePtr fmtPtr retPtr args _ ->
                try
                    let name = Marshal.PtrToStringAnsi namePtr
                    let fmt = Marshal.PtrToStringAnsi fmtPtr
                    session.Dispatch(name, fmt, retPtr, args)
                with ex -> session.Fault ex)
        // NB: nhglue_set_handler (the first P/Invoke, which loads the DLL) is
        // deferred to Start, so the DLL's CRT snapshots NETHACKOPTIONS/rc *after*
        // Start applies any per-game overrides (getenv reads the load-time copy).
        { new IEngine with
            member _.Start opts = session.Start opts
            member _.Step s a = session.Step a }
