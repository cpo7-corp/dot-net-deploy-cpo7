using System.Text;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Deploy;
using Renci.SshNet;

namespace NET.Deploy.Api.Logic.Docker;

public class DockerLogic(ILogger<DockerLogic> logger, ProcessRunner processRunner)
{
    public async Task<bool> PrepareRemoteDirectoryAsync(string projectPath, VpsSettings vps, LogCallback log, string? serviceId)
    {
        if (!IsRemote(vps) || vps.ServerType == "WindowsDocker") return true;

        try
        {
            using var client = CreateSshClient(vps);
            client.Connect();
            var commandText = vps.UseSudoDocker
                ? $"sudo mkdir -p {QuoteBash(projectPath)} && sudo chown {QuoteBash(vps.Username)} {QuoteBash(projectPath)}"
                : $"mkdir -p {QuoteBash(projectPath)}";
            var command = client.CreateCommand(commandText);
            command.Execute();
            if (command.ExitStatus == 0) return true;

            await log("ERROR", $"❌ [Docker] Cannot prepare target directory: {command.Error.Trim()}", serviceId);
            return false;
        }
        catch (Exception ex)
        {
            await log("ERROR", $"❌ [Docker] Cannot prepare target directory: {ex.Message}", serviceId);
            return false;
        }
    }

