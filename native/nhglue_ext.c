/*
 * nhglue_ext.c - NetHack-aware helpers for the libnethack shim DLL.
 *
 * Unlike nhglue.c (a standalone variadic trampoline), this file includes
 * hack.h and uses the game's data tables and functions. Everything here is
 * only safe to call while the game is parked between turns waiting for input,
 * which is exactly when the F# engine builds an Observation (on the game
 * thread, inside a shim input callback).
 */

#include "hack.h"
#include "func_tab.h"   /* extcmdlist[] */
#include <string.h>

static void
copy_name(char *buf, int buflen, const char *name)
{
    int i = 0;
    if (!buf || buflen <= 0)
        return;
    if (name)
        while (name[i] && i < buflen - 1) {
            buf[i] = name[i];
            i++;
        }
    buf[i] = '\0';
}

/*
 * Is map cell (x,y) currently within the hero's sight, as opposed to only
 * remembered from an earlier visit? This is the same IN_SIGHT bit the tty/tiles
 * UI reads to draw a cell bright rather than dimmed. Returns 1 when visible now,
 * 0 when merely remembered (or out of bounds).
 */
int nhglue_cansee(int x, int y);

int
nhglue_cansee(int x, int y)
{
    if (x < 1 || x >= COLNO || y < 0 || y >= ROWNO)
        return 0;
    return cansee(x, y) ? 1 : 0;
}

/*
 * Describe what is remembered at map cell (x,y). Returns a category:
 *   0 skip (terrain/features stay in the ASCII map), 1 monster, 2 pet,
 *   3 object, 4 trap. On a nonzero return, buf receives the concise name
 *   (e.g. "jackal", "food ration", "arrow trap").
 */
int nhglue_describe_at(int x, int y, char *buf, int buflen);

int
nhglue_describe_at(int x, int y, char *buf, int buflen)
{
    int glyph, category = 0;
    coord cc;
    const char *firstmatch = 0;
    struct permonst *supp = 0;
    char out[BUFSZ];

    if (buf && buflen > 0)
        buf[0] = '\0';
    if (x < 1 || x >= COLNO || y < 0 || y >= ROWNO)
        return 0;

    glyph = glyph_at(x, y);
    if (glyph_is_monster(glyph))
        category = glyph_is_pet(glyph) ? 2 : 1;
    else if (glyph_is_object(glyph))
        category = 3;
    else if (glyph_is_trap(glyph))
        category = 4;
    else if (glyph_is_invisible(glyph) || glyph_is_warning(glyph))
        /* 'I' marks a square the hero remembers holds a monster it cannot see
           (map_invisible); a warning glyph marks a sensed-but-unseen monster
           (the 'warning' option). Both mean "a monster is here you can't see",
           so surface them as an entity rather than leaving a bare symbol on the
           map with nothing in the entity list to explain it. */
        category = 5;
    else
        return 0;

    cc.x = x;
    cc.y = y;
    out[0] = '\0';
    (void) do_screen_description(cc, TRUE, 0, out, &firstmatch, &supp);
    copy_name(buf, buflen, (firstmatch && *firstmatch) ? firstmatch : out);
    return category;
}

/*
 * Report the hero's "background" identity into three caller-provided buffers:
 * role name ("Valkyrie", "Healer"), race noun ("human", "elf") and gender
 * ("male", "female", ...). Alignment is omitted here because it is already on
 * the status line. Read fresh from the live globals each call, so a mid-game
 * change (e.g. gender via an amulet of change) is reflected. Any buffer may be
 * null to skip it.
 */
void nhglue_hero_ident(char *role, int rolelen, char *race, int racelen,
                       char *gender, int genderlen);

void
nhglue_hero_ident(char *role, int rolelen, char *race, int racelen,
                  char *gender, int genderlen)
{
    int g = Ugender; /* 0 male, 1 female; honours polymorph */
    const char *rolename =
        (g && gu.urole.name.f) ? gu.urole.name.f : gu.urole.name.m;

    copy_name(role, rolelen, rolename);
    copy_name(race, racelen, gu.urace.noun);
    copy_name(gender, genderlen, genders[g].adj);
}

/*
 * NetHack's global `ubirthday`: the time_t at which the current game was
 * created. It is fixed for the life of a game and is what the score/xlog
 * records use to identify one, so the host can treat it as a per-game id.
 * Returned as a 64-bit value (time_t is 64-bit on the target).
 */
long long nhglue_game_id(void);

long long
nhglue_game_id(void)
{
    return (long long) ubirthday;
}

