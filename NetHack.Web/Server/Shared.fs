namespace NetHack.Web

open NetHack.Api

type GameStateWeb =
    {
        Observation : Observation
        Pending     : Prompt
        Over        : bool
    }

type INetHackApi =
    {
        GetGameState : unit -> Async<GameStateWeb>
    }
