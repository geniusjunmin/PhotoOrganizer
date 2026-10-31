using Microsoft.Data.Sqlite;
using PhotoOrganizer;

internal static class FailureAndMigrationTests
{
    private sealed class Reporter(Action<WorkProgress>? callback = null) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => callback?.Invoke(value); }
    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    public static async Task RunAsync(string root)
    {
        string source = Path.Combine(root, "source");
        string output = Path.Combine(root, "disk-full-target");
        string data = Path.Combine(root, "disk-full-data");
        Directory.CreateDirectory(output);
        var options = new ScanOptions(source, output, true, false, NamingRule.DateDevice);
        var plan = (await Planner.ScanAsync(options, new Reporter(), default)).Where(x => x.Selected).ToList();
        var fault = new Reporter(value =>
        {
            if (value.Units > 0) throw new IOException("Injected disk full", unchecked((int)0x80070070));
        });
        try
        {
            await CopyExecutor.ExecuteAsync(options, plan, data, fault, default);
            throw new Exception("Disk full did not stop the batch");
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 112) { }
        Check(plan[0].State == ItemState.Failed && plan[1].State == ItemState.Fallback &&
            !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(),
            "Fault-injected disk full stops the batch and removes its incomplete temporary copy");

        string duplicates = Path.Combine(root, "batch-duplicates");
        Directory.CreateDirectory(duplicates);
        File.Copy(Path.Combine(source, "a.png"), Path.Combine(duplicates, "first.png"));
        File.Copy(Path.Combine(source, "a.png"), Path.Combine(duplicates, "second.png"));
        var duplicatesPlan = await Planner.ScanAsync(options with { Source = duplicates }, new Reporter(), default);
        Check(duplicatesPlan.Count(x => x.Selected) == 1 && duplicatesPlan.Count(x => x.State == ItemState.Duplicate) == 1,
            "Same-batch identical target candidates are deduplicated before writes");

        string readonlyTarget = Path.Combine(root, "readonly-target");
        Directory.CreateDirectory(readonlyTarget);
        string readonlyFile = Path.Combine(duplicates, "first.png");
        File.SetAttributes(readonlyFile, FileAttributes.ReadOnly);
        var readonlyOptions = options with { Source = duplicates, Destination = readonlyTarget };
        var readonlyPlan = (await Planner.ScanAsync(readonlyOptions, new Reporter(), default)).Where(x => x.Selected).ToList();
        await CopyExecutor.ExecuteAsync(readonlyOptions, readonlyPlan, data, new Reporter(), default);
        Check(readonlyPlan.All(x => x.State == ItemState.Copied) && (File.GetAttributes(readonlyFile) & FileAttributes.ReadOnly) != 0,
            "Read-only originals can be copied without changing their attributes");

        string linksSource = Path.Combine(root, "links-source");
        string linksTarget = Path.Combine(root, "links-target");
        Directory.CreateDirectory(linksSource);
        Directory.CreateDirectory(linksTarget);
        Directory.CreateSymbolicLink(Path.Combine(linksSource, "linked"), source);
        var linked = await Planner.ScanAsync(options with { Source = linksSource, Destination = linksTarget }, new Reporter(), default);
        Check(linked.Count == 0, "Source directory links are not traversed");
        Directory.CreateSymbolicLink(Path.Combine(linksTarget, "2024"), Path.Combine(root, "source"));
        var unsafePlan = await Planner.ScanAsync(options with { Destination = linksTarget }, new Reporter(), default);
        Check(unsafePlan.Where(x => x.Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).All(x => !x.Selected),
            "Linked archive ancestors are rejected during preview");

        string legacy = Path.Combine(root, "schema-v1");
        Directory.CreateDirectory(legacy);
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(legacy, "history.db") }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                CREATE TABLE operations(id INTEGER PRIMARY KEY,batch TEXT NOT NULL,created TEXT NOT NULL,
                    source TEXT NOT NULL,destination TEXT NOT NULL,temporary TEXT NOT NULL,length INTEGER NOT NULL,
                    hash TEXT NOT NULL DEFAULT '',state TEXT NOT NULL,detail TEXT NOT NULL DEFAULT '');
                INSERT INTO operations VALUES(1,'legacy','2024-01-01T00:00:00+00:00','source','destination','',10,'hash','Committed','');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        using (var journal = new Journal(legacy)) Check(journal.Read().Count == 1, "Schema v1 history migrated without losing records");
        var legacyUndo = await HistoryService.PreviewUndoAsync(legacy, [1], default);
        Check(legacyUndo.Single().Reason == "MissingIdentity", "Legacy records without file identity remain protected from automatic undo");
        string future = Path.Combine(root, "schema-future");
        Directory.CreateDirectory(future);
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(future, "history.db") }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }
        try { using var journal = new Journal(future); throw new Exception("Newer schema accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "NewerHistory") { }
        Console.WriteLine("PASS: newer history schemas are rejected.");

        var clock = new Clock();
        var eta = new RemainingTime(clock);
        Check(eta.Estimate(10, 100) is null, "ETA withheld until timing sample is sufficient");
        clock.Ticks = TimeSpan.FromSeconds(5).Ticks;
        Check(eta.Estimate(20, 100) == TimeSpan.FromSeconds(20), "ETA computes remaining work from elapsed phase time");
        Check(eta.Estimate(100, 100) == TimeSpan.Zero, "ETA completes at zero");
        Check(!Diagnostics.Create(new AppSettings()).Contains(root, StringComparison.OrdinalIgnoreCase),
            "Diagnostics omit workspace and photo paths");
        string owned = Path.Combine(root, "owned.tmp");
        File.WriteAllText(owned, "owned");
        string identity = FileIdentity.Read(owned);
        File.Delete(owned);
        File.WriteAllText(owned, "external replacement");
        Check(!FileIdentity.DeleteOwnedTemporary(owned, identity) && File.ReadAllText(owned) == "external replacement",
            "Temporary cleanup preserves a replacement file with a different identity");
        Check(FileIdentity.DeleteOwnedTemporary(owned, FileIdentity.Read(owned)) && !File.Exists(owned),
            "Temporary cleanup deletes only its verified file handle");
        var location = FileLocator.CreateStartInfo(Path.Combine(source, "a.png"));
        Check(Path.GetFileName(location.FileName) == "explorer.exe" && location.ArgumentList.Count == 2 &&
            location.ArgumentList[1] == Path.Combine(source, "a.png"),
            "File location uses Explorer with a separate full-path argument");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
