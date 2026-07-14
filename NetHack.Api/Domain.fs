namespace NetHack.Api

open System.Text.Json.Serialization

/// A coordinate on the dungeon map. X is the column (0..79), Y is the row (0..20).
type Pos = { X: int; Y: int }

/// Compass and vertical directions the hero can move or act in.
type Direction =
    | North | South | East | West
    | Northeast | Northwest | Southeast | Southwest
    | Up      // climb stairs '<'
    | Down    // descend stairs '>'

/// The broad category a decoded glyph falls into. This is the "tier-2" decoding:
/// the raw NetHack glyph is resolved to a category and (when known) a name,
/// but not interpreted into strategy.
type GlyphKind =
    | HeroSelf
    | Monster
    | Pet
    | Object
    | Terrain      // floor, wall, corridor
    | Feature      // fountain, altar, throne, stairs, door, sink, ...
    | Trap
    | Warning      // remembered/again monster warning
    | Unexplored

/// A decoded, positioned thing worth calling out on the map. Terrain such as
/// walls and floor is left to `Observation.Rows`; `Entities` carries the
/// monsters, objects, traps and named features a caller reasons about.
type Entity = {
    Pos    : Pos
    Symbol : char             // the ASCII glyph NetHack would draw, e.g. 'd'
    Kind   : GlyphKind
    Name   : string option    // decoded name when known, e.g. "jackal", "fountain"
    Color  : string           // NetHack's 16-colour name, e.g. "red", "cyan"
    Pile   : bool             // Object only: this square holds more than one item
                              // (the top one is named; the rest stay hidden until visited)
    InView : bool             // true when the hero can see this square right now, so
                              // what is here is live; false when it is out of current
                              // sight and only recalled from an earlier visit, so it
                              // may have moved or gone (an object picked up, a monster
                              // wandered off). This is line-of-sight / fog-of-war, NOT
                              // monster invisibility.
}

/// Hunger state, mirroring NetHack's hunger levels.
type Hunger =
    | Satiated | NotHungry | Hungry | Weak | Fainting | Fainted | Starved

/// Status-line conditions, mirroring the BL_MASK_* flags in botl.h.
/// Present in the list == the condition is currently active.
type Condition =
    | Stone | Slime | Strangled | FoodPoisoning | TerminalIllness
    | Blind | Deaf | Stunned | Confused | Hallucinating
    | Levitating | Flying | Riding
    | Held | Holding | Trapped
    | Paralyzed | Sleeping | Unconscious
    | BareHanded

/// The hero's "background" identity — role, race, and gender. Unlike the
/// status line, these are not shown in the core game's on-screen display.
/// Mostly fixed at character creation, but gender can change mid-game (via an
/// amulet of change), so it is reported fresh each turn alongside the map.
type Character = {
    Role   : string                // "Valkyrie", "Healer", "Wizard", ...
    Race   : string                // "human", "elf", "dwarf", "gnome", "orc"
    Gender : string                // "male", "female", "neuter"
}

/// The status line, delivered field-by-field via win_status_update.
type Status = {
    Title        : string          // "Elbereth the Gallant"
    Alignment    : string          // "lawful" | "neutral" | "chaotic"
    Strength     : string          // NetHack prints e.g. "18/50", so keep a string
    Dexterity    : int
    Constitution : int
    Intelligence : int
    Wisdom       : int
    Charisma     : int
    HP           : int
    HPMax        : int
    Power        : int
    PowerMax     : int
    ArmorClass   : int
    ExpLevel     : int
    Experience   : int64 option
    Gold         : int64
    Dungeon      : string          // "The Dungeons of Doom"
    DungeonLevel : int
    Depth        : int
    Hunger       : Hunger
    Encumbrance  : string option   // "Burdened", "Stressed", ...
    Conditions   : Condition list
    Turns        : int64
    Score        : int64 option
}

/// One row of a menu (inventory, spell list, pick-up list, ...).
type MenuItem = {
    Key      : char            // selector letter, e.g. 'a'
    Text     : string          // "a - an uncursed +0 dagger"
    Glyph    : Entity option   // some menus show an object glyph
    Count    : int option      // current selection count, if any
    Selected : bool
}

/// How many items a menu lets you select.
type MenuMode = PickNone | PickOne | PickAny

