using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;

namespace PhotoOrganizer;

internal enum ItemState { Ready, Fallback, Unsupported, Duplicate, Failed, Copied, Canceled, Renamed, Unchanged, Companion }
internal enum ThemeMode { System, Light, Dark }
internal enum NamingRule { DateDevice, Original, Legacy }
internal enum OperationMode { Copy, Rename }
internal sealed record ScanOptions(string Source, string Destination, bool Recursive, bool IncludeVideos, NamingRule Naming,
    OperationMode Mode = OperationMode.Copy, bool YearFolders = false);
internal sealed class PlanItem
{
    public bool Selected { get; set; }
    public required string Source { get; init; }
    public string Destination { get; set; } = "";
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string Identity { get; init; } = "";
    public string ContentHash { get; set; } = "";
    public DateTime? CapturedAt { get; set; }
    public string DateSource { get; set; } = "NoDate";
    public string Device { get; set; } = "";
    public ItemState State { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed record WorkProgress(string Stage, int Completed, int Total, long Units, long TotalUnits, string FileName);

internal sealed class RemainingTime
{
    private readonly TimeProvider clock;
    private readonly long started;
    public RemainingTime(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        started = clock.GetTimestamp();
    }
    public TimeSpan? Estimate(long completed, long total)
    {
        if (total <= 0 || completed <= 0) return null;
        if (completed >= total) return TimeSpan.Zero;
        double seconds = clock.GetElapsedTime(started).TotalSeconds;
        if (seconds < 2) return null;
        return TimeSpan.FromSeconds(Math.Clamp(seconds * (total - completed) / completed, 0, 31536000));
    }
}

internal sealed class AppSettings
{
    public static string DataDirectory
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != 122) return Path.Combine(local, "PhotoOrganizer");
            var family = new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length, family) == 0 ?
                Path.Combine(local, "Packages", family.ToString(), "LocalState", "PhotoOrganizer") :
                Path.Combine(local, "PhotoOrganizer");
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? family);
    public string Language { get; set; } = "zh-CN";
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public static AppSettings Load(string directory)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")));
            if (value is null) return new();
            if (value.Language is not ("zh-CN" or "en")) value.Language = "zh-CN";
            if (!Enum.IsDefined(value.Theme)) value.Theme = ThemeMode.System;
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}

internal sealed class TextCatalog
{
    private Dictionary<string, string> entries = [];
    public string Language { get; private set; } = "zh-CN";
    public void Load(string language)
    {
        entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Locales", language + ".json")))!;
        Language = language;
    }
    public string this[string key] => entries.GetValueOrDefault(key, key);
    public string Format(string key, params object[] values) =>
        string.Format(System.Globalization.CultureInfo.GetCultureInfo(Language), this[key], values);
}
