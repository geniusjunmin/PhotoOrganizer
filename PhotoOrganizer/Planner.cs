using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoOrganizer;

internal static class PathSafety
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Contains(string parent, string child) =>
        Normalize(child).Equals(Normalize(parent), StringComparison.OrdinalIgnoreCase) ||
        Normalize(child).StartsWith(Path.TrimEndingDirectorySeparator(Normalize(parent)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void CheckAncestors(string path)
    {
        var current = Normalize(path);
        while (current is not null)
        {
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) &&
                (File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new InvalidOperationException("UnsafePath");
            current = Path.GetDirectoryName(current);
        }
    }

    public static void Validate(ScanOptions options)
    {
        if (!System.IO.Directory.Exists(options.Source) ||
            options.Mode == OperationMode.Copy && !System.IO.Directory.Exists(options.Destination))
            throw new InvalidOperationException("ChooseDirectories");
        if (options.Source.StartsWith(@"\\") || options.Destination.StartsWith(@"\\"))
            throw new InvalidOperationException("UnsafePath");
        CheckAncestors(options.Source);
        if (options.Mode == OperationMode.Copy) CheckAncestors(options.Destination);
        string actualSource = FileIdentity.DirectoryPath(options.Source);
        string actualDestination = options.Mode == OperationMode.Copy ? FileIdentity.DirectoryPath(options.Destination) : actualSource;
        if (options.Mode == OperationMode.Copy &&
            (Contains(actualSource, actualDestination) || Contains(actualDestination, actualSource)))
            throw new InvalidOperationException("NestedDirectories");
        var drive = new DriveInfo(Path.GetPathRoot(actualDestination)!);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new InvalidOperationException("TargetDisk");
    }
}

internal static class Planner
{
    private static readonly HashSet<string> Photos = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
    private const string StagingName = ".photo-organizer-staging";

    public static async Task<List<PlanItem>> ScanAsync(ScanOptions options,
        IProgress<WorkProgress> progress, CancellationToken token)
    {
        PathSafety.Validate(options);
        var paths = new List<string>();
        var result = new List<PlanItem>();
        var pending = new Stack<string>();
        var progressClock = Stopwatch.StartNew();
        if (options.YearFolders)
        {
            foreach (var folder in System.IO.Directory.EnumerateDirectories(options.Source))
                if (int.TryParse(Path.GetFileName(folder), out int year) && year > 1900 && year < 3000 &&
                    (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) pending.Push(folder);
        }
        else pending.Push(PathSafety.Normalize(options.Source));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                PathSafety.CheckAncestors(directory);
                foreach (var path in System.IO.Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                    paths.Add(path);
                    if (paths.Count % 100 == 0 && progressClock.ElapsedMilliseconds >= 100)
                    {
                        progress.Report(new("Enumerating", paths.Count, 0, 0, 0, ""));
                        progressClock.Restart();
                    }
                }
                if (!options.Recursive || options.YearFolders) continue;
                foreach (var child in System.IO.Directory.EnumerateDirectories(directory))
                {
                    if (Path.GetFileName(child) is StagingName or ".photo-organizer-withdrawn") continue;
                    if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0)
                        pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new() { Source = directory, State = ItemState.Failed, Detail = ex.Message });
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        progress.Report(new("Scanning", 0, paths.Count, 0, paths.Count, ""));
        var companions = paths.Where(path => Path.GetExtension(path).ToLowerInvariant() is
            ".mov" or ".aae" or ".xmp" or ".raw" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" or ".orf" or ".rw2")
            .Select(path => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < paths.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = paths[index];
            PlanItem item;
            try
            {
                var info = new FileInfo(path);
                var extension = info.Extension.ToLowerInvariant();
                var supported = Photos.Contains(extension) || options.IncludeVideos && Videos.Contains(extension);
                item = new()
                {
                    Source = path, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                    Identity = FileIdentity.Read(path),
                    State = supported ? ItemState.Fallback : ItemState.Unsupported, Selected = supported
                };
                if (supported)
                {
                    DateTime date = info.LastWriteTime;
                    string device = "UnknownDevice";
                    item.DateSource = "FileDate";
                    if (Photos.Contains(extension))
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var metadata = ImageMetadataReader.ReadMetadata(stream);
                            var sub = metadata.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                            var main = metadata.OfType<ExifIfd0Directory>().FirstOrDefault();
                            if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                            { date = original; item.DateSource = "OriginalDate"; }
                            else if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                            { date = digitized; item.DateSource = "DigitizedDate"; }
                            else if (main is not null && main.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
                            { date = modified; item.DateSource = "ModifiedDate"; }
                            var make = main?.GetDescription(ExifDirectoryBase.TagMake) ?? "";
                            var model = main?.GetDescription(ExifDirectoryBase.TagModel) ?? "";
                            var raw = (make + "_" + model).Trim('_', ' ');
                            if (raw.Length > 0) device = Sanitize(raw);
                        }
                        catch (ImageProcessingException)
                        {
                            item.State = ItemState.Failed;
                            item.Selected = false;
                            item.Detail = "InvalidImage";
                        }
                    }
                    item.Device = device;
                    if (item.State != ItemState.Failed)
                    {
                        item.CapturedAt = date;
                        item.State = item.DateSource == "FileDate" ? ItemState.Fallback : ItemState.Ready;
                        var folder = options.Mode == OperationMode.Rename ? info.DirectoryName! :
                            Path.Combine(options.Destination, date.ToString("yyyy", CultureInfo.InvariantCulture),
                                date.ToString("MM", CultureInfo.InvariantCulture));
                        var basename = options.Naming == NamingRule.Original ? Path.GetFileNameWithoutExtension(path) :
                            date.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + device;
                        PathSafety.CheckAncestors(folder);
                        if (options.Naming == NamingRule.Legacy)
                            basename = device + "_" + (item.DateSource == "FileDate" ? "X" : "") +
                                date.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                        var target = Path.Combine(folder, basename + extension);
                        if (options.Mode == OperationMode.Rename)
                        {
                            if (Videos.Contains(extension) || companions.Contains(Path.Combine(info.DirectoryName!, Path.GetFileNameWithoutExtension(path))))
                            {
                                item.Selected = false;
                                item.State = ItemState.Companion;
                            }
                            else if (options.Naming == NamingRule.Legacy && System.Text.RegularExpressions.Regex.IsMatch(
                                Path.GetFileNameWithoutExtension(path), @"^(?:.+_)?X?\d{17}(?:_\d+)?$") ||
                                Path.GetFileNameWithoutExtension(path).Equals(basename, StringComparison.OrdinalIgnoreCase) ||
                                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path),
                                    "^" + System.Text.RegularExpressions.Regex.Escape(basename) + @"_\d{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                target = path;
                                item.Selected = false;
                                item.State = ItemState.Unchanged;
                            }
                            else item.ContentHash = await FileIdentity.HashAsync(path, token);
                        }
                        int suffix = 0;
                        while (item.Selected && (targets.ContainsKey(target) || File.Exists(target) || System.IO.Directory.Exists(target)))
                        {
                            token.ThrowIfCancellationRequested();
                            string candidate = targets.GetValueOrDefault(target, target);
                            if (File.Exists(candidate) && new FileInfo(candidate).Length == item.Length)
                            {
                                PathSafety.CheckAncestors(candidate);
                                if (await SameContentAsync(path, candidate, token))
                                {
                                    item.State = ItemState.Duplicate;
                                    item.Selected = false;
                                    break;
                                }
                            }
                            target = Path.Combine(folder, basename + "_" + (++suffix).ToString("D2") + extension);
                        }
                        item.Destination = target;
                        if (item.Selected) targets.Add(target, path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                item = new() { Source = path, State = ItemState.Failed, Detail = ex.Message };
            }
            result.Add(item);
            if (progressClock.ElapsedMilliseconds >= 100 || index == paths.Count - 1)
            {
                progress.Report(new("Scanning", index + 1, paths.Count, index + 1, paths.Count, Path.GetFileName(path)));
                progressClock.Restart();
            }
        }
        return result;
    }

    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new StringBuilder();
        foreach (char c in value)
            result.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        var name = result.ToString().Trim('.', '_', ' ');
        return name.Length == 0 ? "UnknownDevice" : name[..Math.Min(name.Length, 80)];
    }

    public static async Task<bool> SameContentAsync(string first, string second, CancellationToken token)
    {
        using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var firstHash = await SHA256.HashDataAsync(a, token);
        var secondHash = await SHA256.HashDataAsync(b, token);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }
}
