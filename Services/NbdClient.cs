namespace PfsExplorerLite.Services;

using System.Diagnostics;
using System.Text;

internal sealed class NbdClient : IDisposable
{
    private const string InstanceName = "hdd0";
    private const int DefaultPort = 10809;

    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private Process? _mapProcess;
    private Task<string>? _mapOutputTask;
    private Task<string>? _mapErrorTask;

    public NbdClient(string toolsDirectory)
    {
        _workingDirectory = toolsDirectory;
        _executablePath = Path.Combine(toolsDirectory, "wnbd-client.exe");
    }

    public bool IsAvailable => File.Exists(_executablePath);

    public string GetMapCommandDescription(string ipAddress) =>
        $"\"{_executablePath}\" map {InstanceName} {ipAddress} --port {DefaultPort} --export-name {InstanceName}";

    public async Task<ProcessResult> MapAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("wnbd-client.exe is missing.", _executablePath);

        if (_mapProcess is { HasExited: false })
            return new ProcessResult(0, "WNBD 映射进程已在运行。", string.Empty);

        DisposeExitedMapProcess();

        var commandDescription = GetMapCommandDescription(ipAddress);
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = _workingDirectory
        };
        startInfo.ArgumentList.Add("map");
        startInfo.ArgumentList.Add(InstanceName);
        startInfo.ArgumentList.Add(ipAddress);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(DefaultPort.ToString());
        startInfo.ArgumentList.Add("--export-name");
        startInfo.ArgumentList.Add(InstanceName);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Unable to start wnbd-client.exe.");

        _mapProcess = process;
        _mapOutputTask = process.StandardOutput.ReadToEndAsync();
        _mapErrorTask = process.StandardError.ReadToEndAsync();

        // 成功的 map 命令会持续运行，为 WNBD 提供 I/O 服务；不能等待它退出，否则 GUI 会无响应。
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        if (!process.HasExited)
            return new ProcessResult(0, $"NBD 命令：{commandDescription}{Environment.NewLine}WNBD 映射进程已启动。", string.Empty);

        return await CollectExitedMapResultAsync(process, commandDescription);
    }

    public async Task<ProcessResult> UnmapAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync($"unmap {InstanceName}", cancellationToken);
        if (_mapProcess is { HasExited: false } process)
        {
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TimeoutException) { }
        }
        DisposeExitedMapProcess();
        return result;
    }

    public async Task<int> GetMappedDiskNumberAsync(CancellationToken cancellationToken)
    {
        ProcessResult? latestResult = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            latestResult = await RunAsync("list", cancellationToken);
            if (latestResult.ExitCode != 0)
                throw new InvalidOperationException($"无法读取 WNBD 映射列表：{latestResult.CombinedOutput}");

            var diskNumber = ParseMappedDiskNumber(latestResult.StandardOutput);
            if (diskNumber is not null)
                return diskNumber.Value;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new InvalidOperationException(
            $"WNBD 映射进程已启动，但未找到实例 {InstanceName} 对应的磁盘号。{Environment.NewLine}{latestResult?.CombinedOutput}");
    }

    public void Dispose()
    {
        if (_mapProcess is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }
        _mapProcess?.Dispose();
        _mapProcess = null;
    }

    private async Task<ProcessResult> CollectExitedMapResultAsync(Process process, string commandDescription)
    {
        var output = _mapOutputTask is null ? string.Empty : await _mapOutputTask;
        var error = _mapErrorTask is null ? string.Empty : await _mapErrorTask;
        return new ProcessResult(process.ExitCode, $"NBD 命令：{commandDescription}{Environment.NewLine}{output}", error);
    }

    private void DisposeExitedMapProcess()
    {
        if (_mapProcess is null || !_mapProcess.HasExited) return;
        _mapProcess.Dispose();
        _mapProcess = null;
        _mapOutputTask = null;
        _mapErrorTask = null;
    }

    private static int? ParseMappedDiskNumber(string output)
    {
        foreach (var line in output.Replace("\r", string.Empty).Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[^1].Equals(InstanceName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(fields[1], out var diskNumber))
                return diskNumber;
        }

        return null;
    }

    private Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("wnbd-client.exe is missing.", _executablePath);
        return ProcessRunner.RunAsync(_executablePath, arguments, cancellationToken: cancellationToken, workingDirectory: _workingDirectory);
    }
}
