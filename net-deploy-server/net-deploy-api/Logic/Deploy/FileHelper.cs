namespace NET.Deploy.Api.Logic.Deploy;

public static class FileHelper
{
    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var subDest = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, subDest);
        }
    }

    public static void DeleteDirectoryRecursively(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }
}
