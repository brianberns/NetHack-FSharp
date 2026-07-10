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
