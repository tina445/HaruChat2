#nullable enable

using HaruChat.Runtime.Memory;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace HaruChat.Memory.Sqlite;

/// <summary>SQLite+FTS5 implementation. All database access is serialized so mobile callers have one writer.</summary>
public sealed class SqliteMemoryStore : IMemoryStore, IMemoryRetriever, IMemorySettingsStore, IAsyncDisposable
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly object ProviderGate = new();
    private static bool _providerInitialized;
    private bool _initialized;

    public SqliteMemoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = true }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await UseAsync(async connection =>
        {
            await InitializeConnectionAsync(connection, cancellationToken);
        }, cancellationToken);
    }

    private async Task InitializeConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
            if (_initialized) return;
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;", cancellationToken);
            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at_unix_ms INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS memory_sessions (
  session_id TEXT PRIMARY KEY, character_id TEXT NOT NULL, summary_text TEXT NOT NULL DEFAULT '',
  created_at_unix_ms INTEGER NOT NULL, updated_at_unix_ms INTEGER NOT NULL, expires_at_unix_ms INTEGER);
CREATE INDEX IF NOT EXISTS ix_memory_sessions_character_updated ON memory_sessions(character_id, updated_at_unix_ms DESC);
CREATE TABLE IF NOT EXISTS memory_items (
  row_id INTEGER PRIMARY KEY, memory_id TEXT NOT NULL UNIQUE, character_id TEXT NOT NULL, source_session_id TEXT,
  content TEXT NOT NULL, importance INTEGER NOT NULL CHECK (importance BETWEEN 0 AND 100),
  created_at_unix_ms INTEGER NOT NULL, updated_at_unix_ms INTEGER NOT NULL, expires_at_unix_ms INTEGER,
  FOREIGN KEY (source_session_id) REFERENCES memory_sessions(session_id) ON DELETE SET NULL);
CREATE INDEX IF NOT EXISTS ix_memory_items_retrieval ON memory_items(character_id, expires_at_unix_ms, importance DESC, updated_at_unix_ms DESC);
CREATE TABLE IF NOT EXISTS memory_settings (
  character_id TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)), retention_days INTEGER NOT NULL CHECK (retention_days > 0),
  maximum_retrieved_items INTEGER NOT NULL CHECK (maximum_retrieved_items BETWEEN 1 AND 8), maximum_prompt_tokens INTEGER NOT NULL CHECK (maximum_prompt_tokens >= 32),
  include_recent_session_summary INTEGER NOT NULL CHECK (include_recent_session_summary IN (0, 1)), updated_at_unix_ms INTEGER NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS memory_items_fts USING fts5(content, content='memory_items', content_rowid='row_id', tokenize='unicode61 remove_diacritics 2');
CREATE TRIGGER IF NOT EXISTS memory_items_ai AFTER INSERT ON memory_items BEGIN INSERT INTO memory_items_fts(rowid, content) VALUES (new.row_id, new.content); END;
CREATE TRIGGER IF NOT EXISTS memory_items_ad AFTER DELETE ON memory_items BEGIN INSERT INTO memory_items_fts(memory_items_fts, rowid, content) VALUES ('delete', old.row_id, old.content); END;
CREATE TRIGGER IF NOT EXISTS memory_items_au AFTER UPDATE OF content ON memory_items BEGIN INSERT INTO memory_items_fts(memory_items_fts, rowid, content) VALUES ('delete', old.row_id, old.content); INSERT INTO memory_items_fts(rowid, content) VALUES (new.row_id, new.content); END;", cancellationToken);
            int existing;
            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                existing = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (existing > SchemaVersion) throw new MemoryOperationException(MemoryErrorCode.InvalidData, "The memory database was created by a newer runtime.", false);
            }
            if (existing < 3)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, "ALTER TABLE memory_settings ADD COLUMN automatically_save_important_memories INTEGER NOT NULL DEFAULT 0 CHECK (automatically_save_important_memories IN (0, 1));", cancellationToken, Array.Empty<(string Name, object? Value)>(), transaction);
                await ExecuteAsync(connection, "ALTER TABLE memory_settings ADD COLUMN automatic_memory_importance_threshold INTEGER NOT NULL DEFAULT 70 CHECK (automatic_memory_importance_threshold BETWEEN 1 AND 100);", cancellationToken, Array.Empty<(string Name, object? Value)>(), transaction);
                await ExecuteAsync(connection, @"CREATE TRIGGER IF NOT EXISTS memory_items_source_session_character_insert
BEFORE INSERT ON memory_items WHEN NEW.source_session_id IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'memory source session belongs to another character')
  WHERE NOT EXISTS (SELECT 1 FROM memory_sessions s WHERE s.session_id = NEW.source_session_id AND s.character_id = NEW.character_id);
