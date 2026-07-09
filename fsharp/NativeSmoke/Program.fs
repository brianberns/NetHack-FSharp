module NativeSmoke.Program

// Smoke test for the NetHackNative DLL: register a managed handler, run nhmain,
// and log every window callback until NetHack asks for input. Reaching an input
// request proves the full pipe (P/Invoke -> glue -> shim -> core) works.

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

let binDir =
    @"C:\Users\brian\source\repos\NetHack\Core\binary\Release\x64"

let dllPath = Path.Combine(binDir, "NetHackNative.dll")

// void handler(const char *name, const char *fmt, void *ret_ptr,
//              unsigned long long *args, int nargs)
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Handler =
    delegate of name: nativeint * fmt: nativeint * retPtr: nativeint *
                args: nativeint * nargs: int -> unit

[<DllImport("NetHackNative.dll", CallingConvention = CallingConvention.Cdecl)>]
extern void nhglue_set_handler(Handler handler)

[<DllImport("NetHackNative.dll", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)>]
extern int nhmain(int argc,
                  [<MarshalAs(UnmanagedType.LPArray,
                              ArraySubType = UnmanagedType.LPStr)>] string[] argv)

let mutable calls = 0
let counts = System.Collections.Generic.Dictionary<string, int>()
let noisy = set [ "shim_print_glyph"; "shim_status_update"; "shim_curs"
                  "shim_cliparound" ]
let inputCalls = set [ "shim_nhgetch"; "shim_nh_poskey"; "shim_yn_function"
                       "shim_getlin"; "shim_get_ext_cmd"; "shim_message_menu"
                       "shim_select_menu" ]

let summary () =
    printfn "\n=== callback summary (%d total) ===" calls
    for kv in Seq.sortBy (fun (kv: Collections.Generic.KeyValuePair<_,_>) -> -kv.Value) counts do
        printfn "  %-28s %d" kv.Key kv.Value

let handler =
    Handler(fun namePtr fmtPtr retPtr _args _nargs ->
        try
            let name = Marshal.PtrToStringAnsi(namePtr)
            let fmt = Marshal.PtrToStringAnsi(fmtPtr)
            calls <- calls + 1
            counts[name] <- (match counts.TryGetValue name with
                             | true, c -> c + 1 | _ -> 1)
            if not (noisy.Contains name) then
                printfn "[%5d] %-28s fmt=%s" calls name fmt

            let retType = if String.IsNullOrEmpty fmt then 'v' else fmt.[0]

            // Reaching ANY input request means startup completed end-to-end.
            if inputCalls.Contains name then
                printfn "\n=== reached input request '%s' after %d callbacks: pipe works end-to-end ===" name calls
                summary ()
                Environment.Exit(0)

            // Auto-answer non-input callbacks that expect a value.
            if retPtr <> IntPtr.Zero then
                match retType with
                | 'c' -> Marshal.WriteByte(retPtr, byte 'y')
                | 'i' | 'n' | '2' | '1' | '0' | 'b' -> Marshal.WriteInt32(retPtr, 0)
                | 's' | 'p' -> Marshal.WriteIntPtr(retPtr, IntPtr.Zero)
                | _ -> ()

            if calls > 200000 then
                printfn "\n=== stopping after %d callbacks (no input request seen) ===" calls
                summary ()
                Environment.Exit(2)
        with ex ->
            printfn "handler error: %s" ex.Message
            Environment.Exit(3))

[<EntryPoint>]
let main _ =
    NativeLibrary.SetDllImportResolver(
        Assembly.GetExecutingAssembly(),
        fun name _ _ ->
            if name = "NetHackNative.dll" then NativeLibrary.Load(dllPath)
            else IntPtr.Zero)

    // NetHack looks for its data files (nhdat500, sysconf, ...) in the cwd.
    Directory.SetCurrentDirectory(binDir)
    Environment.SetEnvironmentVariable("NETHACKDIR", binDir)

    nhglue_set_handler(handler)

    // argv must be NULL-terminated for C main(); -u sets the player name so
    // startup does not block on askname().
    let argv = [| "nethack"; "-u"; "Tester"; null |]
    printfn "calling nhmain..."
    let rc = nhmain(3, argv)
    printfn "nhmain returned %d (unexpected: we expected to exit on input)" rc
    GC.KeepAlive(handler)
    0
