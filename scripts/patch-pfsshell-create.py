#!/usr/bin/env python3
"""Apply the local safety guard to 362053534/pfsshell; fail closed on source drift."""
from pathlib import Path
import sys
p=Path(sys.argv[1])
s=p.read_text(encoding="utf-8")
start=s.index('static int do_mkpart(')
end=s.index('\nstatic int do_ls(',start)
body=s[start:end]
marker='/* APA_CREATE_REQUIRE_COMPLETE_V1 */'
if marker in body:
 print('pfsshell complete-allocation guard already applied')
 sys.exit(0)
needle='''    if (result >= 0) {
        (void)iomanX_close(partfd), result = 0;'''
if body.count(needle)!=1:
 sys.exit('Unrecognized pfsshell mkpart implementation; refusing an unverified patch')
guard='''    /* APA_CREATE_REQUIRE_COMPLETE_V1 */
    if (result >= 0 && size_in_mb != 0) {
        int remove_result;
        (void)iomanX_close(partfd);
        remove_result = iomanX_remove(openString);
        fprintf(stderr, "(!) %s: requested allocation is incomplete (%u MiB remaining); refusing to format.\\n", argv[1], size_in_mb);
        if (remove_result < 0) {
            fprintf(stderr, "(!) %s: failed to remove the newly created incomplete partition; stop writes and inspect the APA table.\\n", argv[1]);
            return remove_result;
        }
        fprintf(stderr, "(!) %s: newly created incomplete partition removed.\\n", argv[1]);
        return -ENOSPC;
    }

'''
p.write_text(s[:start]+body.replace(needle,guard+needle,1)+s[end:], encoding="utf-8")
print('Applied pfsshell complete-allocation guard')
