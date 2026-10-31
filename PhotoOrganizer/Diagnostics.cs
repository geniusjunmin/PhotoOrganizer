using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace PhotoOrganizer;

internal static class Diagnostics
{
    public static string Create(AppSettings settings)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        long? historyCount = 0;
        string database = Path.Combine(AppSettings.DataDirectory, "history.db");
        if (File.Exists(database))
        {
            try
            {
                using var history = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
                history.Open();
                using var count = history.CreateCommand();
                count.CommandText = "SELECT count(*) FROM operations";
                historyCount = Convert.ToInt64(count.ExecuteScalar());
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { historyCount = null; }
        }
        return JsonSerializer.Serialize(new
        {
            AppVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = Environment.Version.ToString(), Is64Bit = Environment.Is64BitProcess,
            PackageIdentity = PackageIdentity(), HistoryEntries = historyCount,
            DpiContext = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4)) ? "PerMonitorV2" : "Other",
            OSBuild = Environment.OSVersion.Version.ToString(), SQLite = command.ExecuteScalar()?.ToString(),
            settings.Language, Theme = settings.Theme.ToString(), CapturedUtc = DateTimeOffset.UtcNow,
            Formats = new[] { "JPG", "JPEG", "PNG", "MP4 (file modified date)", "MOV (file modified date)" }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
    private static string PackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        if (result == 15700) return "Unpackaged";
        if (result != 122) return "Unavailable";
        var value = new StringBuilder((int)length);
        return GetCurrentPackageFullName(ref length, value) == 0 ? value.ToString() : "Unavailable";
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? fullName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
