using System.Text;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Deploy;
using Renci.SshNet;

namespace NET.Deploy.Api.Logic.Linux;

public class LinuxLogic(ILogger<LinuxLogic> logger)
{
    private static SshClient CreateSshClient(VpsSettings vps)
    {
        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            vps.Host,
            vps.Port > 0 ? vps.Port : 22,
            vps.Username,
            new PasswordAuthenticationMethod(vps.Username, vps.Password ?? string.Empty),
            new KeyboardInteractiveAuthenticationMethod(vps.Username));

        var kbdAuth = connectionInfo.AuthenticationMethods.OfType<KeyboardInteractiveAuthenticationMethod>().FirstOrDefault();
        if (kbdAuth != null)
        {
            kbdAuth.AuthenticationPrompt += (sender, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = vps.Password ?? string.Empty;
                }
            };
        }

        return new SshClient(connectionInfo);
    }

    private static string QuoteBash(string arg) => "'" + arg.Replace("'", "'\\''") + "'";

    private static string QuoteSystemd(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static bool HasLineBreak(string value) => value.Contains('\r') || value.Contains('\n');

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public async Task<bool> PrepareDeployDirectoryAsync(string targetPath, VpsSettings vps, LogCallback log, string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || HasLineBreak(targetPath) || HasLineBreak(vps.Username))
        {
            await log("ERROR", "❌ [Linux] The deployment path or SSH username contains an unsupported line break.", serviceId);
            return false;
        }

        var sudo = vps.Username != "root" ? "sudo " : "";
        var ownership = $"{vps.Username}:{vps.Username}";
        var command = $"{sudo}mkdir -p {QuoteBash(targetPath)} && {sudo}chown {QuoteBash(ownership)} {QuoteBash(targetPath)}";
        var (success, _, error) = await RunSshCommandAsync(vps, command, log, serviceId);
        if (success)
            return true;

        await log("ERROR", $"❌ [Linux] Failed to prepare deployment directory: {error}", serviceId);
        return false;
    }

    public async Task<(bool Success, string Output, string Error)> RunSshCommandAsync(VpsSettings vps, string commandText, LogCallback? log = null, string? serviceId = null)
    {
        try
        {
            using var client = CreateSshClient(vps);
            client.Connect();

            var cmd = client.CreateCommand(commandText);
            var asyncResult = cmd.BeginExecute();

            using var reader = new StreamReader(cmd.OutputStream);
            using var errReader = new StreamReader(cmd.ExtendedOutputStream);

            while (!asyncResult.IsCompleted)
            {
                if (log != null)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line)) await log("INFO", line, serviceId);
                }
                await Task.Delay(100);
            }

            cmd.EndExecute(asyncResult);

            var remainingOut = await reader.ReadToEndAsync();
            if (log != null && !string.IsNullOrWhiteSpace(remainingOut))
            {
                foreach (var l in remainingOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    await log("INFO", l.TrimEnd(), serviceId);
            }

            var err = (await errReader.ReadToEndAsync())?.Trim() ?? string.Empty;
            if (log != null && !string.IsNullOrWhiteSpace(err) && cmd.ExitStatus != 0)
            {
                await log("ERROR", err, serviceId);
            }

            client.Disconnect();
            return (cmd.ExitStatus == 0, cmd.Result ?? string.Empty, err);
        }
        catch (Exception ex)
        {
            if (log != null) await log("ERROR", $"❌ SSH Error: {ex.Message}", serviceId);
            logger.LogError(ex, "Failed to run SSH command '{Command}' on {Host}", commandText, vps.Host);
            return (false, string.Empty, ex.Message);
        }
    }

    public async Task<string> GetServiceStatusAsync(string serviceName, VpsSettings? vps)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || vps == null || string.IsNullOrWhiteSpace(vps.Host))
            return "Unknown";

        try
        {
            var unitName = NormalizeServiceName(serviceName);
            var cmd = $"systemctl is-active {QuoteBash(unitName)}";
            var (success, output, _) = await RunSshCommandAsync(vps, cmd);
            var state = output.Trim().ToLowerInvariant();

            return state switch
            {
                "active" => "Running",
                "inactive" or "failed" or "deactivating" => "Stopped",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Error";
        }
    }

    public async Task<bool> ManageServiceAsync(string serviceName, string action, VpsSettings vps, LogCallback log, string? serviceId)
    {
        var unitName = NormalizeServiceName(serviceName);
        var actionWord = action switch
        {
            "start" => "Starting",
            "stop" => "Stopping",
            "restart" => "Restarting",
            _ => action
        };

        await log("INFO", $"⚙️ [systemd] {actionWord} service '{unitName}'...", serviceId);

        var sudo = vps.Username != "root" ? "sudo " : "";
        var cmd = $"{sudo}systemctl {action} {QuoteBash(unitName)}";
        var (success, _, err) = await RunSshCommandAsync(vps, cmd, log, serviceId);

        if (success)
        {
            await log("SUCCESS", $"✅ [systemd] Service '{unitName}' {action} succeeded.", serviceId);
            return true;
        }

        await log("ERROR", $"❌ [systemd] Service '{unitName}' {action} failed: {err}", serviceId);
        return false;
    }

    public async Task<bool> EnsureSystemdServiceAsync(
        string serviceName,
        string targetPath,
        string serviceType,
        VpsSettings vps,
        LogCallback log,
        string? serviceId,
        int? internalPort = null)
    {
        var unitName = NormalizeServiceName(serviceName);
        var sudo = vps.Username != "root" ? "sudo " : "";

        if (string.IsNullOrWhiteSpace(unitName) || HasLineBreak(serviceName) || HasLineBreak(targetPath) || HasLineBreak(vps.Username))
        {
            await log("ERROR", "❌ [systemd] The service name, target path, or SSH username is invalid.", serviceId);
            return false;
        }

        await log("INFO", $"⚙️ [systemd] Configuring systemd unit for '{unitName}'...", serviceId);

        var checkDllsCmd = $"find {QuoteBash(targetPath)} -maxdepth 1 -name '*.dll' ! -name '*.Views.dll'";
        var (_, dllsOut, _) = await RunSshCommandAsync(vps, checkDllsCmd);

        var dllFiles = dllsOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(p => p.Trim())
                              .ToList();

        var mainDll = dllFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                   ?? dllFiles.FirstOrDefault(f => !Path.GetFileName(f).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
                                                   !Path.GetFileName(f).StartsWith("System.", StringComparison.OrdinalIgnoreCase));

        string execStart;
        if (mainDll != null)
        {
            execStart = $"/usr/bin/dotnet {QuoteSystemd(mainDll)}";
        }
        else
        {
            var executablePath = $"{targetPath.TrimEnd('/')}/{serviceName}";
            var (executable, _, executableError) = await RunSshCommandAsync(vps, $"chmod u+x {QuoteBash(executablePath)}", log, serviceId);
            if (!executable)
            {
                await log("ERROR", $"❌ [systemd] Failed to make the application executable: {executableError}", serviceId);
                return false;
            }

            execStart = QuoteSystemd(executablePath);
        }

        var envVars = new StringBuilder();
        envVars.AppendLine("Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false");
        envVars.AppendLine("Environment=ASPNETCORE_ENVIRONMENT=Production");
        if (internalPort.HasValue && internalPort > 0)
        {
            envVars.AppendLine($"Environment=ASPNETCORE_URLS=http://0.0.0.0:{internalPort.Value}");
        }

        var serviceFileContent = $@"[Unit]
Description={serviceName} (.NET Service managed by NET Deploy)
After=network.target

[Service]
Type=simple
User={QuoteSystemd(vps.Username)}
WorkingDirectory={QuoteSystemd(targetPath)}
ExecStart={execStart}
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier={unitName}
{envVars}

[Install]
WantedBy=multi-user.target
".Replace("\r\n", "\n").Trim();

        var tempRemoteFile = $"/tmp/{unitName}.service";
        var writeTempCmd = $"echo {QuoteBash(ToBase64(serviceFileContent))} | base64 -d > {QuoteBash(tempRemoteFile)}";
        var (written, _, writeError) = await RunSshCommandAsync(vps, writeTempCmd, log, serviceId);
        if (!written)
        {
            await log("ERROR", $"❌ [systemd] Failed to write the unit file: {writeError}", serviceId);
            return false;
        }

        var installCmd = $"{sudo}mv {tempRemoteFile} /etc/systemd/system/{unitName}.service && {sudo}systemctl daemon-reload && {sudo}systemctl enable {QuoteBash(unitName)}";
        var (installed, _, installErr) = await RunSshCommandAsync(vps, installCmd, log, serviceId);

        if (!installed)
        {
            await log("ERROR", $"❌ [systemd] Failed to install unit file: {installErr}", serviceId);
            return false;
        }

        await log("SUCCESS", $"✅ [systemd] Service '{unitName}' registered and enabled.", serviceId);
        return true;
    }

    public async Task<bool> ConfigureNginxAsync(
        string domainOrSiteName,
        string serviceType,
        string targetPath,
        int? internalPort,
        int? publicPort,
        VpsSettings vps,
        LogCallback log,
        string? serviceId)
    {
        var sudo = vps.Username != "root" ? "sudo " : "";
        var confName = SanitizeFileName(domainOrSiteName);
        var listenPort = publicPort.HasValue && publicPort > 0 ? publicPort.Value : 80;

        if (string.IsNullOrWhiteSpace(confName) || HasLineBreak(domainOrSiteName) || HasLineBreak(targetPath))
        {
            await log("ERROR", "❌ [Nginx] The site name or target path is invalid.", serviceId);
            return false;
        }

        await log("INFO", $"🌐 [Nginx] Configuring reverse proxy / site for '{confName}' (Port: {listenPort})...", serviceId);

        string nginxConfig;
        if (serviceType is "Angular" or "React")
        {
            nginxConfig = $@"server {{
    listen {listenPort};
    server_name {confName} _;

    root {targetPath};
    index index.html;

    location / {{
        try_files $uri $uri/ /index.html;
    }}

    error_page 500 502 503 504 /50x.html;
    location = /50x.html {{
        root /usr/share/nginx/html;
    }}
}}".Replace("\r\n", "\n").Trim();
        }
        else
        {
            var proxyPort = internalPort ?? 5000;
            nginxConfig = $@"server {{
    listen {listenPort};
    server_name {confName} _;

    location / {{
        proxy_pass http://127.0.0.1:{proxyPort};
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }}
}}".Replace("\r\n", "\n").Trim();
        }

        var tempConf = $"/tmp/{confName}.conf";
        var writeCmd = $"echo {QuoteBash(ToBase64(nginxConfig))} | base64 -d > {QuoteBash(tempConf)}";
        var (written, _, writeError) = await RunSshCommandAsync(vps, writeCmd, log, serviceId);
        if (!written)
        {
            await log("ERROR", $"❌ [Nginx] Failed to write the configuration file: {writeError}", serviceId);
            return false;
        }

        var enableCmd = $"{sudo}mkdir -p /etc/nginx/sites-available /etc/nginx/sites-enabled && " +
                        $"{sudo}mv {tempConf} /etc/nginx/sites-available/{confName}.conf && " +
                        $"{sudo}ln -sf /etc/nginx/sites-available/{confName}.conf /etc/nginx/sites-enabled/{confName}.conf && " +
                        $"{sudo}nginx -t && {sudo}systemctl reload nginx";

        var (success, _, err) = await RunSshCommandAsync(vps, enableCmd, log, serviceId);
        if (success)
        {
            await log("SUCCESS", $"✅ [Nginx] Configuration for '{confName}' applied and Nginx reloaded.", serviceId);
            return true;
        }

        await log("ERROR", $"❌ [Nginx] Configuration failed: {err}", serviceId);
        return false;
    }

    public static string NormalizeServiceName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
        return cleaned.ToLowerInvariant().Trim('-');
    }

    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-').ToArray());
        return cleaned.ToLowerInvariant().Trim('-');
    }
}
