/*
 * nhglue.c - marshalling glue between NetHack's variadic shim callback and a
 * fixed-signature handler that .NET P/Invoke can bind.
 *
 * NetHack's shim window port calls a single variadic callback for every window
 * operation:
 *     void callback(const char *name, void *ret_ptr, const char *fmt, ...);
 * where fmt[0] is the return type and fmt[1..] describe the (by-value) argument
 * types.  .NET cannot marshal a managed callback with a C variadic signature,
 * so this trampoline unpacks the va_list into a uniform array of 64-bit slots
 * and forwards to a normalized handler:
 *
 *     void handler(const char *name, const char *fmt, void *ret_ptr,
 *                  unsigned long long *args, int nargs);
 *
 * The managed side reads args[i] according to fmt[i+1] and, for a non-void
 * return (fmt[0] != 'v'), writes the result through ret_ptr.
 *
 * Type codes (from win/shim + sys/libnh/README.md):
 *   v void   i/n/2 int(4)   1 short(2)   0 byte(1)   c char   b boolean
 *   s char*  p void*        f float      d double
 * Integer-ish and char/boolean args are promoted to int through '...'; float is
 * promoted to double.  Only s/p are pointer-width; f/d occupy a 64-bit slot.
 */

#include <stdarg.h>
#include <stdint.h>
#include <string.h>

typedef void (*shim_callback_t)(const char *name, void *ret_ptr,
                                const char *fmt, ...);
extern void shim_graphics_set_callback(shim_callback_t cb);

typedef void (*nhglue_handler_t)(const char *name, const char *fmt,
                                 void *ret_ptr, unsigned long long *args,
                                 int nargs);

static nhglue_handler_t nhglue_handler = 0;

#define NHGLUE_MAX_ARGS 24

static void
nhglue_trampoline(const char *name, void *ret_ptr, const char *fmt, ...)
{
    unsigned long long args[NHGLUE_MAX_ARGS];
    int nargs = 0;
    const char *p;
    va_list ap;

    if (!nhglue_handler)
        return;

    va_start(ap, fmt);
    /* fmt[0] is the return type; arguments start at fmt[1] */
    for (p = fmt + 1; *p && nargs < NHGLUE_MAX_ARGS; ++p) {
        switch (*p) {
        case 's': /* char*  */
        case 'p': /* void*  */
            args[nargs++] = (unsigned long long) (uintptr_t) va_arg(ap, void *);
            break;
        case 'f': /* float promoted to double */
        case 'd': {
            double d = va_arg(ap, double);
            unsigned long long u;
            memcpy(&u, &d, sizeof u);
            args[nargs++] = u;
            break;
        }
        default: /* i n 2 1 0 c b : all promoted to int through '...' */
            args[nargs++] = (unsigned long long) (unsigned int) va_arg(ap, int);
            break;
        }
    }
    va_end(ap);

    nhglue_handler(name, fmt, ret_ptr, args, nargs);
}

/* Register (or clear, with 0) the managed handler and wire up the shim. */
void
nhglue_set_handler(nhglue_handler_t handler)
{
    nhglue_handler = handler;
    shim_graphics_set_callback(handler ? nhglue_trampoline : 0);
}
