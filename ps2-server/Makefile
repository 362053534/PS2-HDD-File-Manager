EE_BIN = PfsNetServer-debug.elf
EE_BIN_STRIPPED = PfsNetServer-stripped.elf
EE_BIN_PACKED = PfsNetServer.elf

EE_OBJS = main.o net.o netcfg.o server.o fsops.o \
	IOMANX_irx.o FILEXIO_irx.o POWEROFF_irx.o DEV9_irx.o ATAD_irx.o HDD_irx.o PFS_irx.o \
	SIO2MAN_irx.o MCMAN_irx.o MCSERV_irx.o \
	BDM_irx.o BDMFS_FATFS_irx.o USBD_irx.o USBMASS_BD_irx.o \
	NETMAN_irx.o SMAP_irx.o PADMAN_irx.o

EE_INCS := -I./include -I$(PS2SDK)/ports/include -I$(PS2SDK)/ee/include -I$(PS2SDK)/common/include
# 对齐 HDDTester：DUMMY_* / KERNEL_NOPATCH + gc-sections；勿链 -lcdvd / ps2time
EE_CFLAGS += -Os -G0 -fdata-sections -ffunction-sections -DNEWLIB_PORT_AWARE \
	-DDUMMY_TIMEZONE -DDUMMY_LIBC_INIT -DKERNEL_NOPATCH -D_EE
EE_LDFLAGS := -L$(PS2SDK)/ee/lib -L$(PS2SDK)/ports/lib -Wl,--gc-sections \
	-Wl,--wrap=sys_now \
	-Wl,--wrap=memp_init \
	-Wl,--wrap=pbuf_alloc \
	-Wl,--wrap=netconn_recv_tcp_pbuf \
	-Wl,--wrap=SifSetDma \
	-Wl,--wrap=SifSendCmd \
	-Wl,--wrap=iSifSendCmd \
	-Wl,--wrap=DelayThread \
	-Wl,--wrap=NetManInitRPCServer \
	-Wl,--wrap=NetManDeinitRPCServer \
	-Wl,--wrap=NetManRPCAllocRxBuffers \
	-Wl,--wrap=__retarget_lock_acquire_recursive \
	-Wl,--wrap=__retarget_lock_release_recursive \
	-Wl,--wrap=sys_init \
	-Wl,--wrap=sys_mbox_new \
	-Wl,--wrap=sys_mbox_free \
	-Wl,--wrap=sys_mbox_post \
	-Wl,--wrap=sys_mbox_trypost \
	-Wl,--wrap=sys_arch_mbox_fetch \
	-Wl,--wrap=sys_arch_mbox_tryfetch \
	-Wl,--wrap=sys_arch_sem_wait \
	-T$(PS2SDK)/ee/startup/linkfile
EE_LIBS = -lpatches -lkernel -ldebug -lfileXio -lps2ip -lnetman -lpad

EE_TEMP_FILES = IOMANX_irx.c FILEXIO_irx.c POWEROFF_irx.c DEV9_irx.c ATAD_irx.c HDD_irx.c PFS_irx.c \
	SIO2MAN_irx.c MCMAN_irx.c MCSERV_irx.c \
	BDM_irx.c BDMFS_FATFS_irx.c USBD_irx.c USBMASS_BD_irx.c \
	NETMAN_irx.c SMAP_irx.c PADMAN_irx.c

# 默认指向本机魔改 PS2SDK 源码树（需先 make install 或已有 iop/irx）
PS2SDK ?= /mnt/c/GitHub/ps2sdk

VPATH = src

%.o : %.c
	$(EE_CC) $(EE_CFLAGS) $(EE_INCS) -c $< -o $@

$(EE_BIN) : $(EE_OBJS)
	$(EE_CC) $(EE_CFLAGS) $(EE_LDFLAGS) -o $(EE_BIN) $(EE_OBJS) $(EE_LIBS)

all: $(EE_BIN_PACKED)

# 保留原始 ELF 供定位问题；发布产物使用与 362053534 OPL 相同的处理顺序。
$(EE_BIN_STRIPPED): $(EE_BIN)
	$(EE_STRIP) -o $@ $<

$(EE_BIN_PACKED): $(EE_BIN_STRIPPED) elf-ident.txt
	ps2-packer $< $@
	# packer 不生成节表；在加载段以外的文件尾保留标识，供 strings/CI 识别。
	printf '\n' >> $@
	cat elf-ident.txt >> $@

release: $(EE_BIN_PACKED)

.INTERMEDIATE: $(EE_BIN_STRIPPED)

clean:
	rm -f $(EE_BIN) $(EE_BIN_STRIPPED) $(EE_BIN_PACKED) $(EE_OBJS) $(EE_TEMP_FILES)

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

BDM_irx.c:
	bin2c $(PS2SDK)/iop/irx/bdm.irx BDM_irx.c BDM_irx

BDMFS_FATFS_irx.c:
	bin2c $(PS2SDK)/iop/irx/bdmfs_fatfs.irx BDMFS_FATFS_irx.c BDMFS_FATFS_irx

USBD_irx.c:
	bin2c $(PS2SDK)/iop/irx/usbd.irx USBD_irx.c USBD_irx

USBMASS_BD_irx.c:
	bin2c $(PS2SDK)/iop/irx/usbmass_bd.irx USBMASS_BD_irx.c USBMASS_BD_irx

NETMAN_irx.c:
	bin2c $(PS2SDK)/iop/irx/netman.irx NETMAN_irx.c NETMAN_irx

SMAP_irx.c:
	bin2c $(PS2SDK)/iop/irx/smap.irx SMAP_irx.c SMAP_irx

PADMAN_irx.c:
	bin2c $(PS2SDK)/iop/irx/padman.irx PADMAN_irx.c PADMAN_irx

include $(PS2SDK)/samples/Makefile.pref

.PHONY: all clean release