/*
 * The core's current input_state (see the getposInp/getdirInp enum in hack.h).
 * yn_function is overloaded, so the F# side reads this to tell a genuine yes/no
 * prompt (any other state) apart from getdir()'s "In what direction?"
 * (getdirInp), which the core also drives through yn_function.
 */
int nhglue_input_state(void);

int
nhglue_input_state(void)
{
    return program_state.input_state;
}

/*
 * Does the *displayed* glyph at (x,y) mark a pile — more than one object stack?
 * Uses glyph_at (what the player sees/remembers), so it respects sight and the
 * hilite_pile option, and never leaks the contents of a pile. 1 if a pile, else 0.
 */
int nhglue_is_pile_at(int x, int y);

int
nhglue_is_pile_at(int x, int y)
{
    int g;

    if (x < 1 || x >= COLNO || y < 0 || y >= ROWNO)
        return 0;
    /* the combined glyph_is_piletop() macro is #if 0'd in this build, so test
       the individual pile-top predicates that cover objects, corpses, statues */
    g = glyph_at(x, y);
    return (glyph_is_normal_piletop_obj(g) || glyph_is_piletop_generic_obj(g)
            || glyph_is_body_piletop(g) || glyph_is_fem_statue_piletop(g)
            || glyph_is_male_statue_piletop(g))
               ? 1 : 0;
}

/*
 * Report the index-th object lying on the floor at (x,y), read from the object
 * chain rather than the displayed glyph, so objects hidden under the hero (or a
 * monster) are still described. Fills buf via doname() and returns the object's
 * class symbol as its notional drawn char (>0); returns 0 when there is no such
 * object. Callers iterate index 0,1,... until it returns 0.
 */
int nhglue_floor_object_at(int x, int y, int index, char *buf, int buflen);

int
nhglue_floor_object_at(int x, int y, int index, char *buf, int buflen)
{
    struct obj *otmp;
    int i = 0;

    if (buf && buflen > 0)
        buf[0] = '\0';
    if (x < 1 || x >= COLNO || y < 0 || y >= ROWNO)
        return 0;
    for (otmp = svl.level.objects[x][y]; otmp; otmp = otmp->nexthere) {
        if (i++ == index) {
            copy_name(buf, buflen, doname(otmp));
            return (int) def_oc_syms[otmp->oclass].sym;
        }
    }
    return 0;
}

/*
 * The index-th item in the hero's inventory: returns its inventory letter
 * (>0) and fills buf with doname() (e.g. "a pair of hard shoes"); returns 0
 * when there is no such item. Callers iterate index 0,1,... until it returns
 * 0. Surfaces exactly what the player could see with the free 'i' command.
 */
int nhglue_inventory_item(int index, char *buf, int buflen);

int
nhglue_inventory_item(int index, char *buf, int buflen)
{
    struct obj *otmp;
    int i = 0;

    if (buf && buflen > 0)
        buf[0] = '\0';
    for (otmp = gi.invent; otmp; otmp = otmp->nobj) {
        if (i++ == index) {
            copy_name(buf, buflen, doname(otmp));
            return (int) otmp->invlet;
        }
    }
    return 0;
}

/*
 * Concise terrain/feature name for the *displayed* glyph at map cell (x,y) —
 * e.g. "corridor", "wall", "staircase up", "fountain", "stone". Uses only what
 * the UI shows (glyph_at), so it reports only terrain the hero actually knows.
 * Fills buf and returns 1 for identified background (cmap) cells; returns 0 for
 * unexplored blanks (deliberately unlabelled — the hero can't tell rock from
 * undiscovered floor) and for foreground glyphs (monster/object/trap), which
 * are already reported as entities. Lets the F# side build an accurate legend.
 */
int nhglue_feature_at(int x, int y, char *buf, int buflen);

int
nhglue_feature_at(int x, int y, char *buf, int buflen)
{
    int glyph;

    if (buf && buflen > 0)
        buf[0] = '\0';
    if (x < 1 || x >= COLNO || y < 0 || y >= ROWNO)
        return 0;
    glyph = glyph_at(x, y);
    if (glyph_is_cmap(glyph)) {
        /* defsyms[].explanation is the concise generic name in the drawing
           build of the table (S_vwall -> "wall", S_corr -> "corridor"). */
        copy_name(buf, buflen, defsyms[glyph_to_cmap(glyph)].explanation);
        return 1;
    }
    return 0; /* unexplored blank, or foreground (an entity) */
}

