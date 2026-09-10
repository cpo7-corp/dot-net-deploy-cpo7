using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Data.Entities;

namespace NET.Deploy.Api.Logic.Settings;

public class SettingsLogic(MongoDbContext db)
{
    public async Task<AppSettingsDB> GetAsync()
    {
        var settings = await db.Settings.Find(_ => true).FirstOrDefaultAsync() ?? new AppSettingsDB();
        var hasMissingEnvironmentIds = settings.VpsEnvironments.Any(vps => string.IsNullOrWhiteSpace(vps.Id));
        if (!hasMissingEnvironmentIds) return settings;

        Prepare(settings);
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            await db.Settings.InsertOneAsync(settings);
        }
        else
        {
            await db.Settings.ReplaceOneAsync(
                Builders<AppSettingsDB>.Filter.Eq(item => item.Id, settings.Id),
                settings);
        }

        return settings;
    }

    public async Task<AppSettingsDB> SaveAsync(AppSettingsDB settings)
    {
        Prepare(settings);
        var existing = await db.Settings.Find(_ => true).FirstOrDefaultAsync();

        if (existing is null)
        {
            await db.Settings.InsertOneAsync(settings);
        }
        else
        {
            settings.Id = existing.Id;
            await db.Settings.ReplaceOneAsync(
                Builders<AppSettingsDB>.Filter.Eq(s => s.Id, existing.Id),
                settings);
        }

        return settings;
    }

    private void Prepare(AppSettingsDB settings)
    {
        if (settings.Git != null)
        {
            settings.Git.Token = settings.Git.Token?.Trim() ?? string.Empty;
            settings.Git.LocalBaseDir = settings.Git.LocalBaseDir?.Trim() ?? string.Empty;
        }

        if (settings.VpsEnvironments != null)
        {
            foreach (var vps in settings.VpsEnvironments)
            {
                if (string.IsNullOrWhiteSpace(vps.Id))
                {
                    vps.Id = Guid.CreateVersion7().ToString();
                }
                vps.Name = vps.Name?.Trim() ?? "Default";
                vps.Host = vps.Host?.Trim() ?? string.Empty;
                vps.Username = vps.Username?.Trim() ?? string.Empty;
                vps.Password = vps.Password?.Trim() ?? string.Empty;
                vps.EnvironmentTag = vps.EnvironmentTag?.Trim() ?? string.Empty;
                vps.DefaultDeployBasePath = vps.DefaultDeployBasePath?.Trim() ?? string.Empty;
                vps.ServerType = vps.ServerType?.Trim() ?? "Windows";
                vps.DefaultDockerBasePath = vps.DefaultDockerBasePath?.Trim() ?? string.Empty;
                vps.DockerRegistryUrl = vps.DockerRegistryUrl?.Trim() ?? string.Empty;
                vps.DockerRegistryUsername = vps.DockerRegistryUsername?.Trim() ?? string.Empty;
                vps.DockerRegistryPassword = vps.DockerRegistryPassword?.Trim() ?? string.Empty;

                if (vps.SharedVariables != null)
                {
                    vps.SharedVariables = vps.SharedVariables
                        .Select(v => new EnvVariable
                        {
                            Key = v.Key?.Trim() ?? string.Empty,
                            Value = v.Value?.Trim() ?? string.Empty
                        })
                        .Where(v => !string.IsNullOrWhiteSpace(v.Key))
                        .ToList();
                }

                if (vps.SharedFileRenames != null)
                {
                    vps.SharedFileRenames = vps.SharedFileRenames
                        .Select(f => new FileRename
                        {
                            SourceFileName = f.SourceFileName?.Trim() ?? string.Empty,
                            TargetFileName = f.TargetFileName?.Trim() ?? string.Empty
                        })
                        .Where(f => !string.IsNullOrWhiteSpace(f.SourceFileName))
                        .ToList();
                }
            }
        }
    }
}
