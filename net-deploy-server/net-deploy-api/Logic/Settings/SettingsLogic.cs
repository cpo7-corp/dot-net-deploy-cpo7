using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Logic.Settings;

public class SettingsLogic(MongoDbContext db)
{
    public async Task<AppSettingsDB> GetAsync() =>
        await db.Settings.Find(_ => true).FirstOrDefaultAsync() ?? new AppSettingsDB();

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
                vps.Name = vps.Name?.Trim() ?? "Default";
                vps.Host = vps.Host?.Trim() ?? string.Empty;
                vps.Username = vps.Username?.Trim() ?? string.Empty;
                vps.Password = vps.Password?.Trim() ?? string.Empty;
                vps.EnvironmentTag = vps.EnvironmentTag?.Trim() ?? string.Empty;

                if (vps.SharedVariables != null)
                {
                    vps.SharedVariables = vps.SharedVariables
                        .Select(v => new NET.Deploy.Api.Logic.Services.Entities.EnvVariableDB { 
                            Key = v.Key?.Trim() ?? string.Empty, 
                            Value = v.Value?.Trim() ?? string.Empty 
                        })
                        .Where(v => !string.IsNullOrWhiteSpace(v.Key))
                        .ToList();
                }

                if (vps.SharedFileRenames != null)
                {
                    vps.SharedFileRenames = vps.SharedFileRenames
                        .Select(f => new NET.Deploy.Api.Logic.Services.Entities.FileRenameDB { 
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
