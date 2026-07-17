namespace NetHack.Web

open NetHack.Agent
open NetHack.Api

/// Client-server DTO.
type SessionState =
    {
        /// What the NetHack player can observe.
        Observation : Observation

        /// Type of action NetHack expects of the player.
        Pending : Prompt

        /// Player's current notes.
        CurrentNotes : Note[]

        /// 0-based indexes of current notes that the player found
        /// relevant on this turn.
        RelevantNotes : int[]

        /// 0-based indexes of current notes that the player deleted
        /// on this turn.
        NotesToDelete : int[]

        /// Notes that the player created on this turn.
        NotesToAdd : Note[]

        /// Action taken by the player on this turn.
        Action : string

        /// Player's prediction of the effect this action will have.
        Prediction : string
    }

/// Client-server NetHack web API.
type INetHackApi =
    {
        /// Gets the current number of game steps.
        GetStateCount : unit -> Async<int>

        /// Gets the session state at the given 0-based index.
        GetSessionState : int -> Async<Result<SessionState, string>>
    }