END;
CREATE TRIGGER IF NOT EXISTS memory_items_source_session_character_update
BEFORE UPDATE OF character_id, source_session_id ON memory_items WHEN NEW.source_session_id IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'memory source session belongs to another character')
  WHERE NOT EXISTS (SELECT 1 FROM memory_sessions s WHERE s.session_id = NEW.source_session_id AND s.character_id = NEW.character_id);
END;", cancellationToken, Array.Empty<(string Name, object? Value)>(), transaction);
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_migrations(version, applied_at_unix_ms) VALUES (3, $now);", cancellationToken, new[] { ("$now", (object?)Now()) }, transaction);
                await transaction.CommitAsync(cancellationToken);
            }
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_migrations(version, applied_at_unix_ms) VALUES ($version, $now);", cancellationToken, ("$version", SchemaVersion), ("$now", Now()));
            _initialized = true;
    }

    public Task UpsertSessionAsync(MemorySession session, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken);
        await using (var ownership = connection.CreateCommand())
        {
            ownership.CommandText = "SELECT character_id FROM memory_sessions WHERE session_id=$session;"; Add(ownership, "$session", session.SessionId);
            var owner = await ownership.ExecuteScalarAsync(cancellationToken) as string;
            if (owner != null && !string.Equals(owner, session.CharacterId, StringComparison.Ordinal))
                throw new MemoryOperationException(MemoryErrorCode.InvalidData, "A memory session cannot be reassigned to another character.", false);
        }
        await ExecuteAsync(connection, @"INSERT INTO memory_sessions(session_id, character_id, summary_text, created_at_unix_ms, updated_at_unix_ms, expires_at_unix_ms)
VALUES ($id, $character, $summary, $created, $updated, $expires)
ON CONFLICT(session_id) DO UPDATE SET character_id=excluded.character_id, summary_text=excluded.summary_text, updated_at_unix_ms=excluded.updated_at_unix_ms, expires_at_unix_ms=excluded.expires_at_unix_ms;", cancellationToken,
            ("$id", session.SessionId), ("$character", session.CharacterId), ("$summary", session.SummaryText), ("$created", Milliseconds(session.CreatedAt)), ("$updated", Milliseconds(session.UpdatedAt)), ("$expires", NullableMilliseconds(session.ExpiresAt)));
    }, cancellationToken);

    public Task SaveMemoryAsync(MemoryItem item, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken);
        if (!string.IsNullOrWhiteSpace(item.SourceSessionId))
        {
            await using var ownership = connection.CreateCommand();
            ownership.CommandText = "SELECT character_id FROM memory_sessions WHERE session_id=$session;"; Add(ownership, "$session", item.SourceSessionId);
            var owner = await ownership.ExecuteScalarAsync(cancellationToken) as string;
            if (owner == null || !string.Equals(owner, item.CharacterId, StringComparison.Ordinal))
                throw new MemoryOperationException(MemoryErrorCode.InvalidData, "A memory source session must belong to the same character.", false);
        }
        await ExecuteAsync(connection, @"INSERT INTO memory_items(memory_id, character_id, source_session_id, content, importance, created_at_unix_ms, updated_at_unix_ms, expires_at_unix_ms)
VALUES ($id, $character, $session, $content, $importance, $created, $updated, $expires)
ON CONFLICT(memory_id) DO UPDATE SET character_id=excluded.character_id, source_session_id=excluded.source_session_id, content=excluded.content, importance=excluded.importance, updated_at_unix_ms=excluded.updated_at_unix_ms, expires_at_unix_ms=excluded.expires_at_unix_ms;", cancellationToken,
            ("$id", item.MemoryId), ("$character", item.CharacterId), ("$session", item.SourceSessionId), ("$content", item.Content), ("$importance", item.Importance), ("$created", Milliseconds(item.CreatedAt)), ("$updated", Milliseconds(item.UpdatedAt)), ("$expires", NullableMilliseconds(item.ExpiresAt)));
    }, cancellationToken);

    public Task DeleteMemoryAsync(string characterId, string memoryId, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(memoryId)) throw new ArgumentException("Character ID and memory ID are required.");
        await EnsureInitializedAsync(connection, cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM memory_items WHERE character_id=$character AND memory_id=$id;", cancellationToken, ("$character", characterId), ("$id", memoryId));
    }, cancellationToken);

    public Task<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken); var match = ToFtsQuery(query.Text); if (match.Length == 0) return (IReadOnlyList<MemoryItem>)Array.Empty<MemoryItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT m.memory_id, m.character_id, m.source_session_id, m.content, m.importance, m.created_at_unix_ms, m.updated_at_unix_ms, m.expires_at_unix_ms
