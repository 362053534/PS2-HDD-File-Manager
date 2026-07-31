EE_BIN = PfsNetServer.elf

EE_OBJS = main.o net.o netcfg.o server.o fsops.o \
	IOMANX_irx.o FILEXIO_irx.o POWEROFF_irx.o DEV9_irx.o ATAD_irx.o HDD_irx.o PFS_irx.o \
	SIO2MAN_irx.o MCMAN_irx.o MCSERV_irx.o \
	NETMAN_irx.o SMAP_irx.o

EE_INCS := -I./include -I$(PS2SDK)/ports/include -I$(PS2SDK)/ee/include -I$(PS2SDK)/common/include
EE_CFLAGS += -Os -DNEWLIB_PORT_AWARE -D_EE
EE_LDFLAGS := -L$(PS2SDK)/ee/lib -L$(PS2SDK)/ports/lib
EE_LIBS = -lpatches -lfileXio -ldebug -lps2ip -lnetman -lcdvd -lkernel -lc

EE_TEMP_FILES = IOMANX_irx.c FILEXIO_irx.c POWEROFF_irx.c DEV9_irx.c ATAD_irx.c HDD_irx.c PFS_irx.c \
	SIO2MAN_irx.c MCMAN_irx.c MCSERV_irx.c NETMAN_irx.c SMAP_irx.c

# 默认指向本机魔改 PS2SDK 源码树（需先 make install 或已有 iop/irx）
PS2SDK ?= /mnt/c/GitHub/ps2sdk

VPATH = src

%.o : %.c
	$(EE_CC) $(EE_CFLAGS) $(EE_INCS) -c $< -o $@

$(EE_BIN) : $(EE_OBJS)
	$(EE_CC) $(EE_CFLAGS) $(EE_LDFLAGS) -o $(EE_BIN) $(EE_OBJS) $(EE_LIBS)

all: $(EE_BIN)

clean:
	rm -f $(EE_BIN) $(EE_OBJS) $(EE_TEMP_FILES)

IOMANX_irx.c:
	bin2c $(PS2SDK)/iop/irx/iomanX.irx IOMANX_irx.c IOMANX_irx

FILEXIO_irx.c:
	bin2c $(PS2SDK)/iop/irx/fileXio.irx FILEXIO_irx.c FILEXIO_irx

POWEROFF_irx.c:
	bin2c $(PS2SDK)/iop/irx/poweroff.irx POWEROFF_irx.c POWEROFF_irx

DEV9_irx.c:
	bin2c $(PS2SDK)/iop/irx/ps2dev9.irx DEV9_irx.c DEV9_irx

ATAD_irx.c:
	bin2c $(PS2SDK)/iop/irx/ps2atad.irx ATAD_irx.c ATAD_irx

HDD_irx.c:
	bin2c $(PS2SDK)/iop/irx/ps2hdd.irx HDD_irx.c HDD_irx

PFS_irx.c:
	bin2c $(PS2SDK)/iop/irx/ps2fs.irx PFS_irx.c PFS_irx

SIO2MAN_irx.c:
	bin2c $(PS2SDK)/iop/irx/sio2man.irx SIO2MAN_irx.c SIO2MAN_irx

MCMAN_irx.c:
	bin2c $(PS2SDK)/iop/irx/mcman.irx MCMAN_irx.c MCMAN_irx

MCSERV_irx.c:
	bin2c $(PS2SDK)/iop/irx/mcserv.irx MCSERV_irx.c MCSERV_irx

NETMAN_irx.c:
	bin2c $(PS2SDK)/iop/irx/netman.irx NETMAN_irx.c NETMAN_irx

SMAP_irx.c:
	bin2c $(PS2SDK)/iop/irx/smap.irx SMAP_irx.c SMAP_irx

include $(PS2SDK)/samples/Makefile.pref

.PHONY: all clean
