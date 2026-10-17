using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace PfsExplorerLite.Services;

internal sealed record DiskTarget(string DevicePath, string DisplayName);

internal sealed class DiskDiscovery
{
    private const uint ApaMagic = 0x00415041;
    private readonly string _executablePath;

    public DiskDiscovery(string toolsDirectory) =>
        _executablePath = Path.Combine(toolsDirectory, OperatingSystem.IsWindows() ? "hdl_dump.exe" : "hdl_dump");

    public async Task<IReadOnlyList<DiskTarget>> GetPs2ApaDisksAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !File.Exists(_executablePath))
            return await Task.Run(() => ProbePosixApaDisks(cancellationToken), cancellationToken);

        if (!File.Exists(_executablePath))
            throw new FileNotFoundException("hdl_dump.exe is missing.", _executablePath);

        var result = await ProcessRunner.RunAsync(_executablePath, "query", cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.CombinedOutput);

        return result.StandardOutput.Replace("\r", string.Empty).Split('\n')
            .Where(line => line.Contains("Playstation 2 HDD", StringComparison.OrdinalIgnoreCase))
            .Select(line => (line, match: Regex.Match(line, @"\bhdd(?<number>\d+):", RegexOptions.IgnoreCase)))
            .Where(item => item.match.Success)
            .Select(item => new DiskTarget(
                OperatingSystem.IsWindows()
                    ? $@"\\.\PhysicalDrive{item.match.Groups["number"].Value}"
                    : $"/dev/disk{item.match.Groups["number"].Value}",
                OperatingSystem.IsWindows()
                    ? $"PS2 APA HDD · PhysicalDrive{item.match.Groups["number"].Value} · {item.line.Trim()}"
                    : $"PS2 APA HDD · disk{item.match.Groups["number"].Value} · {item.line.Trim()}"))
            .DistinctBy(disk => disk.DevicePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(disk => disk.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<DiskTarget> ProbePosixApaDisks(CancellationToken cancellationToken)
    {
        var results = new List<DiskTarget>();
        if (!Directory.Exists("/dev"))
            return results;

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles("/dev", "disk*")
                .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^disk\d+$"))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return results;
        }

        Span<byte> header = stackalloc byte[512];
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var read = stream.Read(header);
                if (read == header.Length && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) == ApaMagic)
                    results.Add(new DiskTarget(path, $"PS2 APA HDD · {Path.GetFileName(path)} ({path})"));
            }
            catch
            {
                // 无权限或非 APA 盘时忽略
            }
        }

        return results;
    }
}
