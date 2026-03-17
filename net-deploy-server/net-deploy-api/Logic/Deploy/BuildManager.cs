namespace NET.Deploy.Api.Logic.Deploy;

public class BuildManager(ProcessRunner processRunner)
{
    public async Task<bool> BuildAsync(string projectPath, string outputPath, string serviceType, LogCallback log, string? serviceId)
    {
        var isNode = serviceType is "Angular" or "React" || projectPath.EndsWith("package.json");

        return isNode 
            ? await RunNpmBuildAsync(projectPath, outputPath, log, serviceId, serviceType)
            : await RunDotnetPublishAsync(projectPath, outputPath, log, serviceId);
    }

    private async Task<bool> RunNpmBuildAsync(string projectPath, string outputPath, LogCallback log, string? serviceId, string serviceType)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
        var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        
        string Run(string verb) => isWindows ? "cmd.exe" : verb;
        string Args(string verb, string args) => isWindows ? $"/c \"{verb} {args}\"" : args;

        await log("INFO", $"📦 Running npm install in: {projectDir}", serviceId);
        if (!await processRunner.RunAsync(Run("npm"), Args("npm", "install"), projectDir, log, serviceId))
            return false;

        string buildCommand = serviceType == "Angular" ? "npx ng build --configuration production" : "npm run build";
        string commandVerb = serviceType == "Angular" ? "npx" : "npm";
        string commandArgs = serviceType == "Angular" ? "ng build --configuration production" : "run build";

        await log("INFO", $"🏗️ Running {buildCommand}...", serviceId);
        if (!await processRunner.RunAsync(Run(commandVerb), Args(commandVerb, commandArgs), projectDir, log, serviceId))
            return false;

        var distPath = Path.Combine(projectDir, "dist");
        var buildPath = Path.Combine(projectDir, "build");
        var builtFolder = Directory.Exists(distPath) ? distPath : (Directory.Exists(buildPath) ? buildPath : null);

        if (builtFolder != null)
        {
            var indexFiles = Directory.GetFiles(builtFolder, "index.html", SearchOption.AllDirectories);
            if (indexFiles.Length > 0)
            {
                builtFolder = Path.GetDirectoryName(indexFiles[0]);
            }
            
            FileHelper.CopyDirectory(builtFolder!, outputPath);
            return true;
        }

        await log("ERROR", "❌ Could not find 'dist' or 'build' output folder.", serviceId);
        return false;
    }

    private async Task<bool> RunDotnetPublishAsync(string projectPath, string outputPath, LogCallback log, string? serviceId)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
        var args = $"publish \"{projectPath}\" -c Release -o \"{outputPath}\" --nologo";
        
        await log("INFO", $"🏗️ Running dotnet publish in: {projectDir}", serviceId);
        return await processRunner.RunAsync("dotnet", args, projectDir, log, serviceId);
    }
}
