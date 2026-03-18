namespace NET.Deploy.Api.Logic.Deploy;

/// <summary>
/// Resolves full paths to executables (git, dotnet, npm, npx, node, etc.)
/// Works both when running locally and under IIS / Windows Service (restricted PATH).
/// </summary>
public static class ExeResolver
{
    private static readonly string[] CommonNodeDirs =
    [
        @"C:\Program Files\nodejs",
        @"C:\Program Files (x86)\nodejs",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"nvm\current"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nvm\current"),
        @"C:\ProgramData\nvm\current",
    ];

    private static readonly string[] CommonGitDirs =
    [
        @"C:\Program Files\Git\bin",
        @"C:\Program Files\Git\cmd",
        @"C:\Program Files (x86)\Git\bin",
        @"C:\Program Files (x86)\Git\cmd",
    ];

    private static readonly string[] CommonDotnetDirs =
    [
        @"C:\Program Files\dotnet",
        @"C:\Program Files (x86)\dotnet",
    ];

    // Windows executables can be .exe or .cmd
    private static readonly string[] WinExtensions = [".exe", ".cmd", ".bat"];

    public static string Resolve(string exe)
    {
        // 1. Already a full path
        if (Path.IsPathRooted(exe) && File.Exists(exe)) return exe;

        var baseName = Path.GetFileNameWithoutExtension(exe);

        // 2. Search PATH environment variable (both machine and user PATH)
        var pathEnv = string.Join(";",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
            Environment.GetEnvironmentVariable("PATH") ?? "");

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var found = ProbeDir(dir.Trim(), baseName);
            if (found != null) return found;
        }

        // 3. Well-known directories by executable name
        var searchDirs = baseName.ToLower() switch
        {
            "git" => CommonGitDirs,
            "dotnet" => CommonDotnetDirs,
            "npm" or "npx" or "node" => CommonNodeDirs,
            _ => Array.Empty<string>()
        };

        foreach (var dir in searchDirs)
        {
            var found = ProbeDir(dir, baseName);
            if (found != null) return found;
        }

        // 4. Fallback
        return exe;
    }

    private static string? ProbeDir(string dir, string baseName)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            foreach (var ext in WinExtensions)
            {
                var path = Path.Combine(dir, baseName + ext);
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }
}
