using System.Diagnostics;

namespace NET.Deploy.Api.Logic.IIS;

public class IISLogic(ILogger<IISLogic> logger)
{
    /// <summary>
    /// Queries IIS site status using appcmd locally.
    /// When the VPS is remote, this should be replaced with SSH execution.
    /// Returns: "Running" | "Stopped" | "Error" | "Unknown"
    /// </summary>
    public async Task<string> GetStatusAsync(string siteName, string serviceType)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            return "Unknown";

        if (serviceType == "WindowsService")
            return await GetWindowsServiceStatusAsync(siteName);

        return await GetIisSiteStatusAsync(siteName);
    }

    private async Task<string> GetWindowsServiceStatusAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start sc.exe.");

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (output.Contains("STATE") && output.Contains("RUNNING"))
                return "Running";

            if (output.Contains("STATE") && (output.Contains("STOPPED") || output.Contains("STOP_PENDING")))
                return "Stopped";

            return "Unknown";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error querying Windows Service status for {Service}", serviceName);
            return "Error";
        }
    }

    private async Task<string> GetIisSiteStatusAsync(string siteName)
    {
        try
        {
            var appcmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"inetsrv\appcmd.exe");

            if (!File.Exists(appcmd))
            {
                logger.LogWarning("appcmd.exe not found – IIS may not be installed locally.");
                return "Unknown";
            }

            var psi = new ProcessStartInfo(appcmd, $"list site \"{siteName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start appcmd.");

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return "Unknown";

            if (output.Contains("Started", StringComparison.OrdinalIgnoreCase))
                return "Running";

            if (output.Contains("Stopped", StringComparison.OrdinalIgnoreCase))
                return "Stopped";

            return "Unknown";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error querying IIS status for site {Site}", siteName);
            return "Error";
        }
    }
}
