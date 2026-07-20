namespace PfsExplorerLite.Services;

internal sealed class NbdClient
{
    private readonly string _executablePath;

    public NbdClient(string toolsDirectory) => _executablePath = Path.Combine(toolsDirectory, "wnbd-client.exe");

    public bool IsAvailable => File.Exists(_executablePath);

    public Task<ProcessResult> MapAsync(string ipAddress, CancellationToken cancellationToken) =>
        RunAsync($"map PS2HDD {ipAddress}", cancellationToken);

    public Task<ProcessResult> UnmapAsync(CancellationToken cancellationToken) =>
        RunAsync("unmap PS2HDD", cancellationToken);

    private Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("wnbd-client.exe is missing.", _executablePath);
        return ProcessRunner.RunAsync(_executablePath, arguments, cancellationToken: cancellationToken);
    }
}
