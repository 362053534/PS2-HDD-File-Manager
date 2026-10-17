using System.Text;
using Avalonia;
using PfsExplorerLite.Services;
using PfsExplorerLite.Services.PfsNet;

namespace PfsExplorerLite;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--run-tests")
            return RunSelfTests();

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunSelfTests()
    {
        var utf8 = new UTF8Encoding(false);
        var latin1 = Encoding.Latin1;
        var failed = 0;

        void Expect(bool ok, string name)
        {
            Console.WriteLine(ok ? $"PASS {name}" : $"FAIL {name}");
            if (!ok)
                failed++;
        }

        const string unicode = "RPG-真说侍魂-武士道列传";
        var wire = latin1.GetString(utf8.GetBytes(unicode));
        Expect(wire[^1] == '\u00A0', "wire name ends with UTF-8 0xA0 as Latin-1 NBSP");
        Expect(PfsNameEncoding.ToDisplay(wire) == unicode, "ToDisplay restores 列传");

        var lsLine = $"drwxrwxrwx        512  2026-09-04 22:07  {wire}/";
        var entries = PfsShellClient.ParseDirectoryEntries(lsLine);
        Expect(entries.Count == 1, "parse one directory");
        Expect(entries[0].Name[^1] == '\u00A0', "parser keeps trailing 0xA0");
        Expect(PfsNameEncoding.ToDisplay(entries[0].Name) == unicode, "parsed name displays as 列传");

        var currentPath = "/POPS/" + entries[0].Name;
        var oldTrim = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Expect(oldTrim[^1][^1] != '\u00A0', "old TrimEntries still reproduces the bug");
        Expect(PfsNameEncoding.ToDisplay(oldTrim[^1]) != unicode, "TrimEntries corrupts 传");

        var parts = PfsNameEncoding.SplitRemotePath(currentPath);
        Expect(parts.Count == 2, "split two path parts");
        Expect(parts[^1][^1] == '\u00A0', "SplitRemotePath keeps trailing 0xA0");
        Expect(PfsNameEncoding.ToDisplay(parts[^1]) == unicode, "cd path still decodes to 列传");
        Expect(PfsNameEncoding.ToDisplay(PfsNameEncoding.ToShell(unicode)) == unicode, "ToShell roundtrip");

        var ascii = PfsNameEncoding.SplitRemotePath("/POPS/ /FTG-TQ3/");
        Expect(ascii.SequenceEqual(["POPS", "FTG-TQ3"]), "ASCII spaces around parts still trimmed");

        var illegal = DiskSafeEject.FormatEjectFailure(0, 0x17, 8, @"SCSI\Disk&Ven_WDC&Prod_WD5000AZLX-60K2T\4&2ce709f8&0&060000");
        Expect(illegal.Contains("不支持弹出", StringComparison.Ordinal), "VetoType 8 is illegal eject, not 'file in use'");
        Expect(!illegal.Contains("其它程序占用", StringComparison.Ordinal), "do not blame other programs for VetoType 8");
        var open = DiskSafeEject.FormatEjectFailure(0, 0x17, 5, "explorer.exe");
        Expect(open.Contains("仍打开", StringComparison.Ordinal), "VetoType 5 mentions open handles");
        var ready = DiskSafeEject.NonHotPlugReadyMessage(0);
        Expect(ready.Contains("已可安全移除", StringComparison.Ordinal), "non-hotplug disks get a short safe-to-remove hint");
        Expect(!ready.Contains("断电", StringComparison.Ordinal), "safe-to-remove hint does not mention power cables");

        var popsErr = PfsNetProtocol.FormatCommandError(PfsNetProtocol.CmdApaRemove, PfsNetProtocol.ErrIo, "__.POPS");
        Expect(popsErr.Contains("__.POPS", StringComparison.Ordinal), "PfsNet delete error names the partition");
        Expect(popsErr.Contains("禁止删除以 __ 开头", StringComparison.Ordinal), "PfsNet delete error explains APA __ protection");
        var genericErr = PfsNetProtocol.FormatCommandError(PfsNetProtocol.CmdPing, PfsNetProtocol.ErrBusy);
        Expect(genericErr.Contains("result=-9", StringComparison.Ordinal), "generic PfsNet errors still include result code");

        Expect(DiskSafeEject.TryParseMacDiskPath("/dev/disk2", out var macDisk) && macDisk == "/dev/disk2", "TryParseMacDiskPath parses /dev/disk2");
        Expect(DiskSafeEject.TryParseMacDiskPath("/dev/rdisk3s1", out var macRawDisk) && macRawDisk == "/dev/disk3", "TryParseMacDiskPath normalizes /dev/rdisk3s1");
        Expect(!DiskSafeEject.TryParseMacDiskPath("/Users/test/ps2.img", out _), "TryParseMacDiskPath rejects image path");

        return failed;
    }
}
