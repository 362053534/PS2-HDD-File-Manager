# Agent rules — PS2-HDD-File-Manager

## Canonical PS2 references (owner 362053534 only)

When implementing, debugging, or compiling anything that touches **Open PS2 Loader (OPL)**, **PS2SDK**, or the **PS2 ELF Docker image**, use **only** repositories and images owned by **362053534**. Treat that fork as the source of truth for behavior (DHCP, config search, IRX stack, UTF-8 path bytes, KERNEL_NOPATCH, etc.).

| Role | Use this | Local checkout (if present) |
|------|----------|-----------------------------|
| OPL | https://github.com/362053534/Open-PS2-Loader | `C:\GitHub\Open-PS2-Loader` |
| PS2SDK | https://github.com/362053534/ps2sdk | `C:\GitHub\ps2sdk` |
| ELF compile image | `ghcr.io/362053534/ps2homebrew:main` | Docker; script default in `ps2-server/scripts/build.ps1` |

Compile `ps2-server` with:

```powershell
.\ps2-server\scripts\build.ps1 -Docker -Image "ghcr.io/362053534/ps2homebrew:main"
```

Do not use official / upstream trees as reference or as a toolchain:

- `github.com/ps2homebrew/Open-PS2-Loader`
- `github.com/ps2dev/ps2sdk`
- `github.com/ps2dev/ps2toolchain*`
- official images such as `ghcr.io/ps2dev/*`

If a local checkout and the 362053534 remote disagree, follow the **362053534** tree. If that checkout is missing, clone or browse that owner — do not fall back to official sources.
