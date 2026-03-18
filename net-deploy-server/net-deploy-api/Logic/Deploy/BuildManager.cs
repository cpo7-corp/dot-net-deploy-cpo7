namespace NET.Deploy.Api.Logic.Deploy;

public class BuildManager(ProcessRunner processRunner)
{
    public async Task<bool> BuildAsync(string projectPath, string outputPath, string serviceType, bool compileSingleFile, LogCallback log, string? serviceId)
    {
        var isNode = serviceType is "Angular" or "React" || projectPath.EndsWith("package.json");

        return isNode 
            ? await RunNpmBuildAsync(projectPath, outputPath, log, serviceId, serviceType)
            : await RunDotnetPublishAsync(projectPath, outputPath, compileSingleFile, log, serviceId);
    }

    private async Task<bool> RunNpmBuildAsync(string projectPath, string outputPath, LogCallback log, string? serviceId, string serviceType)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;

        var npm = ExeResolver.Resolve("npm");
        var npx = ExeResolver.Resolve("npx");

        await log("INFO", $"📦 Running npm install in: {projectDir}", serviceId);
        if (!await processRunner.RunAsync(npm, "install", projectDir, log, serviceId))
            return false;

        string commandExe;
        string commandArgs;
        string buildCommand;

        if (serviceType == "Angular")
        {
            commandExe = npx;
            commandArgs = "ng build --configuration production";
            buildCommand = "npx ng build --configuration production";
        }
        else
        {
            commandExe = npm;
            commandArgs = "run build";
            buildCommand = "npm run build";
        }

        await log("INFO", $"🏗️ Running {buildCommand}...", serviceId);
        if (!await processRunner.RunAsync(commandExe, commandArgs, projectDir, log, serviceId))
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

    private async Task<bool> RunDotnetPublishAsync(string projectPath, string outputPath, bool compileSingleFile, LogCallback log, string? serviceId)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
        var dotnet = ExeResolver.Resolve("dotnet");
        var args = $"publish \"{projectPath}\" -c Release -o \"{outputPath}\" --nologo -v minimal";

        if (compileSingleFile)
        {
            args += " -p:PublishSingleFile=true -r win-x64 --self-contained true";
        }
        
        // Ensure NuGet can resolve packages even when running under IIS AppPool Identity (where APPDATA is empty)
        // We must place this at the ROOT of the repository, otherwise referenced sibling projects won't find it.
        var rootDir = new DirectoryInfo(projectDir);
        while (rootDir != null && !Directory.Exists(Path.Combine(rootDir.FullName, ".git")))
        {
            rootDir = rootDir.Parent;
        }
        var targetConfigDir = rootDir?.FullName ?? projectDir;

        var nugetConfigPath = Path.Combine(targetConfigDir, "nuget.config");
        var nugetConfigPathUpper = Path.Combine(targetConfigDir, "NuGet.Config");
        
        if (!File.Exists(nugetConfigPath) && !File.Exists(nugetConfigPathUpper))
        {
            var defaultNugetConfig = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <add key=""nuget.org"" value=""https://api.nuget.org/v3/index.json"" protocolVersion=""3"" />
  </packageSources>
</configuration>";
            await File.WriteAllTextAsync(nugetConfigPath, defaultNugetConfig);
        }

        await log("INFO", $"🏗️ Running dotnet publish in: {projectDir}", serviceId);
        return await processRunner.RunAsync(dotnet, args, projectDir, log, serviceId);
    }
}