FROM memory_items_fts JOIN memory_items m ON m.row_id=memory_items_fts.rowid
WHERE memory_items_fts MATCH $match AND m.character_id=$character AND (m.expires_at_unix_ms IS NULL OR m.expires_at_unix_ms > $asOf)
ORDER BY (bm25(memory_items_fts) - (m.importance * 0.01) + (($asOf - m.updated_at_unix_ms) / 86400000.0) * 0.001) ASC, m.memory_id ASC LIMIT $limit;";
        Add(command, "$match", match); Add(command, "$character", query.CharacterId); Add(command, "$asOf", Milliseconds(query.AsOf)); Add(command, "$limit", query.MaximumResults);
        var result = new List<MemoryItem>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadItem(reader)); return (IReadOnlyList<MemoryItem>)result;
    }, cancellationToken);

    public Task<IReadOnlyList<MemorySession>> GetRecentSessionsAsync(string characterId, int maximumResults, DateTimeOffset asOf, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        if (string.IsNullOrWhiteSpace(characterId) || maximumResults <= 0) throw new ArgumentException("Character ID and result limit are required.");
        await EnsureInitializedAsync(connection, cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, character_id, summary_text, created_at_unix_ms, updated_at_unix_ms, expires_at_unix_ms FROM memory_sessions WHERE character_id=$character AND (expires_at_unix_ms IS NULL OR expires_at_unix_ms > $asOf) ORDER BY updated_at_unix_ms DESC, session_id ASC LIMIT $limit;";
        Add(command, "$character", characterId); Add(command, "$asOf", Milliseconds(asOf)); Add(command, "$limit", maximumResults);
        var result = new List<MemorySession>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new MemorySession(reader.GetString(0), reader.GetString(1), reader.GetString(2), FromMilliseconds(reader.GetInt64(3)), FromMilliseconds(reader.GetInt64(4)), reader.IsDBNull(5) ? null : FromMilliseconds(reader.GetInt64(5)))); return (IReadOnlyList<MemorySession>)result;
    }, cancellationToken);

    public Task ClearCharacterAsync(string characterId, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM memory_items WHERE character_id=$character;", cancellationToken, new[] { ("$character", (object?)characterId) }, transaction);
        await ExecuteAsync(connection, "DELETE FROM memory_sessions WHERE character_id=$character;", cancellationToken, new[] { ("$character", (object?)characterId) }, transaction); await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task<IReadOnlyList<MemorySession>> ExportSessionsAsync(string characterId, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, character_id, summary_text, created_at_unix_ms, updated_at_unix_ms, expires_at_unix_ms FROM memory_sessions WHERE character_id=$character ORDER BY created_at_unix_ms ASC, session_id ASC;"; Add(command, "$character", characterId);
        var result = new List<MemorySession>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new MemorySession(reader.GetString(0), reader.GetString(1), reader.GetString(2), FromMilliseconds(reader.GetInt64(3)), FromMilliseconds(reader.GetInt64(4)), reader.IsDBNull(5) ? null : FromMilliseconds(reader.GetInt64(5)))); return (IReadOnlyList<MemorySession>)result;
    }, cancellationToken);
    public Task<IReadOnlyList<MemoryItem>> ExportMemoriesAsync(string characterId, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT memory_id, character_id, source_session_id, content, importance, created_at_unix_ms, updated_at_unix_ms, expires_at_unix_ms FROM memory_items WHERE character_id=$character ORDER BY created_at_unix_ms ASC, memory_id ASC;"; Add(command, "$character", characterId);
        var result = new List<MemoryItem>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadItem(reader)); return (IReadOnlyList<MemoryItem>)result;
    }, cancellationToken);

    public Task DeleteExpiredAsync(DateTimeOffset asOf, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM memory_items WHERE expires_at_unix_ms IS NOT NULL AND expires_at_unix_ms <= $asOf;", cancellationToken, new[] { ("$asOf", (object?)Milliseconds(asOf)) }, transaction);
        await ExecuteAsync(connection, "DELETE FROM memory_sessions WHERE expires_at_unix_ms IS NOT NULL AND expires_at_unix_ms <= $asOf;", cancellationToken, new[] { ("$asOf", (object?)Milliseconds(asOf)) }, transaction); await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task<MemorySettings> GetSettingsAsync(string characterId, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        if (string.IsNullOrWhiteSpace(characterId)) throw new ArgumentException("Character ID is required.", nameof(characterId));
        await EnsureInitializedAsync(connection, cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, retention_days, maximum_retrieved_items, maximum_prompt_tokens, include_recent_session_summary, automatically_save_important_memories, automatic_memory_importance_threshold FROM memory_settings WHERE character_id=$character;"; Add(command, "$character", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return MemorySettings.Disabled(characterId);
        return new MemorySettings(characterId, reader.GetInt64(0) != 0, TimeSpan.FromDays(reader.GetInt32(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4) != 0, reader.GetInt64(5) != 0, reader.GetInt32(6));
    }, cancellationToken);

    public Task SaveSettingsAsync(MemorySettings settings, CancellationToken cancellationToken) => UseAsync(async connection =>
    {
        await EnsureInitializedAsync(connection, cancellationToken);
        await ExecuteAsync(connection, @"INSERT INTO memory_settings(character_id, enabled, retention_days, maximum_retrieved_items, maximum_prompt_tokens, include_recent_session_summary, automatically_save_important_memories, automatic_memory_importance_threshold, updated_at_unix_ms)
VALUES ($character, $enabled, $retention, $items, $tokens, $summary, $automatic, $threshold, $updated)
ON CONFLICT(character_id) DO UPDATE SET enabled=excluded.enabled, retention_days=excluded.retention_days, maximum_retrieved_items=excluded.maximum_retrieved_items, maximum_prompt_tokens=excluded.maximum_prompt_tokens, include_recent_session_summary=excluded.include_recent_session_summary, automatically_save_important_memories=excluded.automatically_save_important_memories, automatic_memory_importance_threshold=excluded.automatic_memory_importance_threshold, updated_at_unix_ms=excluded.updated_at_unix_ms;", cancellationToken,
            ("$character", settings.CharacterId), ("$enabled", settings.Enabled ? 1 : 0), ("$retention", (int)Math.Ceiling(settings.Retention.TotalDays)), ("$items", settings.MaximumRetrievedItems), ("$tokens", settings.MaximumPromptTokens), ("$summary", settings.IncludeRecentSessionSummary ? 1 : 0), ("$automatic", settings.AutomaticallySaveImportantMemories ? 1 : 0), ("$threshold", settings.AutomaticMemoryImportanceThreshold), ("$updated", Now()));
    }, cancellationToken);

    public ValueTask DisposeAsync() { _gate.Dispose(); return default; }
    private async Task UseAsync(Func<SqliteConnection, Task> action, CancellationToken cancellationToken) { await UseAsync<object?>(async c => { await action(c); return null; }, cancellationToken); }
    private async Task<T> UseAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken); try { EnsureProvider(); await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); return await action(connection); }
        catch (OperationCanceledException) { throw; } catch (SqliteException error) { throw Map(error); } catch (DllNotFoundException error) { throw new MemoryOperationException(MemoryErrorCode.Unavailable, "The system SQLite library is unavailable.", false, error); } finally { _gate.Release(); }
    }
    private async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken ct) { if (!_initialized) await InitializeConnectionAsync(connection, ct); }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) => await ExecuteAsync(connection, sql, ct, parameters, null);
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, (string Name, object? Value)[] parameters, SqliteTransaction? transaction)
    { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; foreach (var pair in parameters) Add(command, pair.Name, pair.Value); await command.ExecuteNonQueryAsync(ct); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static MemoryItem ReadItem(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetInt32(4), FromMilliseconds(reader.GetInt64(5)), FromMilliseconds(reader.GetInt64(6)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(7) ? null : FromMilliseconds(reader.GetInt64(7)));
    private static long Milliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds(); private static object? NullableMilliseconds(DateTimeOffset? value) => value.HasValue ? Milliseconds(value.Value) : null; private static DateTimeOffset FromMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value); private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string ToFtsQuery(string text) => string.Join(" AND ", Regex.Matches(text, "[\\p{L}\\p{N}_]+", RegexOptions.CultureInvariant).Select(x => "\"" + x.Value.Replace("\"", "\"\"") + "\""));
    private static void EnsureProvider()
    {
        lock (ProviderGate)
        {
            if (_providerInitialized) return;
            SQLitePCL.SQLite3Provider_dynamic_cdecl.Setup("sqlite3", new NativeLibraryAdapter("sqlite3"));
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
            SQLitePCL.raw.FreezeProvider(); _providerInitialized = true;
        }
    }
    private sealed class NativeLibraryAdapter : SQLitePCL.IGetFunctionPointer
    {
        private readonly IntPtr _library;
        public NativeLibraryAdapter(string libraryName)
        {
            if (NativeLibrary.TryLoad(libraryName, out _library)) return;
            var fallback = OperatingSystem.IsLinux() ? "libsqlite3.so.0" : OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib" : libraryName;
            _library = NativeLibrary.Load(fallback);
        }
        public IntPtr GetFunctionPointer(string name) => NativeLibrary.TryGetExport(_library, name, out var address) ? address : IntPtr.Zero;
    }
    private static MemoryOperationException Map(SqliteException error) => error.SqliteErrorCode switch { 5 => new(MemoryErrorCode.Busy, "The memory database is busy.", true, error), 13 => new(MemoryErrorCode.StorageFull, "The memory database is full.", false, error), 11 or 26 => new(MemoryErrorCode.Corrupt, "The memory database is corrupt.", false, error), _ => new(MemoryErrorCode.Unavailable, "The memory database operation failed.", true, error) };
}
