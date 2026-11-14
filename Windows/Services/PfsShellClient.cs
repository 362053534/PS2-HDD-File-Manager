using System.Text;
using System.Text.RegularExpressions;
using PfsExplorerLite.Models;

namespace PfsExplorerLite.Services;

internal sealed record PfsImportRequest(string RemoteDirectory, string LocalFilePath);

internal sealed record PfsBatchImportResult(ProcessResult? Result, bool UsedSharedSession, string? FallbackReason);

internal sealed class PfsShellClient
{
    private static readonly string[] BannerFragments =
    [
        "pfsshell for", "https://", "This program uses", "Type \"help\"", "~Command List~"
    ];

    private readonly string _executablePath;

    public PfsShellClient(string toolsDirectory)
    {
        _executablePath = ResolveExecutablePath(toolsDirectory);
    }

    public bool IsAvailable => File.Exists(_executablePath);

    private static string ResolveExecutablePath(string toolsDirectory)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(toolsDirectory, "pfsshell.exe");

        var unixPath = Path.Combine(toolsDirectory, "pfsshell");
        if (File.Exists(unixPath))
            return unixPath;

        var exePath = Path.Combine(toolsDirectory, "pfsshell.exe");
        return File.Exists(exePath) ? exePath : unixPath;
    }

    public Task<ProcessResult> ListPartitionsAsync(string device, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], "ls", cancellationToken);

    public async Task<ProcessResult> CreatePfsPartitionAsync(string device, string name, string size, CancellationToken cancellationToken)
    {
        var value = size.Trim().ToUpperInvariant();
        if (value.Length < 2 || !long.TryParse(value[..^1], out var count) || count <= 0 || value[^1] is not ('M' or 'G'))
            throw new InvalidOperationException("容量格式为 128M、1G 等。");
        var bytes = checked(count * (value[^1] == 'G' ? 1024L : 1L) * 1048576L);
        var layout = await new ApaPartitionTableReader().ReadAsync(device, cancellationToken);
        ApaPartitionPlanner.RequireComplete(layout, bytes);
        return await RunSessionAsync(device, [], $"mkpart {Quote(name)} {Quote(size)} PFS", cancellationToken);
    }

    public Task<ProcessResult> RemovePartitionAsync(string device, string name, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], $"rmpart {Quote(name)}", cancellationToken);

    public Task<ProcessResult> RenamePartitionAsync(string device, string oldName, string newName, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], $"rename {Quote(oldName)} {Quote(newName)}", cancellationToken);

    public async Task<IReadOnlyList<string>> FilterBrowsablePfsPartitionsAsync(
        string device,
        IReadOnlyList<string> partitions,
        CancellationToken cancellationToken)
    {
        var browsable = new List<string>();
        foreach (var partition in partitions)
        {
            var result = await ListDirectoryAsync(device, partition, "/", cancellationToken);
            if (IsSuccessfulSession(result))
                browsable.Add(partition);
        }

        return browsable;
    }

    public Task<ProcessResult> ListDirectoryAsync(string device, string partition, string remoteDirectory, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), "ls -l", cancellationToken);

    public async Task<ProcessResult> ExportAsync(
        string device,
        string partition,
        string remoteDirectory,
        string fileName,
        string destination,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("The remote file has no name.");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var targetPath = Path.Combine(destination, PfsNameEncoding.ToDisplay(fileName));
        // lcd / get：路径与 PFS 名均为 UTF-8 保真字节。
        var command =
            $"lcd {Quote(PfsNameEncoding.ToLocalShell(destination))}{Environment.NewLine}" +
            $"get {Quote(fileName)}";
        var sessionTask = RunMountedSessionAsync(
            device,
            partition,
            PathParts(remoteDirectory),
            command,
            cancellationToken);
        while (!sessionTask.IsCompleted && progress is not null)
        {
            await Task.WhenAny(sessionTask, Task.Delay(350, cancellationToken));
            if (File.Exists(targetPath))
            {
                try { await progress(new FileInfo(targetPath).Length); }
                catch (IOException) { }
            }
        }
        var result = await sessionTask;
        // 文件已落地则视为成功（规避个别会话收尾异常）。
        if (!File.Exists(targetPath))
        {
            if (!IsSuccessfulSession(result))
                return result;
            return new ProcessResult(
                -1,
                result.StandardOutput,
                string.Join(Environment.NewLine, new[] { result.StandardError, $"Exported file missing: {PfsNameEncoding.ToDisplay(fileName)}" }.Where(text => !string.IsNullOrWhiteSpace(text))));
        }
        if (progress is not null)
            await progress(new FileInfo(targetPath).Length);
        return result;
    }

    public async Task<ProcessResult> ImportAsync(
        string device,
        string partition,
        string remoteDirectory,
        string localFilePath,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null)
    {
        localFilePath = Path.GetFullPath(localFilePath);
        var originalName = Path.GetFileName(localFilePath);
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("The selected file has no name.");

        // 直接 put：本地 UTF-8 绝对路径 + PFS UTF-8 名（pfsshell getline 已修复长命令行）。
        var remoteName = PfsNameEncoding.ToPfsFileName(originalName);
        var command =
            $"put {Quote(PfsNameEncoding.ToLocalShell(localFilePath))} {Quote(PfsNameEncoding.ToShell(remoteName))}";
        return await RunMountedSessionAsync(
            device,
            partition,
            PathParts(remoteDirectory),
            command,
            cancellationToken,
            progress);
    }

    /// <summary>
    /// 将一批文件放入同一个 pfsshell 会话，避免每个文件都重新打开、挂载和卸载 PFS。
    /// </summary>
    public async Task<PfsBatchImportResult> ImportBatchAsync(
        string device,
        string partition,
        IReadOnlyList<PfsImportRequest> requests,
        CancellationToken cancellationToken,
        Func<PfsImportRequest, long, bool, Task>? progress = null)
    {
        if (requests.Count == 0)
            return new PfsBatchImportResult(new ProcessResult(0, string.Empty, string.Empty), true, null);

        var directFiles = new List<DirectImportFile>(requests.Count);
        foreach (var request in requests)
        {
            var localFilePath = Path.GetFullPath(request.LocalFilePath);
            var fileName = Path.GetFileName(localFilePath);
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("The selected file has no name.");

            directFiles.Add(new DirectImportFile(
                request,
                PfsNameEncoding.ToLocalShell(localFilePath),
                PfsNameEncoding.ToShell(PfsNameEncoding.ToPfsFileName(fileName)),
                new FileInfo(localFilePath).Length));
        }

        IReadOnlyList<string> currentPath = Array.Empty<string>();
        var command = new StringBuilder();
        foreach (var file in directFiles)
        {
            var targetPath = PathParts(file.Request.RemoteDirectory);
            AppendPathTransition(command, currentPath, targetPath);
            currentPath = targetPath;
            command.AppendLine($"put {Quote(file.LocalPathShell)} {Quote(file.RemoteNameShell)}");
        }

        var progressIndex = 0;
        var result = await RunSessionCoreAsync(
            device,
            partition,
            [],
            command.ToString(),
            cancellationToken,
            async bytes =>
            {
                if (progressIndex >= directFiles.Count || progress is null)
                    return;

                var file = directFiles[progressIndex];
                var transferred = Math.Clamp(bytes, 0, file.Length);
                var completed = transferred >= file.Length;
                await progress(file.Request, transferred, completed);
                if (completed)
                    progressIndex++;
            });
        return new PfsBatchImportResult(result, true, null);
    }

    // name/oldName 须为 ls 得到的保真字节名；勿再 ToShell，否则乱码名会被二次编码。
    public Task<ProcessResult> DeleteAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rm {Quote(name)}", cancellationToken);

    public Task<ProcessResult> CreateDirectoryAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"mkdir {Quote(PfsNameEncoding.ToShell(PfsNameEncoding.ToPfsFileName(name)))}", cancellationToken);

    public Task<ProcessResult> RemoveDirectoryAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rmdir {Quote(name)}", cancellationToken);

    public Task<ProcessResult> RenameAsync(string device, string partition, string remoteDirectory, string oldName, string newName, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rename {Quote(oldName)} {Quote(PfsNameEncoding.ToShell(PfsNameEncoding.ToPfsFileName(newName)))}", cancellationToken);

    private Task<ProcessResult> RunSessionAsync(string device, IReadOnlyList<string> remotePath, string command, CancellationToken cancellationToken) =>
        RunSessionCoreAsync(device, mountPartition: null, remotePath, command, cancellationToken, progress: null);

    private Task<ProcessResult> RunMountedSessionAsync(
        string device,
        string partition,
        IReadOnlyList<string> remotePath,
        string command,
        CancellationToken cancellationToken,
        Func<long, Task>? progress = null) =>
        RunSessionCoreAsync(device, partition, remotePath, command, cancellationToken, progress);

    private async Task<ProcessResult> RunSessionCoreAsync(
        string device,
        string? mountPartition,
        IReadOnlyList<string> remotePath,
        string command,
        CancellationToken cancellationToken,
        Func<long, Task>? progress)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("pfsshell.exe is missing. Build the project from this repository or place it in the tools folder.", _executablePath);

        var script = new StringBuilder();
        // 镜像路径可能含任意 Unicode：按 UTF-8 字节送给 pfsshell。
        script.AppendLine($"device {Quote(PfsNameEncoding.ToLocalShell(device))}");
        if (mountPartition is not null)
            script.AppendLine($"mount {Quote(mountPartition)}");
        foreach (var part in remotePath)
            script.AppendLine($"cd {Quote(part)}");
        foreach (var line in command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries))
            script.AppendLine(line);
        // 仅实际 mount 过时才需要 umount；对 mkpart/rmpart 执行 umount 会产生无意义的错误输出。
        if (mountPartition is not null)
            script.AppendLine("umount");
        script.AppendLine("exit");
        // Latin-1 保真传输 UTF-8 字节；显示侧由 ToDisplay 再解码。
        return await ProcessRunner.RunAsync(
            _executablePath,
            string.Empty,
            script.ToString(),
            cancellationToken,
            encoding: PfsNameEncoding.Wire,
            progress: progress);
    }

    public static IReadOnlyList<string> ParsePartitionNames(string output)
    {
        var names = new List<string>();
        foreach (var line in UsefulLines(output))
        {
            // 允许空格（如 "PGEN 1.1"）；APA 名最长 32 字节，与 PfsNet fileXio 一致。
            var named = Regex.Match(line, @"(?:^|\s)(?<name>[+_.A-Za-z0-9-][+_. A-Za-z0-9-]{0,30}?)(?:\s+\d+(?:\.\d+)?[KMG]?)?\s+(?:PFS|CFS|HDL|REISER|EXT2(?:SWAP)?|MBR)\b", RegexOptions.IgnoreCase);
            if (named.Success)
            {
                var name = named.Groups["name"].Value.Trim();
                if (name.Length > 0)
                    names.Add(name);
                continue;
            }

            // pfsshell ls：如 "0.12 GiB  PGEN 1.1/"
            var sizeFirst = Regex.Match(line.Trim(), @"^\d+(?:\.\d+)?\s+[KMGT]i?B\s+(?<name>.{1,32}?)/?$", RegexOptions.IgnoreCase);
            if (sizeFirst.Success)
            {
                var name = sizeFirst.Groups["name"].Value.Trim();
                if (name.Length > 0)
                    names.Add(name);
                continue;
            }

            var simpleName = Regex.Match(line.Trim(), @"^(?<name>[+_.A-Za-z0-9-][+_. A-Za-z0-9-]{0,30})/?$");
            if (simpleName.Success)
            {
                var name = simpleName.Groups["name"].Value.Trim();
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
    }

    public static IReadOnlyList<PfsEntry> ParseDirectoryEntries(string output)
    {
        var entries = new List<PfsEntry>();
        foreach (var line in UsefulLines(output))
        {
            var trimmed = PfsNameEncoding.TrimAscii(line);
            var listing = Regex.Match(trimmed, @"^(?<mode>[d-][rwx-]{9})[ \t]+(?<size>\d+)[ \t]+\d{4}-\d{2}-\d{2}[ \t]+\d{2}:\d{2}[ \t]+(?<name>.+?)/*$");
            if (!listing.Success)
                continue;

            var name = listing.Groups["name"].Value.Trim('"').TrimEnd('/');
            if (name.Length == 0 || name is "." or "..")
                continue;

            var isDirectory = listing.Groups["mode"].Value.StartsWith('d');
            _ = long.TryParse(listing.Groups["size"].Value, out var size);
            entries.Add(new PfsEntry(name, isDirectory, trimmed, size));
        }

        return entries
            .Where(entry => !entry.Name.Equals("exit", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsSuccessfulSession(ProcessResult result) =>
        result.ExitCode == 0 && !result.CombinedOutput.Contains("(!)", StringComparison.Ordinal);

    private static IEnumerable<string> UsefulLines(string output) =>
        output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.TrimStart('>', ' '))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !BannerFragments.Any(fragment => line.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Where(line => !line.StartsWith("device ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("mount ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("lcd ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("umount", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> PathParts(string path) =>
        PfsNameEncoding.SplitRemotePath(path);

    // 只生成必要的 cd/cd ..，确保一批文件可以在同一挂载会话中写入不同目录。
    private static void AppendPathTransition(StringBuilder command, IReadOnlyList<string> currentPath, IReadOnlyList<string> targetPath)
    {
        var commonLength = 0;
        while (commonLength < currentPath.Count && commonLength < targetPath.Count &&
               string.Equals(currentPath[commonLength], targetPath[commonLength], StringComparison.Ordinal))
            commonLength++;

        for (var index = currentPath.Count - 1; index >= commonLength; index--)
            command.AppendLine("cd ..");
        for (var index = commonLength; index < targetPath.Count; index++)
            command.AppendLine($"cd {Quote(targetPath[index])}");
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty)}\"";

    private sealed record DirectImportFile(
        PfsImportRequest Request,
        string LocalPathShell,
        string RemoteNameShell,
        long Length);
}