/// What the game is currently waiting for. This is the crucial piece: it tells
/// the caller which Actions are legal right now, because NetHack input is modal.
/// Each case corresponds to a window-proc callback that is currently blocked.
type Prompt =
    | Command                                                  // win_nhgetch: free to issue any command
    | Direction    of question: string                        // getdir: pick a direction
    | MultiChoice  of question: string * choices: string * defaultChoice: char option  // win_yn_function: reply one char from choices (e.g. "yn","ynq"); '#' count entry unsupported
    | Quantity     of question: string                        // "How many?" numeric reply
    | TextLine     of prompt: string                          // win_getlin
    | Menu         of title: string * mode: MenuMode * items: MenuItem list  // win_select_menu
    | More                                                     // "--More--" paginated message
    | GameOver     of reason: string                          // terminal; no further input

/// An item in the hero's pack. `Letter` is the inventory slot that getobj
/// prompts refer to (e.g. answer 'q' to "What do you want to wear? [q or ?*]");
/// `Text` is its description (e.g. "a pair of hard shoes").
type InventoryItem = {
    Letter : char
    Text   : string
}

/// Everything the caller can see: a readable ASCII map, decoded entities on it,
/// the status line, and the messages the last action produced.
type Observation = {
    Width    : int             // 80
    Height   : int             // 21
    Rows      : string list    // ASCII map, one string per row — human-readable
    Legend    : Map<string, string> // meaning of each known terrain symbol in Rows, e.g. "#" -> "corridor"
    Hero      : Pos
    Character : Character      // who the hero is: role / race / gender / alignment
    Entities  : Entity list    // decoded monsters / objects / features / traps
    Status    : Status
    Inventory : InventoryItem list  // the hero's pack — what the free 'i' command shows
    Messages  : string list    // messages produced by the action that led here
}

/// The value the API is a function of. `Continuation` holds the in-process
/// engine's private state and is never serialized. Callers reason about
/// `Observation` and `Pending`.
type GameState = {
    [<JsonIgnore>] Continuation : obj  // opaque, engine-private; excluded from the wire
    // NetHack's `ubirthday` (the game's creation time); fixed for the life of a
    // game, so the host can use it as a per-game id. Host-only, not sent to the
    // agent.
    [<JsonIgnore>] GameId : int64
    Observation : Observation
    Pending     : Prompt
    Over        : bool
}

/// The player's next input, interpreted relative to the current `Prompt`.
type Action =
    | Move     of Direction    // when Pending = Command or Direction
    | Key      of char         // a raw command key, e.g. 's' search, 'i' inventory
    | Extended of string       // an extended command, e.g. "#pray"
    | Answer   of char         // reply to a MultiChoice prompt
    | Text     of string       // reply to a TextLine prompt
    | Number   of int          // reply to a Quantity prompt
    | Choose   of char list    // menu selections
    | Proceed                  // acknowledge a --More-- prompt
    | Cancel                   // back out of a prompt (sends ESC): decline a
                               // MultiChoice, abort a TextLine/Quantity/menu/Direction
    | Run      of Direction    // travel in a direction until something notable
                               // (a junction, item, monster, ...); one command
                               // that covers many tiles (NetHack's 'G'+direction)
    | RepeatKey of int * char  // a command key preceded by a repeat count, e.g.
                               // (20, 's') to search/rest 20 turns in one command

/// Options for starting a new game. `None` fields let the engine choose (or,
/// eventually, prompt through the callbacks). `Seed` fixes the RNG for
/// reproducibility.
type NewGame = {
    Name : string option
    Role : string option       // "Valkyrie", "Wizard", ...
    Race : string option       // "human", "elf", ...
    Seed : int option
    Wizard : bool              // debug (wizard) mode: enables #wiz* commands, for tests
}

module NewGame =
    /// A fully-defaulted new game request.
    let defaults =
        { Name = None; Role = None; Race = None; Seed = None; Wizard = false }

/// The engine contract. `Start` produces the initial state; `Step` advances it.
/// The proposed core signature `GameState -> Action -> GameState` is exactly
/// `Step` with its argument order. Implementations may be a live native session,
/// a WASM instance, or (here) an in-process stub.
type IEngine =
    abstract member Start : NewGame -> GameState
    abstract member Step  : GameState -> Action -> GameState