    /// <summary>
    /// Checks container status locally or remotely over SSH.
    /// Returns: "Running" | "Stopped" | "Error" | "Unknown"
    /// </summary>
    public async Task<string> GetStatusAsync(string containerName, VpsSettings? vps = null)
    {
        if (string.IsNullOrWhiteSpace(containerName)) return "Unknown";

        try
        {
            var isRemote = vps != null && !vps.IsLocal && !string.IsNullOrWhiteSpace(vps.Host) && vps.Host != "localhost" && vps.Host != "127.0.0.1";
            var inspectCmd = $"{GetDockerCommand(vps)} inspect -f \"{{{{.State.Status}}}}\" {QuoteArgument(containerName, vps)}";

            if (isRemote)
            {
                using var client = new SshClient(vps!.Host, vps.Port > 0 ? vps.Port : 22, vps.Username, vps.Password ?? string.Empty);
                client.Connect();
                var cmd = client.CreateCommand(inspectCmd);
                var output = cmd.Execute()?.Trim().ToLowerInvariant() ?? "";
                client.Disconnect();

                if (output == "running") return "Running";
                if (output is "exited" or "created" or "paused" or "dead") return "Stopped";
                return "Unknown";
            }
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo("docker", $"inspect -f \"{{{{.State.Status}}}}\" {containerName}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return "Unknown";

                var output = (await process.StandardOutput.ReadToEndAsync()).Trim().ToLowerInvariant();
                await process.WaitForExitAsync();

                if (output == "running") return "Running";
                if (output is "exited" or "created" or "paused" or "dead") return "Stopped";
                return "Unknown";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get docker container status for {Container}", containerName);
            return "Error";
        }
    }

    /// <summary>
    /// Executes Docker commands (start, stop, restart) locally or over SSH.
    /// </summary>
    public async Task<bool> ManageContainerAsync(string containerName, string action, LogCallback log, string? serviceId, VpsSettings? vps = null)
    {
        if (string.IsNullOrWhiteSpace(containerName)) return false;

        var isRemote = vps != null && !vps.IsLocal && !string.IsNullOrWhiteSpace(vps.Host) && vps.Host != "localhost" && vps.Host != "127.0.0.1";
        await log("INFO", $"🐳 [Docker] {action} container '{containerName}'...", serviceId);

        var dockerCmd = $"{GetDockerCommand(vps)} {action} {QuoteArgument(containerName, vps)}";

        if (isRemote)
        {
            try
            {
                using var client = new SshClient(vps!.Host, vps.Port > 0 ? vps.Port : 22, vps.Username, vps.Password ?? string.Empty);
                client.Connect();
                var cmd = client.CreateCommand(dockerCmd);
                var result = cmd.Execute();
                if (cmd.ExitStatus != 0)
                {
                    await log("ERROR", $"❌ [Docker] {action} failed on VPS: {cmd.Error.Trim()}", serviceId);
                    return false;
                }
                client.Disconnect();
                await log("SUCCESS", $"✅ [Docker] {action} completed on VPS: {result.Trim()}", serviceId);
                return true;
            }
            catch (Exception ex)
            {
                await log("ERROR", $"❌ [Docker] Failed to {action} container on VPS: {ex.Message}", serviceId);
                return false;
            }
        }
        else
        {
            return await processRunner.RunAsync("docker", $"{action} \"{containerName}\"", ".", log, serviceId);
        }
    }

    public async Task<bool> RunComposeAsync(
        string projectPath,
        string composeFile,
        string? environmentComposeFile,
        string? overrideFile,
        string? composeServiceName,
        string? composeProjectName,
        string action,
        VpsSettings? vps,
        LogCallback log,
        string? serviceId,
        CancellationToken ct = default)
    {
        var isRemote = IsRemote(vps);
        var composeAction = action switch
        {
            "deploy" => "up -d --build --no-deps",
            "start" => "start",
            "stop" => "stop",
            "restart" => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported Docker Compose action.")
        };
        var projectArgument = !string.IsNullOrWhiteSpace(composeProjectName)
            ? $"-p \"{composeProjectName}\" "
            : string.Empty;
        var environmentArgument = !string.IsNullOrWhiteSpace(environmentComposeFile)
            ? $" -f \"{environmentComposeFile}\""
            : string.Empty;
        var overrideArgument = !string.IsNullOrWhiteSpace(overrideFile)
            ? $" -f \"{overrideFile}\""
            : string.Empty;
        var serviceArgument = !string.IsNullOrWhiteSpace(composeServiceName)
            ? $" \"{composeServiceName}\""
            : string.Empty;
        var composeArguments = $"compose {projectArgument}-f \"{composeFile}\"{environmentArgument}{overrideArgument} {composeAction}{serviceArgument}";

        if (!isRemote)
        {
            return await processRunner.RunAsync(
                "docker",
                composeArguments,
                projectPath,
                log,
                serviceId,
                ct);
        }

        try
        {
            using var client = CreateSshClient(vps!);
            client.Connect();

            var commandText = BuildRemoteComposeCommand(projectPath, composeArguments, vps!);
            var command = client.CreateCommand(commandText);
            var output = await Task.Run(command.Execute, ct);

            if (command.ExitStatus != 0)
            {
                await log("ERROR", $"❌ [Docker] Docker Compose failed on VPS: {command.Error.Trim()}", serviceId);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                await log("INFO", output.Trim(), serviceId);
            }

            return true;
        }
        catch (Exception ex)
        {
            await log("ERROR", $"❌ [Docker] Docker Compose failed on VPS: {ex.Message}", serviceId);
            return false;
        }
    }

    /// <summary>
    /// Generates a zero-downtime docker-compose.yml file when none is provided,
    /// creating a balanced cluster of replicas with an internal Nginx proxy.
    /// </summary>
    public string GenerateComposeWithLoadBalancer(
        string serviceName,
        string gatewayContainerName,
        string dockerfilePath,
        int hostPort,
        int containerInternalPort,
        int replicas,
        int parallelism,
        int drainSeconds)
    {
        var cleanServiceName = serviceName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
        var sb = new StringBuilder();

        sb.AppendLine("services:");
        sb.AppendLine($"  {cleanServiceName}:");
        sb.AppendLine("    build:");
        sb.AppendLine("      context: .");
        sb.AppendLine($"      dockerfile: {dockerfilePath}");
        sb.AppendLine("    env_file:");
        sb.AppendLine("      - .env");
        sb.AppendLine("    deploy:");
        sb.AppendLine($"      replicas: {Math.Max(1, replicas)}");
        sb.AppendLine("      update_config:");
        sb.AppendLine($"        parallelism: {Math.Max(1, parallelism)}");
        sb.AppendLine($"        delay: {Math.Max(1, drainSeconds)}s");
        sb.AppendLine("        order: start-first");
        sb.AppendLine("        failure_action: rollback");
        sb.AppendLine("      restart_policy:");
        sb.AppendLine("        condition: on-failure");
        sb.AppendLine($"    stop_grace_period: {Math.Max(5, drainSeconds + 5)}s");
        sb.AppendLine("    networks:");
        sb.AppendLine("      - net-deploy-internal");
        sb.AppendLine();
        sb.AppendLine("  gateway:");
        sb.AppendLine("    image: nginx:alpine");
        sb.AppendLine($"    container_name: \"{gatewayContainerName.Replace("\"", "\\\"")}\"");
        sb.AppendLine("    restart: always");
        sb.AppendLine("    ports:");
        sb.AppendLine($"      - \"{hostPort}:80\"");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - ./nginx.conf:/etc/nginx/conf.d/default.conf:ro");
        sb.AppendLine("    depends_on:");
        sb.AppendLine($"      - {cleanServiceName}");
        sb.AppendLine("    networks:");
        sb.AppendLine("      - net-deploy-internal");
        sb.AppendLine();
        sb.AppendLine("networks:");
        sb.AppendLine("  net-deploy-internal:");
        sb.AppendLine("    driver: bridge");

