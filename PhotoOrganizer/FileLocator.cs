using System.Diagnostics;

namespace PhotoOrganizer;

internal static class FileLocator
{
    public static ProcessStartInfo CreateStartInfo(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
        if (File.Exists(path)) start.ArgumentList.Add("/select,");
        else if (!Directory.Exists(path)) throw new FileNotFoundException("Changed");
        start.ArgumentList.Add(Path.GetFullPath(path));
        return start;
    }
    public static void Open(string path) => Process.Start(CreateStartInfo(path));
}
