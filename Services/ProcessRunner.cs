using System.Diagnostics;
using System.Text;

namespace PfsExplorerLite.Services;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? input = null,
        CancellationToken cancellationToken = default,
        Encoding? encoding = null,
        string? workingDirectory = null,
        Func<long, Task>? progress = null)
    {
        // 默认 UTF-8 无 BOM，供 hdl_dump / wnbd 等工具；pfsshell 需传入系统 ANSI 编码。
        var textEncoding = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = textEncoding,
            StandardErrorEncoding = textEncoding
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        // ProcessStartInfo rejects StandardInputEncoding unless stdin is redirected.
        // Disk discovery has no stdin; pfsshell sessions do.
        if (input is not null)
            startInfo.StandardInputEncoding = textEncoding;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(fileName)}.");

        var outputTask = progress is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : ReadOutputAsync(process.StandardOutput, cancellationToken, progress);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            // 按指定代码页写原始字节，避免 StreamWriter 在部分环境下对换行/编码处理不一致。
            var bytes = textEncoding.GetBytes(input);
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        Func<long, Task>? progress)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (progress is not null && TryParseProgress(line, out var bytes))
            {
                await progress(bytes);
                continue;
            }

            output.AppendLine(line);
        }

        return output.ToString();
    }

    private static bool TryParseProgress(string line, out long bytes)
    {
        const string prefix = "__PFS_PROGRESS__:";
        bytes = 0;
        if (!line.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(line[prefix.Length..], out var parsed) || parsed < 0)
            return false;

        bytes = parsed;
        return true;
    }
}

