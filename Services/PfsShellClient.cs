using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PfsExplorerLite.Models;

namespace PfsExplorerLite.Services;

internal sealed class PfsShellClient
{
    private static readonly string[] BannerFragments =
    [
        "pfsshell for", "https://", "This program uses", "Type \"help\"", "~Command List~"
    ];

    private readonly string _executablePath;

    public PfsShellClient(string toolsDirectory)
    {
        _executablePath = Path.Combine(toolsDirectory, "pfsshell.exe");
    }

    public bool IsAvailable => File.Exists(_executablePath);

    public Task<ProcessResult> ListPartitionsAsync(string device, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], "ls", cancellationToken);

    public Task<ProcessResult> CreatePfsPartitionAsync(string device, string name, string size, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], $"mkpart {Quote(name)} {Quote(size)} PFS", cancellationToken);

    public Task<ProcessResult> RemovePartitionAsync(string device, string name, CancellationToken cancellationToken) =>
        RunSessionAsync(device, [], $"rmpart {Quote(name)}", cancellationToken);

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
        Directory.CreateDirectory(destination);

        // pfsshell 的 get 对中文远程名不可靠：非 ASCII 先临时 rename 为 ASCII，lcd 到同卷 ASCII 目录 get，再 rename 回原名；最后用 .NET 移到目标（可含中文）。
        var stagingRoot = CreateAsciiStagingDirectory("Export", destination);
        var extension = Path.GetExtension(fileName);
        var stagingName = IsAscii(fileName)
            ? fileName
            : "exp_" + Guid.NewGuid().ToString("N")[..8] + (IsAscii(extension) ? extension : ".bin");
        try
        {
            var command = string.Equals(stagingName, fileName, StringComparison.Ordinal)
                ? $"lcd {Quote(stagingRoot)}{Environment.NewLine}get {Quote(stagingName)}"
                : $"lcd {Quote(stagingRoot)}{Environment.NewLine}rename {Quote(fileName)} {Quote(stagingName)}{Environment.NewLine}get {Quote(stagingName)}{Environment.NewLine}rename {Quote(stagingName)} {Quote(fileName)}";
            var exportedPath = Path.Combine(stagingRoot, stagingName);
            var sessionTask = RunMountedSessionAsync(
                device,
                partition,
                PathParts(remoteDirectory),
                command,
                cancellationToken);
            while (!sessionTask.IsCompleted && progress is not null)
            {
                await Task.WhenAny(sessionTask, Task.Delay(350, cancellationToken));
                if (File.Exists(exportedPath))
                {
                    try { await progress(new FileInfo(exportedPath).Length); }
                    catch (IOException) { }
                }
            }
            var result = await sessionTask;
            if (!IsSuccessfulSession(result))
                return result;

            if (!File.Exists(exportedPath))
            {
                return new ProcessResult(
                    -1,
                    result.StandardOutput,
                    string.Join(Environment.NewLine, new[] { result.StandardError, $"Exported file missing: {stagingName}" }.Where(text => !string.IsNullOrWhiteSpace(text))));
            }
            if (progress is not null)
                await progress(new FileInfo(exportedPath).Length);

            // 本地目标名按系统 ACP 解码，便于资源管理器显示；shell 侧仍用保真字节名。
            var targetPath = Path.Combine(destination, PfsNameEncoding.ToDisplay(fileName));
            await Task.Run(() =>
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(exportedPath, targetPath);
            }, cancellationToken);
            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<ProcessResult> ImportAsync(string device, string partition, string remoteDirectory, string localFilePath, CancellationToken cancellationToken)
    {
        localFilePath = Path.GetFullPath(localFilePath);
        var originalName = Path.GetFileName(localFilePath);
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("The selected file has no name.");

        // pfsshell 找不到“进程工作目录”里的中文路径文件；lcd 中文路径也常失败。
        // 做法：同卷 ASCII 目录硬链接（不复制数据）；非 ASCII 名先 put payload 再 rename 回原名。
        var stagingRoot = CreateAsciiStagingDirectory("Import", localFilePath);
        var extension = Path.GetExtension(originalName);
        var stagingName = IsAscii(originalName)
            ? originalName
            : "payload_" + Guid.NewGuid().ToString("N")[..8] + (IsAscii(extension) ? extension : ".bin");
        var stagingPath = Path.Combine(stagingRoot, stagingName);
        try
        {
            await Task.Run(() => LinkOrCopy(localFilePath, stagingPath), cancellationToken);
            // 非 ASCII 本地名：put 仅用 ASCII staging 名，再 rename 为 ACP 字节（经 Latin-1 保真送入 pfsshell）
            var shellName = PfsNameEncoding.ToShell(originalName);
            var command = string.Equals(stagingName, originalName, StringComparison.Ordinal)
                ? $"lcd {Quote(stagingRoot)}{Environment.NewLine}put {Quote(stagingName)}"
                : $"lcd {Quote(stagingRoot)}{Environment.NewLine}put {Quote(stagingName)}{Environment.NewLine}rename {Quote(stagingName)} {Quote(shellName)}";
            return await RunMountedSessionAsync(
                device,
                partition,
                PathParts(remoteDirectory),
                command,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    // name/oldName 须为 ls 得到的保真字节名；勿再 ToShell，否则乱码名会被二次编码。
    public Task<ProcessResult> DeleteAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rm {Quote(name)}", cancellationToken);

    public Task<ProcessResult> CreateDirectoryAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"mkdir {Quote(PfsNameEncoding.ToShell(name))}", cancellationToken);

    public Task<ProcessResult> RemoveDirectoryAsync(string device, string partition, string remoteDirectory, string name, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rmdir {Quote(name)}", cancellationToken);

    public Task<ProcessResult> RenameAsync(string device, string partition, string remoteDirectory, string oldName, string newName, CancellationToken cancellationToken) =>
        RunMountedSessionAsync(device, partition, PathParts(remoteDirectory), $"rename {Quote(oldName)} {Quote(PfsNameEncoding.ToShell(newName))}", cancellationToken);

    private Task<ProcessResult> RunSessionAsync(string device, IReadOnlyList<string> remotePath, string command, CancellationToken cancellationToken) =>
        RunSessionCoreAsync(device, mountPartition: null, remotePath, command, cancellationToken);

    private Task<ProcessResult> RunMountedSessionAsync(
        string device,
        string partition,
        IReadOnlyList<string> remotePath,
        string command,
        CancellationToken cancellationToken) =>
        RunSessionCoreAsync(device, partition, remotePath, command, cancellationToken);

    private async Task<ProcessResult> RunSessionCoreAsync(
        string device,
        string? mountPartition,
        IReadOnlyList<string> remotePath,
        string command,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("pfsshell.exe is missing. Build the project from this repository or place it in the tools folder.", _executablePath);

        var script = new StringBuilder();
        script.AppendLine($"device {Quote(device)}");
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
        // Latin-1 保真传输原始文件名字节；用户可见名称由 PfsNameEncoding.ToDisplay 再按 ACP 解码。
        return await ProcessRunner.RunAsync(
            _executablePath,
            string.Empty,
            script.ToString(),
            cancellationToken,
            encoding: PfsNameEncoding.Wire);
    }

    public static IReadOnlyList<string> ParsePartitionNames(string output)
    {
        var names = new List<string>();
        foreach (var line in UsefulLines(output))
        {
            var named = Regex.Match(line, @"(?:^|\s)(?<name>[+_.A-Za-z0-9-]{1,32})(?:\s+\d+(?:\.\d+)?[KMG]?)?\s+(?:PFS|CFS|HDL|REISER|EXT2(?:SWAP)?|MBR)\b", RegexOptions.IgnoreCase);
            if (named.Success)
            {
                names.Add(named.Groups["name"].Value);
                continue;
            }

            var simpleName = Regex.Match(line.Trim(), @"^(?<name>[+_.A-Za-z0-9-]{1,32})/?$");
            if (simpleName.Success)
                names.Add(simpleName.Groups["name"].Value);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
    }

    public static IReadOnlyList<PfsEntry> ParseDirectoryEntries(string output)
    {
        var entries = new List<PfsEntry>();
        foreach (var line in UsefulLines(output))
        {
            var trimmed = line.Trim();
            var listing = Regex.Match(trimmed, @"^(?<mode>[d-][rwx-]{9})\s+(?<size>\d+)\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(?<name>.+?)/*$");
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
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty)}\"";

    private static bool IsAscii(string value) => value.Length > 0 && value.All(static c => c <= 0x7F);

    private static string CreateAsciiStagingDirectory(string purpose, string? sameVolumePath)
    {
        foreach (var candidate in EnumerateStagingCandidates(purpose, sameVolumePath))
        {
            if (!IsAscii(candidate))
                continue;
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
            }
        }

        throw new IOException("无法创建供 pfsshell 使用的 ASCII 临时目录。");
    }

    private static IEnumerable<string> EnumerateStagingCandidates(string purpose, string? sameVolumePath)
    {
        var id = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrWhiteSpace(sameVolumePath))
        {
            var root = Path.GetPathRoot(Path.GetFullPath(sameVolumePath));
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "PS2HDDFM", purpose, id);
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS2-HDD-File-Manager", purpose, id);
        yield return Path.Combine(Path.GetTempPath(), "PS2HDDFM", purpose, id);
        yield return Path.Combine(AppContext.BaseDirectory, "PS2HDDFM", purpose, id);
    }

    private static void LinkOrCopy(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        // 同卷 NTFS 硬链接不复制文件内容；仅在失败时回退拷贝。
        if (CreateHardLink(destinationPath, sourcePath, IntPtr.Zero))
            return;

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