        return sb.ToString();
    }

    /// <summary>
    /// Generates nginx.conf upstream to load balance between docker compose replicas.
    /// </summary>
    public string GenerateNginxConf(string serviceName, int hostPort, int containerInternalPort)
    {
        var cleanServiceName = serviceName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
        return $$"""
            resolver 127.0.0.11 valid=5s;

            upstream app_cluster {
                zone app_cluster 64k;
                server {{cleanServiceName}}:{{containerInternalPort}} resolve;
            }

            server {
                listen 80;

                location / {
                    proxy_pass http://app_cluster;
                    proxy_http_version 1.1;
                    proxy_set_header Upgrade $http_upgrade;
                    proxy_set_header Connection "upgrade";
                    proxy_set_header Host $host;
                    proxy_cache_bypass $http_upgrade;
                    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                    proxy_set_header X-Forwarded-Proto $scheme;
                    proxy_connect_timeout 5s;
                    proxy_read_timeout 60s;
                }
            }
            """;
    }

    /// <summary>
    /// Converts Environment variables and JSON configs into Docker .env format
    /// converting nested keys to .NET standard (e.g. ConnectionStrings__DefaultConnection)
    /// </summary>
    public string GenerateEnvFileContent(IEnumerable<EnvVariable> variables)
    {
        var sb = new StringBuilder();
        foreach (var v in variables)
        {
            if (string.IsNullOrWhiteSpace(v.Key)) continue;
            // .NET environment variable formatting: Replace ':' with '__'
            var formattedKey = v.Key.Replace(":", "__").Replace(".", "__");
            var escapedVal = v.Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$");
            sb.AppendLine($"{formattedKey}=\"{escapedVal}\"");
        }
        return sb.ToString();
    }

    public string GenerateComposeOverride(string composeServiceName, string envFile)
    {
        var escapedServiceName = composeServiceName.Replace("\"", "\\\"");
        var escapedEnvFile = envFile.Replace("\"", "\\\"");
        return $$"""
            services:
              "{{escapedServiceName}}":
                env_file:
                  - "{{escapedEnvFile}}"
            """;
    }

    private static bool IsRemote(VpsSettings? vps) =>
        vps != null &&
        !vps.IsLocal &&
        !string.IsNullOrWhiteSpace(vps.Host) &&
        vps.Host != "localhost" &&
        vps.Host != "127.0.0.1";

    private static SshClient CreateSshClient(VpsSettings vps) =>
        new(vps.Host, vps.Port > 0 ? vps.Port : 22, vps.Username, vps.Password ?? string.Empty);

    private static string GetDockerCommand(VpsSettings? vps) =>
        vps?.UseSudoDocker == true && vps.ServerType == "LinuxDocker" ? "sudo docker" : "docker";

    private static string BuildRemoteComposeCommand(string projectPath, string composeArguments, VpsSettings vps)
    {
        var docker = GetDockerCommand(vps);
        if (vps.ServerType == "WindowsDocker")
        {
            var escapedPath = projectPath.Replace("'", "''");
            var escapedArguments = composeArguments.Replace("\"", "`\"");
            return $"powershell -NoProfile -NonInteractive -Command \"Set-Location -LiteralPath '{escapedPath}'; {docker} {escapedArguments}\"";
        }

        return $"cd {QuoteBash(projectPath)} && {docker} {composeArguments}";
    }

    private static string QuoteArgument(string value, VpsSettings? vps) =>
        vps?.ServerType == "WindowsDocker" ? $"\"{value.Replace("\"", "\\\"")}\"" : QuoteBash(value);

    private static string QuoteBash(string value) => $"'{value.Replace("'", "'\"'\"'")}'";
}
