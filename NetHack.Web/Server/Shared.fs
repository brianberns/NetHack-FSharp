namespace NetHack.Web

open NetHack.Api

type SessionState =
    {
        Observation : Observation
        Pending : Prompt
        Over : bool
    }

type INetHackApi =
    {
        GetSessionState : unit -> Async<SessionState>
    }
