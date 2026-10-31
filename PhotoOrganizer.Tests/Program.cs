using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoOrganizer;

internal static class Program
{
    private sealed class InlineProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => action(value); }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--seed-package-data", var dataDirectory, var fixtureRoot])
        {
            Directory.CreateDirectory(fixtureRoot);
            string source = Path.Combine(fixtureRoot, "source");
            string archive = Path.Combine(fixtureRoot, "archive");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(archive);
            using (var image = new Bitmap(32, 32)) image.Save(Path.Combine(source, "original.png"), System.Drawing.Imaging.ImageFormat.Png);
            var options = new ScanOptions(source, archive, true, false, NamingRule.DateDevice);
            var reporter = new InlineProgress(_ => { });
            var plan = Planner.ScanAsync(options, reporter, default).GetAwaiter().GetResult();
            CopyExecutor.ExecuteAsync(options, plan.Where(x => x.Selected).ToList(), dataDirectory, reporter, default).GetAwaiter().GetResult();
            new AppSettings { Language = "en", Theme = ThemeMode.Dark }.Save(dataDirectory);
            return 0;
        }
        if (args.Length == 3 && args[0].StartsWith("--crash-"))
        {
            AdvancedTests.CrashWorkerAsync(args[0]["--crash-".Length..], args[1], args[2]).GetAwaiter().GetResult();
            return 73;
        }
        if (args.Length != 1) throw new ArgumentException("Pass an isolated test-output directory.");
        var root = Path.Combine(Path.GetFullPath(args[0]), "run-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            RunCoreTests(root).GetAwaiter().GetResult();
            AdvancedTests.RunAsync(root).GetAwaiter().GetResult();
            MetadataAndScaleTests.RunAsync(root).GetAwaiter().GetResult();
            FailureAndMigrationTests.RunAsync(root).GetAwaiter().GetResult();
            var uiTarget = Path.Combine(root, "ui-archive");
            Directory.CreateDirectory(uiTarget);
            var options = new ScanOptions(Path.Combine(root, "source"), uiTarget, true, false, NamingRule.DateDevice);
            var preview = Planner.ScanAsync(options, new InlineProgress(_ => { }), default).GetAwaiter().GetResult();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm(Path.Combine(root, "settings"));
            form.RenderForTest(Path.Combine(root, "ui-zh-light.png"), "zh-CN", ThemeMode.Light, options, preview);
            form.RenderForTest(Path.Combine(root, "ui-en-dark.png"), "en", ThemeMode.Dark, options, preview);
            Console.WriteLine("PASS: UI constructed and rendered in Chinese/light and English/dark.");
            form.Close();
            UiWorkflowTests.Run(root);
            Console.WriteLine("ARTIFACTS: " + root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine("ARTIFACTS: " + root);
            return 1;
        }
    }

    private static async Task RunCoreTests(string root)
    {
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "archive");
        var data = Path.Combine(root, "settings");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var when = new DateTime(2024, 5, 6, 14, 23, 1);
        foreach (var (name, color) in new[] { ("a.png", Color.CadetBlue), ("b.PNG", Color.OrangeRed) })
        {
            using var image = new Bitmap(64, 64);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
            var path = Path.Combine(source, name);
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            File.SetLastWriteTime(path, when);
        }
        File.WriteAllText(Path.Combine(source, "notes.txt"), "must stay in place");
        File.WriteAllText(Path.Combine(source, "broken.jpg"), "invalid jpeg");
        var originalHashes = Directory.GetFiles(source).ToDictionary(x => x, Hash);
        var options = new ScanOptions(source, target, true, false, NamingRule.DateDevice);
        var reporter = new InlineProgress(_ => { });
        var plan = await Planner.ScanAsync(options, reporter, default);
        Check(Directory.GetFileSystemEntries(target).Length == 0, "Preview performed no writes");
        Check(plan.Count == 4 && plan.Count(x => x.Selected) == 2, "PNG handling, unknown format and damaged image classification");
        Check(plan.Where(x => x.Selected).Select(x => x.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Deterministic numbering for same-second photos");
        Check(plan.Any(x => x.Destination.EndsWith("_01.png")), "Collision sequence starts at 01");

        var chosen = plan.Where(x => x.Selected).ToList();
        await CopyExecutor.ExecuteAsync(options, chosen, data, reporter, default);
        Check(chosen.All(x => x.State == ItemState.Copied && File.Exists(x.Destination)), "Copy committed every selected file");
        Check(chosen.All(x => Hash(x.Source) == Hash(x.Destination)), "Archive hashes match source hashes");
        Check(originalHashes.All(x => Hash(x.Key) == x.Value), "Source tree preserved");
        using (var journal = new Journal(data))
        {
            Check(journal.Read().Count == 2 && journal.Read().All(x => x.State == "Committed"), "SQLite records committed operations");
            Check(journal.PendingCount() == 0, "No unfinished writes");
        }
        var repeat = await Planner.ScanAsync(options, reporter, default);
        Check(repeat.Count(x => x.State == ItemState.Duplicate) == 2, "Repeated run skips archived content including numbered paths");

        var raceTarget = Path.Combine(root, "race");
        Directory.CreateDirectory(raceTarget);
        var raceOptions = options with { Destination = raceTarget };
        var racePlan = (await Planner.ScanAsync(raceOptions, reporter, default)).Where(x => x.Selected).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(racePlan[0].Destination)!);
        File.WriteAllText(racePlan[0].Destination, "external file");
        await CopyExecutor.ExecuteAsync(raceOptions, [racePlan[0]], data, reporter, default);
        Check(File.ReadAllText(racePlan[0].Destination) == "external file" && racePlan[0].State == ItemState.Failed,
            "External destination creation is preserved");

        var cancelTarget = Path.Combine(root, "canceled");
        Directory.CreateDirectory(cancelTarget);
        var cancelOptions = options with { Destination = cancelTarget };
        var cancelPlan = (await Planner.ScanAsync(cancelOptions, reporter, default)).Where(x => x.Selected).ToList();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await CopyExecutor.ExecuteAsync(cancelOptions, cancelPlan, data, reporter, cancel.Token);
            throw new Exception("Cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Check(!Directory.EnumerateFiles(cancelTarget, "*", SearchOption.AllDirectories).Any(), "Canceled operation creates no final files");
        using var midCopyCancel = new CancellationTokenSource();
        var cancelReporter = new InlineProgress(value => { if (value.Units > 0) midCopyCancel.Cancel(); });
        try
        {
            await CopyExecutor.ExecuteAsync(cancelOptions, cancelPlan, data, cancelReporter, midCopyCancel.Token);
            throw new Exception("Mid-copy cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Check(!Directory.EnumerateFiles(cancelTarget, "*", SearchOption.AllDirectories).Any(),
            "Mid-copy cancellation cleans only its own temporary file and produces no final file");
        using (var journal = new Journal(data))
            Check(journal.Read().Any(x => x.State == "Canceled"), "Cancellation persisted in SQLite");
        try
        {
            await Planner.ScanAsync(options with { Destination = source }, reporter, default);
            throw new Exception("Nested destination was accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message == "NestedDirectories") { }
        Console.WriteLine("PASS: nested destination blocked.");

        var settings = new AppSettings { Language = "en", Theme = ThemeMode.Dark };
        settings.Save(data);
        var loaded = AppSettings.Load(data);
        Check(loaded.Language == "en" && loaded.Theme == ThemeMode.Dark, "Language and theme persisted");
        var zh = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Locales", "zh-CN.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Locales", "en.json")))!;
        Check(zh.Keys.Order().SequenceEqual(en.Keys.Order()), "Chinese and English resource keys match");
        Check(new RemainingTime().Estimate(0, 100) is null && new RemainingTime().Estimate(100, 100) == TimeSpan.Zero,
            "ETA starts unknown and finishes at zero");
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
