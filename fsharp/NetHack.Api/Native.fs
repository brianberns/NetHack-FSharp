namespace NetHack.Api

open System
open System.IO
open System.Reflection
open System.Threading
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

    /// Directory holding NetHackNative.dll and the data files (nhdat500, the
    /// *.template files, ...). Defaults to the in-repo build output; override
    /// before the first `create` if the DLL lives elsewhere.
    let mutable dataDir =
        @"C:\Users\brian\source\repos\NetHack\Core\binary\Release\x64"

    let mutable private resolverInstalled = false

    let private installResolver () =
        if not resolverInstalled then
            resolverInstalled <- true
            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(),
                fun name _ _ ->
                    if name = Dll then
                        NativeLibrary.Load(Path.Combine(dataDir, Dll))
                    else IntPtr.Zero)

    // ---- window / status constants ------------------------------------

    let private NHW_MESSAGE = 1
    let private NHW_MAP = 3

    let private BL_GOLD = 10
    let private BL_HP = 18
    let private BL_HHUNGER = 17
    let private BL_CONDITION = 22

    let private COLNO = 80
    let private ROWNO = 21

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
        if p = IntPtr.Zero then "" else (Marshal.PtrToStringAnsi p |> Option.ofObj |> Option.defaultValue "")

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

    // ---- the session --------------------------------------------------

    /// Everything the callback thread and the API thread share. One instance
    /// per process while a game is live.
    type private Session() =
        // rendezvous: the game thread releases `settled` when it parks on an
        // input request; the API thread releases `action` when it has one.
        let settled = new SemaphoreSlim(0, 1)
        let action = new SemaphoreSlim(0, 1)

        // screen buffers, rebuilt as output callbacks arrive
        let glyphs = Array2D.create ROWNO COLNO ' '
        let cellColor = Array2D.create ROWNO COLNO "gray"
        let winType = System.Collections.Generic.Dictionary<int, int>()
        let statusText = System.Collections.Generic.Dictionary<int, string>()
        let messages = ResizeArray<string>()
        let menuItems = ResizeArray<MenuItem>()

        let mutable nextWin = 1
        let mutable condMask = 0
        let mutable heroX = 0
        let mutable heroY = 0
        // during startup we auto-answer pre-game prompts until the first command
        let mutable initializing = true
        let mutable over = false

        // published to the API thread at each settle point
        let mutable curObs = Unchecked.defaultof<Observation>
        let mutable curPrompt = Command
        let mutable pending : NetHack.Api.Action = Proceed

        member val Handler : Handler = Unchecked.defaultof<Handler> with get, set
        member val Thread : Thread = null with get, set

        member _.Over = over

        member private _.BuildStatus() : Status =
            let txt k = match statusText.TryGetValue k with true, v -> v | _ -> ""
            let conds =
                conditionBits
                |> List.choose (fun (bit, c) -> if condMask &&& bit <> 0 then Some c else None)
            { Title = txt 0
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

        member private this.BuildObservation() : Observation =
            let rows =
                [ for y in 0 .. ROWNO - 1 ->
                    String(Array.init COLNO (fun x -> glyphs[y, x])) ]
            let hero = { X = heroX; Y = heroY }
            let entities =
                [ { Pos = hero; Symbol = '@'; Kind = HeroSelf
                    Name = Some "you"; Color = "white"; Glyph = 0 } ]
            { Width = COLNO; Height = ROWNO; Rows = rows; Hero = hero
              Entities = entities; Status = this.BuildStatus()
              Messages = List.ofSeq messages }

        /// Called from the game thread when it needs input: publish the current
        /// screen + prompt, block until the API thread supplies an Action.
        member private this.Settle(prompt: Prompt) : NetHack.Api.Action =
            curObs <- this.BuildObservation()
            curPrompt <- prompt
            messages.Clear()          // messages are per-step
            settled.Release() |> ignore
            action.Wait()
            pending

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
                    let s = strAt args 2
                    if s <> "" then messages.Add s
            | "shim_raw_print" | "shim_raw_print_bold" ->
                let s = strAt args 0
                if s <> "" then messages.Add s
            | "shim_status_update" ->
                let fld = int (argAt args 0)
                if fld = BL_CONDITION then
                    condMask <- Marshal.ReadInt32(nativeint (argAt args 1))
                elif fld >= 0 then
                    statusText[fld] <- strAt args 1
            | "shim_start_menu" -> menuItems.Clear()
            | "shim_add_menu" ->
                let ch = char (int (argAt args 3))
                let text = strAt args 7
                menuItems.Add { Key = ch; Text = text; Glyph = None
                                Count = None; Selected = false }
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
                else
                    let dflt = if def = '\000' then None else Some def
                    match this.Settle(YesNo(query, choices, dflt)) with
                    | Answer c -> writeChar c
                    | _ -> writeChar (defaultArg dflt 'q')
            | "shim_getlin" ->
                let query = strAt args 0
                let buf = nativeint (argAt args 1)
                let reply =
                    if initializing then ""
                    else match this.Settle(TextLine query) with Text s -> s | _ -> "\027"
                let bytes = Text.Encoding.ASCII.GetBytes(reply)
                if buf <> IntPtr.Zero then
                    Marshal.Copy(bytes, 0, buf, bytes.Length)
                    Marshal.WriteByte(buf, bytes.Length, 0uy)
            | "shim_select_menu" ->
                // returning selections is not wired up yet; cancel the menu.
                let outp = nativeint (argAt args 2)
                if outp <> IntPtr.Zero then Marshal.WriteIntPtr(outp, IntPtr.Zero)
                if not initializing then this.Settle(Menu("", PickNone, List.ofSeq menuItems)) |> ignore
                writeInt -1
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

        // ---- API-thread side ----
        member this.Start(opts: NewGame) : GameState =
            let argv =
                [| "nethack"; "-u"; defaultArg opts.Name "Player"; null |]
            let t =
                Thread((fun () ->
                    nhmain(3, argv) |> ignore
                    over <- true
                    settled.Release() |> ignore),
                    16 * 1024 * 1024)
            t.IsBackground <- true
            this.Thread <- t
            t.Start()
            settled.Wait()
            this.ToState()

        member this.Step(a: NetHack.Api.Action) : GameState =
            if over then this.ToState()
            else
                pending <- a
                action.Release() |> ignore
                settled.Wait()
                this.ToState()

        member private _.ToState() : GameState =
            { Continuation = null; Session = "native"
              Observation = curObs; Pending = (if over then GameOver "ended" else curPrompt)
              Over = over }

    // ---- data files & lifecycle ---------------------------------------

    let private dataFiles =
        [ "sysconf.template"; "symbols.template"; "nethackrc.template"
          "nhdat500"; "record"; "opthelp"; "license"; "nethack.txt"
          "Guidebook.txt"; Dll ]

    /// NetHack derives its data directory from the host exe's folder, so make
    /// sure the data files sit next to it, and run from there.
    let private prepareEnvironment () =
        // vi-keys movement (number_pad off) and plain-ASCII map symbols so the
        // Rows are clean and Move maps to hjkl.
        Environment.SetEnvironmentVariable("NETHACKOPTIONS",
            "number_pad:0,symset:plain,roguesymset:plain")
        let baseDir = AppContext.BaseDirectory
        for f in dataFiles do
            let dst = Path.Combine(baseDir, f)
            let src = Path.Combine(dataDir, f)
            // always refresh, so a rebuilt DLL / data is picked up
            if File.Exists src then
                try File.Copy(src, dst, true) with _ -> ()
        // the stock rc template forces the IBMGraphics_2 line-drawing symset;
        // replace it with a clean one so the map is plain ASCII.
        File.WriteAllText(Path.Combine(baseDir, "nethackrc.template"),
            "OPTIONS=number_pad:0\nOPTIONS=symset:plain,roguesymset:plain\n")
        Directory.SetCurrentDirectory(baseDir)

    /// NetHack's writable playground (level/lock/save files) lives under the
    /// user's local app data, not the exe dir. Clear leftovers from prior runs
    /// so getlock() doesn't prompt to recover an interrupted game.
    let private cleanPlayground () =
        let candidates =
            [ Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ]
            |> List.map (fun d -> Path.Combine(d, "NetHack", "5.0"))
        for d in candidates do
            if Directory.Exists d then
                for f in Directory.GetFiles d do
                    let n = Path.GetFileName f
                    // level files end in .<digits>; also lock and save files
                    if Regex.IsMatch(n, @"\.\d+$")
                       || n.Contains("lock", StringComparison.OrdinalIgnoreCase)
                       || n.StartsWith("save", StringComparison.OrdinalIgnoreCase)
                       || n.Equals(".nethackrc", StringComparison.OrdinalIgnoreCase)
                       || n.Equals("nethackrc", StringComparison.OrdinalIgnoreCase) then
                        try File.Delete f with _ -> ()

    let mutable private live = false
    let private gate = obj ()

    /// Create the native engine. Only one live session per process is allowed.
    let create () : IEngine =
        installResolver ()
        lock gate (fun () ->
            if live then invalidOp "A native NetHack session is already live in this process."
            live <- true)
        prepareEnvironment ()
        cleanPlayground ()
        let session = Session()
        // keep the delegate alive for the life of the session
        session.Handler <-
            Handler(fun namePtr fmtPtr retPtr args _ ->
                try
                    let name = Marshal.PtrToStringAnsi namePtr
                    let fmt = Marshal.PtrToStringAnsi fmtPtr
                    session.Dispatch(name, fmt, retPtr, args)
                with _ -> ())
        nhglue_set_handler session.Handler
        { new IEngine with
            member _.Start opts = session.Start opts
            member _.Step s a = session.Step a }
