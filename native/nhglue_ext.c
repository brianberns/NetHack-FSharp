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
    if (x < 0 || x >= COLNO || y < 0 || y >= ROWNO)
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
    if (x < 0 || x >= COLNO || y < 0 || y >= ROWNO)
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
