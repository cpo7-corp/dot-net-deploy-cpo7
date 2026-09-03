using System.Text;
using NET.Deploy.Api.Data.Entities;
using Renci.SshNet;

namespace NET.Deploy.Api.Logic.Deploy;

internal static class IisDeployment
{
    // Serialize IIS configuration changes made by parallel deployments.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static string TargetPath(string? configuredPath, string? siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName) || siteName is "." or ".." ||
            siteName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 ||
            siteName.Any(char.IsControl) || siteName.EndsWith('.') || siteName.EndsWith(' '))
            throw new InvalidOperationException("A valid IIS site name is required for the deployment folder.");

        var path = configuredPath?.Trim().Replace('/', '\\').TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(path)) return @"C:\inetpub\wwwroot\" + siteName;
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':' && configuredPath!.EndsWith('\\'))
            return path + "\\";
        var separator = path.LastIndexOf('\\');
        if (separator < 0 || !(path.StartsWith(@"\\") || (path.Length >= 3 && path[1] == ':' && path[2] == '\\')))
            throw new InvalidOperationException("The IIS deployment path must be an absolute Windows path.");
        return path;
    }

    internal static string Script(string siteName, string action, string? targetPath, string? defaultBasePath = null, int? port = null)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        if (action is not ("ensure" or "start" or "stop" or "recycle"))
            throw new ArgumentException("Unsupported IIS action.", nameof(action));

        return $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
                $name = {{Literal(siteName)}}
                $action = {{Literal(action)}}
                $target = {{Literal(targetPath ?? "")}}
                $defaultBase = {{Literal(defaultBasePath ?? "")}}
                $port = {{port ?? 80}}
                $manager = New-Object Microsoft.Web.Administration.ServerManager
                try {
                    $site = $manager.Sites[$name]
                    if ($action -eq 'ensure') {
                        if ($null -ne $site) {
                            $target = [Environment]::ExpandEnvironmentVariables($site.Applications['/'].VirtualDirectories['/'].PhysicalPath)
                        } elseif ([string]::IsNullOrWhiteSpace($target)) {
                                if ([string]::IsNullOrWhiteSpace($defaultBase)) { $defaultBase = 'C:\inetpub\wwwroot' }
                                $defaultBase = [Environment]::ExpandEnvironmentVariables($defaultBase).Replace('/', '\')
                                if ($defaultBase -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') {
                                    throw 'The default IIS folder must be an absolute Windows path.'
                                }
                                $target = [System.IO.Path]::Combine($defaultBase, $name)
                        }
                        if ([string]::IsNullOrWhiteSpace($target)) { throw 'The IIS site has no physical path.' }
                        [System.IO.Directory]::CreateDirectory($target) | Out-Null
                        if ($null -eq $site) {
                            if ([Uri]::CheckHostName($name) -eq [UriHostNameType]::Unknown) {
                                throw 'For automatic creation, the IIS site name must be a valid host name.'
                            }
                            if ($port -lt 1 -or $port -gt 65535) { throw 'IIS port must be between 1 and 65535.' }
                            $binding = '*:' + $port + ':' + $name
                            foreach ($otherSite in $manager.Sites) {
                                foreach ($existingBinding in $otherSite.Bindings) {
                                    if ($existingBinding.Protocol -eq 'http' -and $existingBinding.BindingInformation -eq $binding) {
                                        throw "HTTP binding $binding is already assigned to another site."
                                    }
                                }
                            }
                            $pool = $manager.ApplicationPools[$name]
                            if ($null -eq $pool) {
                                $pool = $manager.ApplicationPools.Add($name)
                                $pool.ManagedRuntimeVersion = ''
                            }
                            $site = $manager.Sites.Add($name, 'http', $binding, $target)
                            $site.Applications['/'].ApplicationPoolName = $name
                            $site.ServerAutoStart = $false
                            Write-Output "Creating IIS site '$name' at '$target' with HTTP binding '$binding'."
                        } else {
                            $site.Applications['/'].VirtualDirectories['/'].PhysicalPath = $target
                            Write-Output "IIS site '$name' uses '$target'."
                        }
                        $manager.CommitChanges()
                        Write-Output ('NET_DEPLOY_TARGET:' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($target)))
                    } else {
                        if ($null -eq $site) { throw "IIS site '$name' does not exist." }
                        $poolName = $site.Applications['/'].ApplicationPoolName
                        $pool = $manager.ApplicationPools[$poolName]
                        if ($null -eq $pool) { throw "Application pool '$poolName' does not exist." }
                        switch ($action) {
                            'stop' {
                                if ($site.State -ne 'Stopped') { $site.Stop() | Out-Null }
                                if ($pool.State -ne 'Stopped') { $pool.Stop() | Out-Null }
                            }
                            'start' {
                                if ($pool.State -ne 'Started') { $pool.Start() | Out-Null }
                                if ($site.State -ne 'Started') { $site.Start() | Out-Null }
                                if ($site.State -ne 'Started' -or $pool.State -ne 'Started') {
                                    throw "IIS site '$name' or its application pool did not start."
                                }
                                $site.ServerAutoStart = $true
                                $manager.CommitChanges()
                            }
                            'recycle' { $pool.Recycle() | Out-Null }
                        }
                        Write-Output "IIS $action completed for '$name'."
                    }
                } finally { if ($null -ne $manager) { $manager.Dispose() } }
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
    }

    internal static async Task<string?> RunAsync(string? siteName, string action, string? targetPath,
        VpsSettings? vps, ProcessRunner runner, LogCallback log, string? serviceId, int? port = null)
    {
        if (string.IsNullOrWhiteSpace(siteName)) throw new InvalidOperationException("Missing IIS site name.");
        if (action == "ensure") _ = TargetPath(null, siteName);
        var script = Script(siteName, action, targetPath, vps?.DefaultDeployBasePath, port);
        string? resolvedPath = null;
        async Task CaptureLog(string level, string message, string? id)
        {
            foreach (var line in message.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                const string marker = "NET_DEPLOY_TARGET:";
                if (line.Trim().StartsWith(marker, StringComparison.Ordinal))
                    resolvedPath = Encoding.Unicode.GetString(Convert.FromBase64String(line.Trim()[marker.Length..]));
                else
                    await log(level, line.TrimEnd(), id);
            }
        }
        var arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        await Gate.WaitAsync();
        try
        {
            bool success;
            if (vps != null && !vps.IsLocal && !string.IsNullOrWhiteSpace(vps.Host) && vps.Host != "localhost" && vps.Host != "127.0.0.1")
            {
                using var client = new SshClient(vps.Host, vps.Port > 0 ? vps.Port : 22, vps.Username, vps.Password);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
                await client.ConnectAsync(CancellationToken.None);
                using var command = client.CreateCommand("powershell.exe " + arguments);
                command.CommandTimeout = TimeSpan.FromMinutes(2);
                await command.ExecuteAsync(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(command.Result)) await CaptureLog("INFO", command.Result.Trim(), serviceId);
                if (!string.IsNullOrWhiteSpace(command.Error)) await log("ERROR", command.Error.Trim(), serviceId);
                success = command.ExitStatus == 0;
            }
            else
            {
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                success = await runner.RunAsync(powershell, arguments, ".", CaptureLog, serviceId);
            }
            if (!success) throw new InvalidOperationException($"IIS {action} failed for '{siteName}'.");
            if (action == "ensure" && string.IsNullOrWhiteSpace(resolvedPath))
                throw new InvalidOperationException("IIS setup did not return a deployment path.");
        }
        finally { Gate.Release(); }
        if (action == "stop") await Task.Delay(3000);
        return resolvedPath;
    }
}
