namespace NetHack.Web

type GameState = string

type INetHackApi =
    {
        GetGameState : unit -> Async<GameState>
    }
