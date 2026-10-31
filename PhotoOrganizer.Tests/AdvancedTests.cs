using System.Diagnostics;
using PhotoOrganizer;

internal static class AdvancedTests
{
    private sealed class Reporter : IProgress<WorkProgress> { public void Report(WorkProgress value) { } }
    private static readonly Reporter Progress = new();

    public static async Task RunAsync(string root)
    {
        var testRoot = Path.Combine(root, "advanced");
        Directory.CreateDirectory(testRoot);
        string fixture = Path.Combine(root, "source", "a.png");
        string renameRoot = MakeDirectory(testRoot, "rename");
        File.Copy(fixture, Path.Combine(renameRoot, "original.png"));
        var renameOptions = new ScanOptions(renameRoot, renameRoot, true, false, NamingRule.Legacy, OperationMode.Rename);
        string data = MakeDirectory(testRoot, "data");
        var plan = (await Planner.ScanAsync(renameOptions, Progress, default)).Where(x => x.Selected).ToList();
        Check(plan.Count == 1 && plan[0].Destination.Contains("UnknownDevice_X"), "Legacy fallback naming");
        await RenameExecutor.ExecuteAsync(renameOptions, plan, data, Progress, default);
        Check(plan[0].State == ItemState.Renamed && !File.Exists(plan[0].Source), "In-place rename executed");
        Check((await Planner.ScanAsync(renameOptions, Progress, default)).All(x => x.State == ItemState.Unchanged),
            "In-place rename is idempotent");
        long id;
        using (var journal = new Journal(data)) id = journal.Read().Single().Id;
        var undo = await HistoryService.PreviewUndoAsync(data, [id], default);
        Check(undo.Single().CanExecute, "Rename undo can be previewed");
        await HistoryService.UndoAsync(data, undo, Progress, default);
        Check(File.Exists(plan[0].Source) && !File.Exists(plan[0].Destination), "Rename undo restores original path");

        File.Copy(fixture, Path.Combine(renameRoot, "pair.png"));
        File.WriteAllText(Path.Combine(renameRoot, "pair.aae"), "companion");
        Check((await Planner.ScanAsync(renameOptions, Progress, default)).Single(x => x.Source.EndsWith("pair.png")).State == ItemState.Companion,
            "Paired sidecars prevent in-place renaming");
        plan = (await Planner.ScanAsync(renameOptions, Progress, default)).Where(x => x.Selected).ToList();
        await RenameExecutor.ExecuteAsync(renameOptions, plan, data, Progress, default);
        using (var journal = new Journal(data)) id = journal.Read().First(x => x.State == "Committed").Id;
        File.WriteAllText(plan[0].Source, "new occupant");
        Check(!(await HistoryService.PreviewUndoAsync(data, [id], default)).Single().CanExecute, "Undo preserves an occupied original name");
        File.Delete(plan[0].Source);
        byte[] sameBytes = File.ReadAllBytes(plan[0].Destination);
        File.Delete(plan[0].Destination);
        File.WriteAllBytes(plan[0].Destination, sameBytes);
        Check(!(await HistoryService.PreviewUndoAsync(data, [id], default)).Single().CanExecute,
            "Undo rejects a replacement file even when its bytes match");

        string copyRoot = MakeDirectory(testRoot, "copy");
        var copyOptions = new ScanOptions(Path.Combine(root, "source"), copyRoot, true, false, NamingRule.Original);
        var copies = (await Planner.ScanAsync(copyOptions, Progress, default)).Where(x => x.Selected).ToList();
        await CopyExecutor.ExecuteAsync(copyOptions, copies, data, Progress, default);
        long[] copyIds;
        using (var journal = new Journal(data)) copyIds = journal.Read().Where(x => x.Kind == "Copy" && x.State == "Committed").Select(x => x.Id).ToArray();
        var copyUndo = await HistoryService.PreviewUndoAsync(data, copyIds, default);
        await HistoryService.UndoAsync(data, copyUndo, Progress, default);
        Check(copyUndo.All(x => File.Exists(x.To) && !File.Exists(x.From)), "Copy undo moves verified copies into withdrawal storage");
        Check(File.Exists(fixture), "Copy undo preserves source files");
        HistoryService.Export(data, Path.Combine(testRoot, "history.json"));
        Check(File.ReadAllText(Path.Combine(testRoot, "history.json")).Contains("Undone"), "History export includes undo state");
        HistoryService.Export(data, Path.Combine(testRoot, "selected-history.json"), [copyIds[0]]);
        Check(System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(testRoot, "selected-history.json"))).RootElement.GetArrayLength() == 1,
            "History export respects selected operation IDs");

        string years = MakeDirectory(testRoot, "years");
        foreach (string year in new[] { "1900", "1901", "2024", "3000", "misc" })
            File.Copy(fixture, Path.Combine(MakeDirectory(years, year), "sample.png"));
        File.Copy(fixture, Path.Combine(MakeDirectory(Path.Combine(years, "2024"), "nested"), "nested.png"));
        var yearOptions = new ScanOptions(years, copyRoot, true, false, NamingRule.Original, YearFolders: true);
        var yearPlan = await Planner.ScanAsync(yearOptions, Progress, default);
        Check(yearPlan.Count == 2, "Year scope only includes direct 1901–2999 folders");

        using (var held = new OperationLock(copyRoot))
        {
            try
            {
                using var duplicate = new OperationLock(copyRoot);
                throw new Exception("Concurrent writer was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message == "BusyDestination") { }
        }
        Console.WriteLine("PASS: concurrent writer excluded.");

        await CrashRecoveryTestAsync(testRoot, fixture, "stage");
        await CrashRecoveryTestAsync(testRoot, fixture, "rename");
        await RecoveryConflictTestAsync(testRoot, fixture);
    }

    private static async Task CrashRecoveryTestAsync(string root, string fixture, string mode)
    {
        string crashRoot = MakeDirectory(root, "crash-" + mode);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(AdvancedTests).Assembly.Location);
        start.ArgumentList.Add("--crash-" + mode);
        start.ArgumentList.Add(crashRoot);
        start.ArgumentList.Add(fixture);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 73, "Worker terminated between file operation and completion bookkeeping: " + mode);
        string data = Path.Combine(crashRoot, "data");
        await HistoryService.RecoverAsync(data, Progress, default);
        using var journal = new Journal(data);
        Check(journal.Read().Single().State == "Committed" && File.Exists(journal.Read().Single().Destination),
            "Recovery reconciles real process interruption: " + mode);
        var undo = await HistoryService.PreviewUndoAsync(data, [journal.Read().Single().Id], default);
        await HistoryService.UndoAsync(data, undo, Progress, default);
        Check(File.Exists(undo.Single().To), "Recovered operations remain undoable: " + mode);
    }

    public static async Task CrashWorkerAsync(string mode, string root, string fixture)
    {
        string archive = MakeDirectory(root, "archive");
        using var operationLock = new OperationLock(archive);
        string source = Path.Combine(archive, "original.png");
        File.Copy(fixture, source);
        string target = Path.Combine(archive, "final.png");
        string temporary = Path.Combine(archive, "staged.tmp");
        var item = new PlanItem { Source = source, Destination = target, Length = new FileInfo(source).Length };
        using var journal = new Journal(Path.Combine(root, "data"));
        string from = mode == "stage" ? temporary : source;
        if (mode == "stage") File.Copy(source, temporary);
        string identity = FileIdentity.Read(from);
        string hash = await FileIdentity.HashAsync(from, default);
        long id = journal.Begin("crash-fixture", item, mode == "stage" ? temporary : "", mode == "stage" ? "Copy" : "Rename", archive);
        journal.Update(id, mode == "stage" ? "Staged" : "Planned", hash, identity: identity);
        if (mode == "rename") await FileIdentity.MoveVerifiedAsync(source, target, identity, hash, default);
        Environment.Exit(73);
    }

    private static async Task RecoveryConflictTestAsync(string root, string fixture)
    {
        string archive = MakeDirectory(root, "recovery-conflict");
        string data = MakeDirectory(root, "conflict-data");
        string source = Path.Combine(archive, "source.png");
        string target = Path.Combine(archive, "target.png");
        File.Copy(fixture, source);
        File.WriteAllText(target, "external occupant");
        using (var journal = new Journal(data))
        {
            long id = journal.Begin("conflict", new PlanItem { Source = source, Destination = target }, "", "Rename", archive);
            journal.Update(id, "Planned", await FileIdentity.HashAsync(source, default), identity: FileIdentity.Read(source));
        }
        await HistoryService.RecoverAsync(data, Progress, default);
        Check(File.ReadAllText(target) == "external occupant" && File.Exists(source), "Recovery conflict preserves both files");
        long[] ids;
        using (var journal = new Journal(data))
        {
            ids = journal.Read().Select(x => x.Id).ToArray();
            try { journal.Clear(); throw new Exception("Pending history was cleared"); }
            catch (InvalidOperationException ex) when (ex.Message == "PendingHistory") { }
        }
        HistoryService.Abandon(data, ids);
        using (var journal = new Journal(data)) { journal.Clear(); Check(journal.Read().Count == 0, "History can be cleared after explicit recovery abandonment"); }
    }

    private static string MakeDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
