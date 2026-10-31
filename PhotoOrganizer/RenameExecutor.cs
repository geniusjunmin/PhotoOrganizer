namespace PhotoOrganizer;

internal static class RenameExecutor
{
    public static async Task ExecuteAsync(ScanOptions options, IReadOnlyList<PlanItem> items,
        string dataDirectory, IProgress<WorkProgress> progress, CancellationToken token)
    {
        if (options.Mode != OperationMode.Rename) throw new InvalidOperationException("Changed");
        PathSafety.Validate(options);
        using var operationLock = new OperationLock(options.Source);
        using var journal = new Journal(dataDirectory);
        var batch = Guid.NewGuid().ToString("N");
        long total = items.Sum(x => Math.Max(1, x.Length));
        long processed = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress.Report(new("Renaming", 0, items.Count, 0, total, ""));
        for (int index = 0; index < items.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[index];
            long previous = processed;
            long id = 0;
            bool moved = false;
            try
            {
                if (!PathSafety.Contains(options.Source, item.Source) ||
                    !PathSafety.Normalize(Path.GetDirectoryName(item.Source)!).Equals(
                        PathSafety.Normalize(Path.GetDirectoryName(item.Destination)!), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Changed");
                var snapshot = new FileInfo(item.Source);
                if (!snapshot.Exists || snapshot.Length != item.Length || snapshot.LastWriteTimeUtc != item.LastWriteUtc)
                    throw new IOException("Changed");
                id = journal.Begin(batch, item, "", "Rename", PathSafety.Normalize(options.Source));
                journal.Update(id, "Planned", item.ContentHash, identity: item.Identity);
                await FileIdentity.MoveVerifiedAsync(item.Source, item.Destination, item.Identity, item.ContentHash, token,
                    count =>
                    {
                        processed += count;
                        if (clock.ElapsedMilliseconds < 100) return;
                        progress.Report(new("Renaming", index, items.Count, processed, total, Path.GetFileName(item.Source)));
                        clock.Restart();
                    });
                moved = true;
                journal.Update(id, "Committed");
                item.State = ItemState.Renamed;
                item.Selected = false;
            }
            catch (OperationCanceledException)
            {
                if (id != 0) journal.Update(id, "Canceled");
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (id != 0 && !moved) journal.Update(id, "JournalFailed", detail: ex.Message);
                item.State = ItemState.Failed;
                item.Selected = false;
                item.Detail = ex.Message;
            }
            processed = previous + Math.Max(1, item.Length);
            if (clock.ElapsedMilliseconds >= 100 || index == items.Count - 1)
            {
                progress.Report(new("Renaming", index + 1, items.Count, processed, total, Path.GetFileName(item.Source)));
                clock.Restart();
            }
        }
    }
}
