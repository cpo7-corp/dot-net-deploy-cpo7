using System.Diagnostics;

namespace NET.Deploy.Api.Logic.Deploy;

public class ProcessRunner
{
    public async Task<bool> RunAsync(string command, string arguments, string workingDir, LogCallback log, string? serviceId, System.Threading.CancellationToken ct = default)
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
        
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

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

    public async Task KillProcessesInDirectory(string directory, LogCallback log, string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var processes = Process.GetProcesses();
        int killedCount = 0;

        foreach (var proc in processes)
        {
            try
            {
                if (proc.Id == Process.GetCurrentProcess().Id) continue;

                string? procPath = null;
                try
                {
                    procPath = proc.MainModule?.FileName;
                }
                catch { /* Access Denied or process exited */ }

                if (!string.IsNullOrEmpty(procPath) && procPath.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    await log("WARNING", $"⚠️ Process {proc.ProcessName} (PID {proc.Id}) is holding files in target directory. Terminating...", serviceId);
                    proc.Kill(true);
                    killedCount++;
                }
            }
            catch { /* Ignore errors for individual processes */ }
        }

        if (killedCount > 0)
        {
            await log("INFO", $"✅ Terminated {killedCount} processes holding files in {directory}", serviceId);
            await Task.Delay(1000); // Give some time for OS to release locks
        }
    }
}