/*
 * Custom disambiguating character for the *displayed* glyph, so the ASCII map
 * can separate glyphs NetHack draws with the same char: doorway vs floor, tree
 * vs corridor, lava vs water, a spellbook object vs a closed door, etc. Also
 * re-draws the eleven wall/corner glyphs with Unicode box-drawing (mirroring
 * NetHack's own DECgraphics orientation), which frees '+' to mean closed door
 * only. Returns a Unicode code point (>0) for glyphs we re-map, or 0 to keep
 * the engine's normal ttychar. Monsters and ordinary objects fall through to 0
 * so they keep their usual symbols. Fog-of-war-safe: the caller passes the
 * glyph the player actually sees (glyph_at), so this only re-renders, it never
 * reveals anything the UI wouldn't already show.
 */
int nhglue_map_char(int glyph);

int
nhglue_map_char(int glyph)
{
    if (glyph_is_cmap(glyph)) {
        switch (glyph_to_cmap(glyph)) {
        /* walls & corners: box-drawing, per NetHack's own DECgraphics shapes */
        case S_vwall:   return 0x2502; /* | -> box vertical */
        case S_hwall:   return 0x2500; /* - -> box horizontal */
        case S_tlcorn:  return 0x250C; /* top-left corner */
        case S_trcorn:  return 0x2510; /* top-right corner */
        case S_blcorn:  return 0x2514; /* bottom-left corner */
        case S_brcorn:  return 0x2518; /* bottom-right corner */
        case S_crwall:  return 0x253C; /* cross */
        case S_tuwall:  return 0x2534; /* T-up */
        case S_tdwall:  return 0x252C; /* T-down */
        case S_tlwall:  return 0x2524; /* T-left */
        case S_trwall:  return 0x251C; /* T-right */
        /* doors: closed stays '+'; the two open doors and the doorless
           doorway split off from the wall chars they otherwise share */
        case S_hodoor:  return 0x2016; /* open door in a vertical wall */
        case S_vodoor:  return 0x2550; /* open door in a horizontal wall */
        case S_ndoor:   return 0x25AB; /* doorway (no door) */
        /* terrain that otherwise hides behind corridor/fountain/water/wall */
        case S_tree:    return 0x2663; /* tree */
        case S_bars:    return 0x2263; /* iron bars */
        case S_sink:    return 0x2294; /* sink (vs fountain '{') */
        case S_lava:    return 0x224B; /* molten lava (vs water '}') */
        case S_grave:   return 0x2020; /* grave (vs wall) */
        default:        return 0;      /* floor, corridor, fountain, ... as-is */
        }
    }
    if (glyph_is_object(glyph)) {
        int otyp = glyph_to_obj(glyph);
        if (otyp >= 0 && otyp < NUM_OBJECTS) {
            if (objects[otyp].oc_class == SPBOOK_CLASS)
                return 0x00B1; /* spellbook (vs closed door '+') */
            if (otyp == BOULDER)
                return 0x25CF; /* boulder (vs engraving '`') */
        }
    }
    return 0; /* monsters, other objects, traps, blanks: keep normal symbol */
}

/*
 * Index of the extended command named `name` (e.g. "loot", "pray") in
 * extcmdlist[], or -1 if there is no such command. get_ext_cmd() returns such
 * an index, so this lets the F# side answer an Action.Extended by name. Case
 * insensitive; a leading '#' should already be stripped by the caller.
 */
int nhglue_ext_cmd_index(const char *name);

int
nhglue_ext_cmd_index(const char *name)
{
    int i;

    if (name)
        for (i = 0; extcmdlist[i].ef_txt; i++)
            if (!strcmpi(extcmdlist[i].ef_txt, name))
                return i;
    return -1;
}

/* Size of the `anything` identifier union, so the F# side can pack a copy. */
int nhglue_anything_size(void);

int
nhglue_anything_size(void)
{
    return (int) sizeof(anything);
}

/*
 * Build a menu_item[count] for *menu_list from `count` packed identifier
 * copies (each sizeof(anything) bytes) and per-item counts. Uses NetHack's
 * allocator so the core can free() the array normally (same convention as the
 * tty port's select_menu).
 */
void *nhglue_build_menu(const unsigned char *anythings, const int *counts, int count);

void *
nhglue_build_menu(const unsigned char *anythings, const int *counts, int count)
{
    int i, asz;
    menu_item *mi;

    if (count <= 0)
        return (void *) 0;
    asz = (int) sizeof(anything);
    mi = (menu_item *) alloc((unsigned) (count * (int) sizeof(menu_item)));
    for (i = 0; i < count; i++) {
        memcpy((void *) &mi[i].item, anythings + (i * asz), sizeof(anything));
        mi[i].count = (long) counts[i];
    }
    return (void *) mi;
}
