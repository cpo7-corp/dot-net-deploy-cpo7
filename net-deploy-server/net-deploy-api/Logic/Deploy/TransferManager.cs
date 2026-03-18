using NET.Deploy.Api.Logic.Settings.Entities;
using Renci.SshNet;

namespace NET.Deploy.Api.Logic.Deploy;

public class TransferManager
{
    public async Task<bool> TransferAsync(string localPath, string remotePath, VpsSettings? vps, LogCallback log, string? serviceId)
    {
        const int maxTransferAttempts = 3;
        for (int attempt = 1; attempt <= maxTransferAttempts; attempt++)
        {
            try
            {
                if (vps != null && !string.IsNullOrWhiteSpace(vps.Host) && vps.Host != "localhost" && vps.Host != "127.0.0.1")
                {
                    if (attempt > 1) await log("WARNING", $"🔄 Retrying upload ({attempt}/{maxTransferAttempts})...", serviceId);
                    else await log("INFO", $"🚀 Uploading files to remote VPS: {vps.Host} at {remotePath}...", serviceId);

                    await UploadDirectoryToRemoteAsync(localPath, remotePath, vps, log, serviceId);
                    await log("SUCCESS", $"✅ Files uploaded to remote: {vps.Host}", serviceId);
                }
                else
                {
                    await log("INFO", $"📂 Copying to: {remotePath} (local)", serviceId);
                    FileHelper.CopyDirectory(localPath, remotePath);
                    await log("SUCCESS", $"✅ Files copied locally to {remotePath}", serviceId);
                }
                return true; 
            }
            catch (Exception ex)
            {
                if (attempt == maxTransferAttempts) 
                {
                    throw new Exception($"Failed to transfer to {remotePath}: {ex.Message}", ex);
                }
                await log("WARNING", $"⚠️ Transfer attempt {attempt} failed: {ex.Message}. Retrying in 5s...", serviceId);
                await Task.Delay(5000);
            }
        }
        return false;
    }

    private async Task UploadDirectoryToRemoteAsync(string localPath, string remotePath, VpsSettings vps, LogCallback log, string? serviceId)
    {
        await log("INFO", $"[SFTP] Attempting connect to Host: '{vps.Host}', Username: '{vps.Username}'", serviceId);

        var connectionInfo = new Renci.SshNet.ConnectionInfo(vps.Host, vps.Port > 0 ? vps.Port : 22, vps.Username,
            new PasswordAuthenticationMethod(vps.Username, vps.Password),
            new KeyboardInteractiveAuthenticationMethod(vps.Username));

        var kbdAuth = connectionInfo.AuthenticationMethods.OfType<KeyboardInteractiveAuthenticationMethod>().FirstOrDefault();
        if (kbdAuth != null)
        {
            kbdAuth.AuthenticationPrompt += (sender, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = vps.Password;
                }
            };
        }

        try
        {
            using var client = new SftpClient(connectionInfo);
            client.HostKeyReceived += (sender, e) => e.CanTrust = true;
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            
            client.Connect();
            
            var root = remotePath.Replace("\\", "/");
            async Task UploadSf(string localDir, string remoteDir)
            {
                if (!client.Exists(remoteDir)) EnsureRemoteDirectory(client, remoteDir);
                foreach (var file in Directory.GetFiles(localDir))
                {
                    using var fs = File.OpenRead(file);
                    client.UploadFile(fs, remoteDir + "/" + Path.GetFileName(file));
                }
                foreach (var subDir in Directory.GetDirectories(localDir))
                {
                    await UploadSf(subDir, remoteDir + "/" + Path.GetFileName(subDir));
                }
            }

            await UploadSf(localPath, root);
            client.Disconnect();
            return;
        }
        catch (Exception ex)
        {
            await log("ERROR", $"SFTP connection failed: {ex.Message}", serviceId);
            throw;
        }
    }

    private void EnsureRemoteDirectory(SftpClient client, string path)
    {
        if (client.Exists(path)) return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        
        int startIndex = 0;
        if (path.Contains(":/"))
        {
            current = parts[0] + "/";
            startIndex = 1;
        }
        else if (path.StartsWith("/"))
        {
            current = "/";
        }

        for (int i = startIndex; i < parts.Length; i++)
        {
            current += (current.EndsWith("/") ? "" : "/") + parts[i];
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }


}
