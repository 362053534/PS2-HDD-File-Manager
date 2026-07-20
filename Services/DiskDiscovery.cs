using System.Text.RegularExpressions;

namespace PfsExplorerLite.Services;

internal sealed record DiskTarget(string DevicePath, string DisplayName);

internal sealed class DiskDiscovery
{
    private readonly string _executablePath;

    public DiskDiscovery(string toolsDirectory) => _executablePath = Path.Combine(toolsDirectory, "hdl_dump.exe");

    public async Task<IReadOnlyList<DiskTarget>> GetPs2ApaDisksAsync(CancellationToken cancellationToken)
    {
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
                $@"\\.\PhysicalDrive{item.match.Groups["number"].Value}",
                $"PS2 APA HDD · PhysicalDrive{item.match.Groups["number"].Value} · {item.line.Trim()}"))
            .DistinctBy(disk => disk.DevicePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(disk => disk.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
