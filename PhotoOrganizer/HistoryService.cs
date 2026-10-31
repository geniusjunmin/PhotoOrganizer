using System.Text;
using System.Text.Json;

namespace PhotoOrganizer;

internal sealed record HistoryAction(HistoryEntry Entry, string From, string To, string Reason)
{
    public bool CanExecute => Reason.Length == 0;
}

internal static class HistoryService
{
    public static async Task<List<HistoryAction>> PreviewUndoAsync(string dataDirectory, IReadOnlyCollection<long> ids,
        CancellationToken token, IProgress<WorkProgress>? progress = null)
    {
        using var journal = new Journal(dataDirectory);
        var result = new List<HistoryAction>();
        var selected = ids.ToHashSet();
        var entries = ids.Count == 1 ? journal.Read(ids.First()) : journal.Read().Where(entry => selected.Contains(entry.Id)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new("Undoing", 0, entries.Count, 0, entries.Count, ""));
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var to = entry.Kind == "Rename" ? entry.Source :
                Path.Combine(entry.Root, ".photo-organizer-withdrawn", entry.Batch, entry.Id + "-" + Path.GetFileName(entry.Destination));
            string reason = "";
            try
            {
                if (entry.State != "Committed") reason = "NotCommitted";
                else if (entry.Root.Length == 0 || !PathSafety.Contains(entry.Root, entry.Destination)) reason = "MissingIdentity";
                else if (!await FileIdentity.MatchesAsync(entry.Destination, entry.Identity, entry.Hash, token)) reason = "Changed";
                else if (!PathSafety.Contains(entry.Root, to)) reason = "Changed";
                else if (File.Exists(to) || Directory.Exists(to)) reason = "UndoOccupied";
                else PathSafety.CheckAncestors(to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { reason = ex.Message; }
            result.Add(new(entry, entry.Destination, to, reason));
            if (clock.ElapsedMilliseconds >= 100 || result.Count == entries.Count)
            {
                progress?.Report(new("Undoing", result.Count, entries.Count, result.Count, entries.Count, Path.GetFileName(entry.Destination)));
                clock.Restart();
            }
        }
        return result;
    }

    public static async Task UndoAsync(string dataDirectory, IReadOnlyList<HistoryAction> actions,
        IProgress<WorkProgress> progress, CancellationToken token)
    {
        using var journal = new Journal(dataDirectory);
        progress.Report(new("Undoing", 0, actions.Count, 0, actions.Count, ""));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < actions.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var action = actions[index];
            var entry = journal.Read(action.Entry.Id).Single();
            if (!action.CanExecute || entry.State != "Committed") continue;
            using var operationLock = new OperationLock(entry.Root);
            // Recompute paths from the durable record rather than trusting preview paths.
            var fresh = (await PreviewUndoAsync(dataDirectory, [entry.Id], token)).Single();
            if (!fresh.CanExecute) { journal.Update(entry.Id, "Committed", detail: fresh.Reason); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(fresh.To)!);
            PathSafety.CheckAncestors(fresh.To);
            journal.PlanUndo(entry.Id, fresh.To);
            try
            {
                await FileIdentity.MoveVerifiedAsync(fresh.From, fresh.To, entry.Identity, entry.Hash, token);
                journal.Update(entry.Id, "Undone");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { journal.Update(entry.Id, "UndoPlanned", detail: ex.Message); }
            if (clock.ElapsedMilliseconds >= 100 || index == actions.Count - 1)
            {
                progress.Report(new("Undoing", index + 1, actions.Count, index + 1, actions.Count, Path.GetFileName(fresh.From)));
                clock.Restart();
            }
        }
    }

    public static async Task RecoverAsync(string dataDirectory, IProgress<WorkProgress> progress, CancellationToken token)
    {
        using var journal = new Journal(dataDirectory);
        var entries = journal.Read().Where(e => e.State is "Planned" or "Staged" or "UndoPlanned").ToList();
        progress.Report(new("Recovering", 0, entries.Count, 0, entries.Count, ""));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[index];
            try
            {
                if (entry.Root.Length == 0 || !PathSafety.Contains(entry.Root, entry.Destination))
                    throw new IOException("MissingIdentity");
                using var operationLock = new OperationLock(entry.Root);
                var current = journal.Read(entry.Id).Single();
                if (current.State != entry.State) continue;
                bool undo = entry.State == "UndoPlanned";
                string from = undo ? entry.Destination : entry.Kind == "Rename" ? entry.Source : entry.Temporary;
                string to = undo ? entry.UndoDestination : entry.Destination;
                if (to.Length == 0 || from.Length == 0 || !PathSafety.Contains(entry.Root, to) ||
                    !PathSafety.Contains(entry.Root, from)) throw new IOException("Changed");
                if (!File.Exists(from) && !File.Exists(to) && entry.State == "Planned")
                {
                    journal.Update(entry.Id, "Aborted", detail: "NoOutput");
                    continue;
                }
                if (!File.Exists(from) && await FileIdentity.MatchesAsync(to, entry.Identity, entry.Hash, token))
                    journal.Update(entry.Id, undo ? "Undone" : "Committed");
                else if (!File.Exists(to) && !Directory.Exists(to) &&
                    await FileIdentity.MatchesAsync(from, entry.Identity, entry.Hash, token))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await FileIdentity.MoveVerifiedAsync(from, to, entry.Identity, entry.Hash, token);
                    journal.Update(entry.Id, undo ? "Undone" : "Committed");
                }
                else throw new IOException("RecoveryConflict");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { journal.Update(entry.Id, entry.State, detail: ex.Message); }
            if (clock.ElapsedMilliseconds >= 100 || index == entries.Count - 1)
            {
                progress.Report(new("Recovering", index + 1, entries.Count, index + 1, entries.Count, Path.GetFileName(entry.Destination)));
                clock.Restart();
            }
        }
    }
    public static void Abandon(string dataDirectory, IReadOnlyCollection<long> ids)
    {
        using var journal = new Journal(dataDirectory);
        foreach (var entry in journal.Read().Where(e => ids.Contains(e.Id) && e.State is "Planned" or "Staged" or "UndoPlanned"))
        {
            using var operationLock = new OperationLock(entry.Root.Length == 0 ? dataDirectory : entry.Root);
            if (journal.Read(entry.Id).Single().State is "Planned" or "Staged" or "UndoPlanned")
                journal.Update(entry.Id, "Aborted", detail: "PreservedFiles");
        }
    }

    public static void Export(string dataDirectory, string path, IReadOnlyCollection<long>? ids = null)
    {
        using var journal = new Journal(dataDirectory);
        var selected = ids?.ToHashSet();
        var records = journal.Read().Where(entry => selected is null || selected.Contains(entry.Id));
        File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}
