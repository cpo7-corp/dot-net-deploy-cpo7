using System.Diagnostics;

namespace NET.Deploy.Api.Logic.Deploy;

public class ProcessRunner
{
    public async Task<bool> RunAsync(string command, string arguments, string workingDir, LogCallback log, string? serviceId)
    {
        var psi = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command} {arguments}");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await outputTask;
        var stderr = await errorTask;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            await log("INFO", line.TrimEnd(), serviceId);

        if (process.ExitCode != 0)
        {
            foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await log("ERROR", line.TrimEnd(), serviceId);
            return false;
        }

        return true;
    }
}
