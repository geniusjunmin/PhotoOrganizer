using Microsoft.Data.Sqlite;

namespace PhotoOrganizer;

internal sealed record HistoryEntry(long Id, string Batch, string Created, string Source, string Destination, string State, string Detail)
{
    public string Kind { get; init; } = "Copy";
    public string Root { get; init; } = "";
    public string Temporary { get; init; } = "";
    public string Identity { get; init; } = "";
    public string Hash { get; init; } = "";
    public string UndoDestination { get; init; } = "";
}

internal sealed class Journal : IDisposable
{
    private readonly SqliteConnection connection;
    public Journal(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "history.db"), Pooling = false
        }.ToString());
        connection.Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        int version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 2) { connection.Dispose(); throw new InvalidOperationException("NewerHistory"); }
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS operations (
                id INTEGER PRIMARY KEY, batch TEXT NOT NULL, created TEXT NOT NULL,
                source TEXT NOT NULL, destination TEXT NOT NULL, temporary TEXT NOT NULL,
                length INTEGER NOT NULL, hash TEXT NOT NULL DEFAULT '',
                state TEXT NOT NULL, detail TEXT NOT NULL DEFAULT ''
            );
            """;
        command.ExecuteNonQuery();
        if (version < 2)
        {
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) < 2)
            {
                foreach (var name in new[] { "kind", "root", "identity", "undo_destination" })
                {
                    command.CommandText = $"ALTER TABLE operations ADD COLUMN {name} TEXT NOT NULL DEFAULT ''";
                    command.ExecuteNonQuery();
                }
                command.CommandText = "UPDATE operations SET kind='Copy'; PRAGMA user_version=2;";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    public long Begin(string batch, PlanItem item, string temporary, string kind = "Copy", string root = "")
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(batch,created,source,destination,temporary,length,state,kind,root)
            VALUES($batch,$created,$source,$destination,$temporary,$length,'Planned',$kind,$root);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$batch", batch);
        command.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$source", item.Source);
        command.Parameters.AddWithValue("$destination", item.Destination);
        command.Parameters.AddWithValue("$temporary", temporary);
        command.Parameters.AddWithValue("$length", item.Length);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$root", root);
        return (long)command.ExecuteScalar()!;
    }
    public void Update(long id, string state, string? hash = null, string detail = "", string? identity = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE operations SET state=$state,hash=COALESCE($hash,hash),identity=COALESCE($identity,identity),detail=$detail WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", (object?)identity ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }
    public List<HistoryEntry> Read(long? id = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,batch,created,source,destination,state,detail,kind,root,temporary,identity,hash,undo_destination FROM operations" +
            (id.HasValue ? " WHERE id=$id" : "") + " ORDER BY id DESC";
        if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value);
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
            entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6))
            {
                Kind = reader.GetString(7), Root = reader.GetString(8), Temporary = reader.GetString(9),
                Identity = reader.GetString(10), Hash = reader.GetString(11), UndoDestination = reader.GetString(12)
            });
        return entries;
    }
    public int PendingCount()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operations WHERE state IN ('Planned','Staged','UndoPlanned')";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void PlanUndo(long id, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE operations SET state='UndoPlanned',undo_destination=$path WHERE id=$id AND state='Committed'";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$path", path);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Changed");
    }
    public void Clear()
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM operations WHERE state IN ('Planned','Staged','UndoPlanned')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidOperationException("PendingHistory");
        command.CommandText = "DELETE FROM operations";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
    public void Dispose() => connection.Dispose();
}
