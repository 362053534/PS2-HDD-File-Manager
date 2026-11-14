#!/usr/bin/env python3
"""Mac-only framed command replies for 362053534/pfsshell; legacy mode unchanged."""
from pathlib import Path
import sys
p = Path(sys.argv[1])
s = p.read_text(encoding="utf-8")
marker = "/* APA_NATIVE_SESSION_V1 */"
if marker in s:
    print("macOS session protocol already applied")
    sys.exit(0)
start = s.index("int shell_loop(")
end = s.index("\nstatic int do_lcd(", start)
body = s[start:end]
needle = "    while (true) {\n"
assert body.count(needle) == 1, "Unrecognized shell loop"
body = body.replace(needle, r"""    /* APA_NATIVE_SESSION_V1 */
    const char *session = getenv("APA_NATIVE_SESSION");
    if (session && *session) {
        fprintf(out, "\n__APA_NATIVE_SESSION_V1__%s:READY\n", session);
        fflush(out);
    }
    while (true) {
""", 1)
needle = "        /* update prompt */"
assert body.count(needle) == 1
body = body.replace(needle, r"""        if (session && *session) {
            fflush(err);
            fflush(out);
            fprintf(out, "\n__APA_NATIVE_SESSION_V1__%s:%d\n", session, result);
            fflush(out);
        }

        /* update prompt */""", 1)
s = s[:start]+body+s[end:]
# Explicit filesystem sync before acknowledging each file. No implicit global option.
needle = '        {"pwd", 0, need_device + need_mount, &do_pwd},'
assert s.count(needle) == 1
s = s.replace(needle, needle+'\n        {"apa_sync", 0, need_device + need_mount, &do_apa_sync},',1)
needle = "static int do_pwd("
assert s.count(needle) == 1
s = s.replace(needle, r"""static int do_apa_sync(context_t *ctx, int argc, char *argv[])
{
    return iomanX_sync("pfs0:", 0);
}

static int do_pwd(""",1)
p.write_text(s, encoding="utf-8")
print("Applied macOS framed session protocol")
