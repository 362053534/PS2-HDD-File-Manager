#!/usr/bin/env python3
"""Exercise the actual server creation routine with native, deterministic I/O stubs."""
from pathlib import Path
import os,subprocess,tempfile
root=Path(__file__).resolve().parents[1]
source=(root/'src/fsops.c').read_text()
start=source.index('static int createApaPfsPartition(')
end=source.index('\nstatic int handleApaCreatePfs(',start)
function=source[start:end]
arrays=source[source.index('static const char *const g_apaSizeStr'):source.index('static int parseSizeToMb')]
prelude=r'''
#include <stdint.h>
#include <errno.h>
#include <stdio.h>
#include <string.h>
typedef uint32_t u32;
#define APA_IDMAX 32
#define FIO_O_RDONLY 1
#define FIO_O_RDWR 2
#define FIO_O_CREAT 4
#define HDIOC_MAXSECTOR 10
#define HIOCADDSUB 11
#define PFSNET_OK 0
#define PFSNET_ERR_INVAL -2
#define PFSNET_ERR_EXIST -3
#define PFSNET_ERR_NOSPC -4
static int formats, removes, creates, close_calls, fail_mode, remove_failure;
static int isSystemPartitionName(const char *s) { return 0; }
static int mapErrno(int e) { return e; }
static int fileXioDevctl(const char*a,int b,void*c,int d,void*e,int f) { return fail_mode==3 ? -EIO : 0x2000000; }
static int fileXioOpen(const char *p,int flags,int mode) {
 if(flags==FIO_O_RDONLY) return -ENOENT;
 creates++; return 3;
}
static int fileXioClose(int fd) { close_calls++; return 0; }
static int fileXioIoctl2(int a,int b,void*c,int d,void*e,int f) { return fail_mode==2 ? -ENOSPC : -EFBIG; }
static int fileXioRemove(const char *s) { removes++; return remove_failure ? -EIO : 0; }
static int formatPfsPartition(const char *s) { formats++; return 0; }
static void reset(int mode) {formats=removes=creates=close_calls=0;fail_mode=mode;remove_failure=0;}
'''
tests=r'''
int main(void) {
 int errors=0, rv;
 reset(0); rv=createApaPfsPartition("+OPL",128);
 if(rv!=0 || formats!=1 || removes!=0) { puts("FAIL complete allocation should format"); errors++; }
 reset(1); rv=createApaPfsPartition("+OPL",32768);
 if(rv==0 || formats!=0 || removes!=1) { printf("FAIL EFBIG shortfall: result=%d formats=%d removals=%d\n",rv,formats,removes); errors++; }
 reset(2); rv=createApaPfsPartition("+OPL",32768);
 if(rv==0 || formats!=0 || removes!=1) { printf("FAIL ENOSPC shortfall: result=%d formats=%d removals=%d\n",rv,formats,removes); errors++; }
 reset(1); remove_failure=1; rv=createApaPfsPartition("+OPL",32768);
 if(rv==0 || formats!=0) { puts("FAIL cleanup failure must not format or succeed"); errors++; }
 if(errors==0) puts("PASS server complete allocation formats; EFBIG/ENOSPC/cleanup failure never format or succeed");
 return errors ? 1 : 0;
}
'''
with tempfile.TemporaryDirectory(prefix='apa-create-server-') as folder:
 p=Path(folder); (p/'check.c').write_text(prelude+arrays+function+tests)
 subprocess.run([os.environ.get('CC','cc'),'-std=c99','-Werror=implicit-function-declaration',str(p/'check.c'),'-o',str(p/'check')],check=True)
 subprocess.run([str(p/'check')],check=True)
