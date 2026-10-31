using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PhotoOrganizer;

internal static class CopyExecutor
{
    public static async Task ExecuteAsync(ScanOptions options, IReadOnlyList<PlanItem> items,
        string dataDirectory, IProgress<WorkProgress> progress, CancellationToken token)
    {
        PathSafety.Validate(options);
        var destination = PathSafety.Normalize(options.Destination);
        using var operationLock = new OperationLock(destination);
        {
            using var journal = new Journal(dataDirectory);
            var batch = Guid.NewGuid().ToString("N");
            long total = items.Sum(x => x.Length * 2);
            long processed = 0;
            int completed = 0;
            var clock = Stopwatch.StartNew();
            progress.Report(new("Copying", 0, items.Count, 0, total, ""));
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var previous = processed;
                long id = 0;
                string? temporary = null;
                bool committed = false;
                string temporaryIdentity = "";
                bool reportedFirstChunk = false;
                try
                {
                    temporary = Path.Combine(destination, ".photo-organizer-staging",
                        batch + "-" + Guid.NewGuid().ToString("N") + ".tmp");
                    id = journal.Begin(batch, item, temporary, root: destination);
                    var info = new FileInfo(item.Source);
                    PathSafety.CheckAncestors(item.Source);
                    if (!PathSafety.Contains(options.Source, item.Source) ||
                        !PathSafety.Contains(destination, item.Destination) ||
                        !info.Exists || info.Length != item.Length || info.LastWriteTimeUtc != item.LastWriteUtc ||
                        (!string.IsNullOrEmpty(item.Identity) && FileIdentity.Read(item.Source) != item.Identity) ||
                        File.Exists(item.Destination) || Directory.Exists(item.Destination))
                        throw new IOException("Changed");
                    var folder = Path.GetDirectoryName(item.Destination)!;
                    PathSafety.CheckAncestors(folder);
                    Directory.CreateDirectory(folder);
                    PathSafety.CheckAncestors(folder);
                    var staging = Path.Combine(destination, ".photo-organizer-staging");
                    PathSafety.CheckAncestors(staging);
                    Directory.CreateDirectory(staging);
                    using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1024 * 1024];
                    using (var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true))
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, buffer.Length, true))
                    {
                        temporaryIdentity = FileIdentity.Read(target.SafeFileHandle);
                        journal.Update(id, "Planned", identity: temporaryIdentity);
                        int count;
                        while ((count = await source.ReadAsync(buffer, token)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, count), token);
                            sourceHash.AppendData(buffer, 0, count);
                            processed += count;
                            Report(!reportedFirstChunk && completed == 0);
                            reportedFirstChunk = true;
                        }
                        await target.FlushAsync(token);
                        target.Flush(true);
                    }
                    byte[] hash = sourceHash.GetHashAndReset();
                    info.Refresh();
                    if (info.Length != item.Length || info.LastWriteTimeUtc != item.LastWriteUtc) throw new IOException("Changed");
                    token.ThrowIfCancellationRequested();
                    File.SetLastWriteTimeUtc(temporary, item.LastWriteUtc);
                    string hashText = Convert.ToHexString(hash);
                    string identity = FileIdentity.Read(temporary);
                    journal.Update(id, "Staged", hashText, identity: identity);
                    PathSafety.CheckAncestors(folder);
                    await FileIdentity.MoveVerifiedAsync(temporary, item.Destination, identity, hashText, token,
                        count => { processed += count; Report(false); });
                    committed = true;
                    journal.Update(id, "Committed", hashText);
                    item.State = ItemState.Copied;
                    item.Selected = false;
                }
                catch (OperationCanceledException)
                {
                    item.State = ItemState.Canceled;
                    if (id != 0) journal.Update(id, "Canceled");
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    item.State = ItemState.Failed;
                    item.Selected = false;
                    item.Detail = ex.Message;
                    if (id != 0 && !committed) journal.Update(id, "JournalFailed", detail: ex.Message);
                    // A completed rename followed by a database failure keeps the Staged record for reconciliation.
                    if (ex is IOException && (ex.HResult & 0xffff) is 15 or 21 or 39 or 112 or 1167) throw;
                }
                finally
                {
                    if (!committed && temporary is not null && temporaryIdentity.Length > 0 && File.Exists(temporary))
                    {
                        try
                        {
                            if (!FileIdentity.DeleteOwnedTemporary(temporary, temporaryIdentity))
                                journal.Update(id, "Planned", detail: "TemporaryChanged");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                        { journal.Update(id, "Planned", detail: "TemporaryChanged"); }
                    }
                }
                processed = previous + item.Length * 2;
                completed++;
                Report(completed == items.Count);

                void Report(bool force)
                {
                    if (!force && clock.ElapsedMilliseconds < 100) return;
                    clock.Restart();
                    progress.Report(new("Copying", completed, items.Count, processed, total, Path.GetFileName(item.Source)));
                }
            }
        }
    }
}
