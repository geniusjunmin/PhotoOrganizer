using System.Diagnostics;
using System.Drawing;
using System.Text;
using PhotoOrganizer;

internal static class MetadataAndScaleTests
{
    private sealed class Reporter : IProgress<WorkProgress> { public void Report(WorkProgress value) { } }
    public static async Task RunAsync(string root)
    {
        string source = Path.Combine(root, "exif");
        string target = Path.Combine(root, "exif-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        WriteExifJpeg(Path.Combine(source, "original.jpeg"), "2020:02:03 04:05:06", "2021:03:04 05:06:07", "2022:04:05 06:07:08");
        WriteExifJpeg(Path.Combine(source, "digitized.jpg"), null, "2021:03:04 05:06:07", "2022:04:05 06:07:08");
        WriteExifJpeg(Path.Combine(source, "modified.JPG"), null, null, "2022:04:05 06:07:08");
        var options = new ScanOptions(source, target, true, false, NamingRule.DateDevice);
        var plan = await Planner.ScanAsync(options, new Reporter(), default);
        Check(plan.Single(x => x.Source.EndsWith("original.jpeg")).Destination.Contains("20200203_040506_TestCamera_ModelA"),
            "EXIF original date takes priority over digitized, modified and filesystem dates");
        Check(plan.Single(x => x.Source.EndsWith("digitized.jpg")).DateSource == "DigitizedDate",
            "EXIF digitized date fallback");
        Check(plan.Single(x => x.Source.EndsWith("modified.JPG")).DateSource == "ModifiedDate",
            "EXIF IFD0 modified date fallback");
        Check(plan.All(x => x.Selected), "JPEG/JPEG uppercase extensions supported");

        string longSource = Path.Combine(root, "long-path", new string('a', 120), new string('b', 120));
        string longTarget = Path.Combine(root, "long-target");
        Directory.CreateDirectory(longSource);
        Directory.CreateDirectory(longTarget);
        File.Copy(Path.Combine(source, "original.jpeg"), Path.Combine(longSource, new string('c', 180) + ".jpeg"));
        var longOptions = new ScanOptions(longSource, longTarget, true, false, NamingRule.Original);
        var longPlan = (await Planner.ScanAsync(longOptions, new Reporter(), default)).Where(x => x.Selected).ToList();
        await CopyExecutor.ExecuteAsync(longOptions, longPlan, Path.Combine(root, "long-data"), new Reporter(), default);
        Check(longPlan.Count == 1 && longPlan[0].State == ItemState.Copied,
            "Long-path copy and handle-based commit" + (longPlan.Any(x => x.State == ItemState.Failed) ?
                " — " + string.Join(", ", longPlan.Select(x => x.Detail)) : ""));

        string many = Path.Combine(root, "scale-source");
        string manyTarget = Path.Combine(root, "scale-target");
        Directory.CreateDirectory(many);
        Directory.CreateDirectory(manyTarget);
        byte[] sample = File.ReadAllBytes(Path.Combine(source, "original.jpeg"));
        for (int index = 0; index < 10000; index++) File.WriteAllBytes(Path.Combine(many, $"photo-{index:D5}.jpg"), sample);
        var clock = Stopwatch.StartNew();
        var manyPlan = await Planner.ScanAsync(new(many, manyTarget, true, false, NamingRule.Original), new Reporter(), default);
        clock.Stop();
        long memory = Process.GetCurrentProcess().PeakWorkingSet64;
        Check(manyPlan.Count == 10000 && manyPlan.All(x => x.Selected), "10,000-file read-only scan completed");
        Check(clock.Elapsed.TotalSeconds < 120, "Synthetic 10,000-file scan stays within 120 seconds");
        Check(!Directory.EnumerateFileSystemEntries(manyTarget).Any(), "10,000-file preview remains read-only");
        File.WriteAllText(Path.Combine(root, "performance.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Files = 10000, SampleBytes = sample.Length, ScanSeconds = clock.Elapsed.TotalSeconds,
            PeakWorkingSetBytes = memory, ProcessorCount = Environment.ProcessorCount,
            Runtime = Environment.Version.ToString(), Note = "Synthetic small JPEG fixture. Real camera-file performance requires separate hardware benchmarking."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"METRIC: 10000 small JPEGs, scan={clock.Elapsed.TotalSeconds:F2}s, process peak={memory / 1048576d:F1}MB.");
    }

    private static void WriteExifJpeg(string path, string? original, string? digitized, string modified)
    {
        using var bitmap = new Bitmap(16, 16);
        using var jpeg = new MemoryStream();
        bitmap.Save(jpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
        var dates = new List<(ushort Tag, string Text)>();
        if (original is not null) dates.Add((0x9003, original));
        if (digitized is not null) dates.Add((0x9004, digitized));
        const int mainOffset = 8;
        const int subOffset = mainOffset + 2 + 4 * 12 + 4;
        int dataOffset = subOffset + 2 + dates.Count * 12 + 4;
        using var tiff = new MemoryStream();
        using var writer = new BinaryWriter(tiff, Encoding.ASCII, true);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write((uint)mainOffset);
        writer.Write((ushort)4);
        var strings = new List<string>();
        WriteStringEntry(0x010F, "TestCamera");
        WriteStringEntry(0x0110, "ModelA");
        WriteStringEntry(0x0132, modified);
        writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write((uint)1); writer.Write((uint)subOffset);
        writer.Write((uint)0);
        writer.Write((ushort)dates.Count);
        foreach (var date in dates) WriteStringEntry(date.Tag, date.Text);
        writer.Write((uint)0);
        foreach (var value in strings) writer.Write(Encoding.ASCII.GetBytes(value + "\0"));
        byte[] payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff.ToArray()).ToArray();
        using var output = File.Create(path);
        output.Write([0xFF, 0xD8, 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
        output.Write(payload);
        output.Write(jpeg.ToArray().AsSpan(2));
        File.SetLastWriteTime(path, new DateTime(2030, 1, 1));
        return;

        void WriteStringEntry(ushort tag, string value)
        {
            writer.Write(tag); writer.Write((ushort)2); writer.Write((uint)(value.Length + 1)); writer.Write((uint)dataOffset);
            dataOffset += value.Length + 1;
            strings.Add(value);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
