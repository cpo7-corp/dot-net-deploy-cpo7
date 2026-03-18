using System.Diagnostics;

namespace NET.Deploy.Api.Logic.Deploy;

public class ProcessRunner
{
    public async Task<bool> RunAsync(string command, string arguments, string workingDir, LogCallback log, string? serviceId)
    {
        var resolvedCommand = ExeResolver.Resolve(command);
        var psi = new ProcessStartInfo(resolvedCommand, arguments)
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
        {
            var trimmed = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            
            // Filter noise: technical build/restore logs that clutter the UI
            var lower = trimmed.ToLower();
            if (lower.Contains(": warning ")) continue;
            if (lower.Contains("restored ") && (lower.Contains(".csproj") || lower.Contains(".sln"))) continue;
            if (lower.Contains("restore completed in")) continue;
            if (lower.Contains(" -> ") && (lower.Contains(@"\bin\") || lower.Contains(@"/bin/"))) continue;
            if (trimmed == "[]" || trimmed == "[ ]") continue;
            if (lower.Contains("determineprojectstorestore")) continue;
            if (lower.Contains("restore complete")) continue;
            if (lower.Contains("determining projects to restore")) continue;
            if (lower.Contains("msvcrt.lib")) continue; // common noise in C++ or odd builds
            if (trimmed.Length < 2 && !char.IsLetterOrDigit(trimmed[0])) continue; // Single character symbols

            await log("INFO", trimmed, serviceId);
        }

        if (process.ExitCode != 0)
        {
            foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await log("ERROR", line.TrimEnd(), serviceId);
            return false;
        }

        return true;
    }
}
