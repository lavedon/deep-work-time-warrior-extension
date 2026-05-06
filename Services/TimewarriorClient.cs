using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace DeepWork.Services;

public sealed class TimewarriorClient
{
    public async Task<string> ExportAsync(DateOnly fromInclusive, DateOnly toExclusive, CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo("timew")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        processStartInfo.ArgumentList.Add("export");
        processStartInfo.ArgumentList.Add(fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        processStartInfo.ArgumentList.Add("-");
        processStartInfo.ArgumentList.Add(toExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        try
        {
            using var process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Failed to start timew.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"timew export failed with exit code {process.ExitCode}: {stderr.Trim()}");
            }

            return string.IsNullOrWhiteSpace(stdout) ? "[]" : stdout;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Could not find the 'timew' executable. Install Timewarrior or pass --file path/to/timew-export.json.",
                ex);
        }
    }
}
