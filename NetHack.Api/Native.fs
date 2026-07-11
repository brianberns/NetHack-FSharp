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

    // Report the hero's role / race / gender into three buffers.
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern void private nhglue_hero_ident(
        System.Text.StringBuilder role, int rolelen,
        System.Text.StringBuilder race, int racelen,
        System.Text.StringBuilder gender, int genderlen)

    // program_state.input_state at the moment of the call (getdirInp == 3, etc.).
    [<DllImport(Dll, CallingConvention = CallingConvention.Cdecl)>]
    extern int private nhglue_input_state()

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
        // vi-keys movement (number_pad off); plain-ASCII symbols are forced in
        // the DLL (windmain LIBNH_SHIM). Process-scoped env only.
        Environment.SetEnvironmentVariable("NETHACKOPTIONS",
            "number_pad:0,symset:plain,roguesymset:plain")
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
        // the stock rc template forces the IBMGraphics_2 line-drawing symset;
        // replace it with a clean one so the map is plain ASCII.
        File.WriteAllText(Path.Combine(baseDir, "nethackrc.template"),
            "OPTIONS=number_pad:0\nOPTIONS=symset:plain,roguesymset:plain\n")
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

        member private this.BuildObservation() : Observation =
            let rows =
                [ for y in 0 .. ROWNO - 1 ->
                    String(Array.init COLNO (fun x -> glyphs[y, x])) ]
            let hero = { X = heroX; Y = heroY }
            let entities = ResizeArray<Entity>()
            entities.Add { Pos = hero; Symbol = '@'; Kind = HeroSelf
                           Name = Some "you"; Color = "white"; Glyph = 0 }
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
                                  Color = cellColor[y, x]; Glyph = 0 }
            { Width = COLNO; Height = ROWNO; Rows = rows; Hero = hero
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
                    let ttychar = Marshal.ReadInt32(gi, 4)
                    let color = Marshal.ReadInt32(gi, 16)
                    glyphs[y, x] <- char ttychar
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
                let ch = char (int (argAt args 3))
                let identPtr = nativeint (argAt args 2)
                // selectable items have an accelerator and a non-null identifier
                if ch <> '\000' && identPtr <> IntPtr.Zero then
                    if anythingSize = 0 then anythingSize <- nhglue_anything_size ()
                    let bytes = Array.zeroCreate<byte> anythingSize
                    Marshal.Copy(identPtr, bytes, 0, anythingSize)
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
                writeInt (this.KeyFor(this.Settle Command))
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
            | "shim_get_ext_cmd" -> writeInt -1
            | "shim_message_menu" -> writeChar '\033'
            | _ -> ()   // notifications with nothing to return

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
            | Number n -> int (string n).[0]
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
                { Continuation = null; Session = "native"
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
            let name = defaultArg opts.Name "Player"
            cleanPlayground name
            let argv = [| "nethack"; "-u"; name; null |]
            let run () =
                let ending = try nhmain(3, argv) |> ignore; Ended with ex -> Faulted ex
                over <- true
                try outbox.Add ending with _ -> ()
            let t = Thread(ThreadStart run, 16 * 1024 * 1024)
            t.IsBackground <- true
            this.Thread <- t
            t.Start()
            this.Await()

        member this.Step(a: NetHack.Api.Action) : GameState =
            if over then
                { Continuation = null; Session = "native"
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
        nhglue_set_handler session.Handler
        { new IEngine with
            member _.Start opts = session.Start opts
            member _.Step s a = session.Step a }
