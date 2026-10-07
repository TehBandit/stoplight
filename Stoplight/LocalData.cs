using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stoplight;

internal sealed record SavedError(string TurnId, long ObservedAt);
internal sealed record RolloutResult(string TurnId, bool? Error, bool Completed);

internal static class Paths
{
    public static string Data { get; } = Path.Combine(AppContext.BaseDirectory, "data");
    public static string CodexHome { get; } = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
}

// Windows ships this SQLite library. Open read-only, with a short busy timeout.
// No extra package or writes to Codex/VS Code databases are needed.
internal sealed class ReadOnlySqlite : IDisposable
{
    private IntPtr db;
    public ReadOnlySqlite(string path)
    {
        var rc = Native.sqlite3_open_v2(path, out db, 1, IntPtr.Zero);
        if (rc != 0) { Dispose(); throw new IOException("Could not read local chat database."); }
        Native.sqlite3_busy_timeout(db, 100);
    }
    public IEnumerable<string[]> Query(string sql)
    {
        if (Native.sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw new IOException("Could not query local chat database.");
        try
        {
            int result;
            while ((result = Native.sqlite3_step(stmt)) == 100)
            {
                var values = new string[Native.sqlite3_column_count(stmt)];
                for (var i = 0; i < values.Length; i++) values[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i)) ?? "";
                yield return values;
            }
            if (result != 101) throw new IOException("Local chat database is busy.");
        }
        finally { Native.sqlite3_finalize(stmt); }
    }
    public void Dispose() { if (db != IntPtr.Zero) { Native.sqlite3_close(db); db = IntPtr.Zero; } }
    private static class Native
    {
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);
    }
}

internal static class LocalData
{
    public static bool? ResolveError(SavedError? cached, RolloutResult result, long updated)
    {
        var keepSaved = cached != null && !result.Completed &&
            (result.TurnId == cached.TurnId || (string.IsNullOrEmpty(result.TurnId) && updated <= cached.ObservedAt));
        return keepSaved ? true : result.Error;
    }
    public static List<ChatMetadata> ReadMetadata()
    {
        if (!Directory.Exists(Paths.CodexHome)) return [];
        var file = Directory.EnumerateFiles(Paths.CodexHome, "state_*.sqlite")
            .OrderByDescending(p => int.TryParse(Path.GetFileNameWithoutExtension(p).Split('_').Last(), out var n) ? n : 0).FirstOrDefault();
        if (file == null) return [];
        using var db = new ReadOnlySqlite(file);
        return db.Query("SELECT id, title, cwd, rollout_path, updated_at, archived FROM threads WHERE source = 'vscode' ORDER BY updated_at DESC")
            .Select(row => new ChatMetadata(row[0], string.IsNullOrWhiteSpace(row[1]) ? "Codex chat" : row[1], row[2], row[3], long.Parse(row[4]), row[5] == "1")).ToList();
    }
    public static HashSet<string>? ReadUnread()
    {
        var unread = new HashSet<string>(); var found = false;
        foreach (var flavor in new[] { "Code", "Code - Insiders", "VSCodium" })
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), flavor, "User", "globalStorage", "state.vscdb");
            if (!File.Exists(file)) continue;
            using var db = new ReadOnlySqlite(file);
            foreach (var row in db.Query("SELECT value FROM ItemTable WHERE key = 'openai.chatgpt'"))
            {
                var state = JsonNode.Parse(row[0]); var atoms = state?["persisted-atom-state"];
                if (atoms is JsonValue) atoms = JsonNode.Parse(J.Text(atoms));
                if (atoms?["unread-thread-ids-by-host-v1"]?["local"] is JsonArray ids)
                { found = true; foreach (var id in ids) unread.Add(J.Text(id)); }
            }
        }
        return found ? unread : null;
    }
    // Restore only terminal error evidence from the end of an existing transcript.
    // Never treat an unfinished historical turn as a currently running process.
    public static RolloutResult? ReadLatestError(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - 256 * 1024); stream.Position = offset;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (offset > 0) reader.ReadLine();
        bool? error = null; var turnId = ""; var completed = false;
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                if (!root.TryGetProperty("type", out var kind) || kind.GetString() != "event_msg" || !root.TryGetProperty("payload", out var p)) continue;
                if (!p.TryGetProperty("type", out var type)) continue;
                switch (type.GetString())
                {
                    case "task_started": case "task_complete": case "turn_aborted":
                        error = false; completed = type.GetString() != "task_started";
                        turnId = p.TryGetProperty("turn_id", out var id) ? id.GetString() ?? "" : "";
                        break;
                    case "error":
                        if ((p.TryGetProperty("will_retry", out var retry) || p.TryGetProperty("willRetry", out retry)) && retry.ValueKind == JsonValueKind.False) error = true;
                        break;
                }
            }
            catch (JsonException) { /* A concurrently appended last line may be incomplete. */ }
        }
        return new(turnId, error, completed);
    }
}

internal sealed class ErrorCache
{
    private readonly string path = Path.Combine(Paths.Data, "errors.json");
    private string previous = "";
    private readonly object gate = new();
    public Dictionary<string, SavedError> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, SavedError>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
    public void Save(MonitorView view)
    {
        // Store IDs and error flags only, never prompts, replies, or tool results.
        lock (gate)
        {
            var json = JsonSerializer.Serialize(view.Chats.Where(c => c.Host == "local" && c.Error)
                .ToDictionary(c => c.Id, c => new SavedError(c.LatestTurnId, c.Updated.ToUnixTimeSeconds())));
            if (json == previous) return;
            Directory.CreateDirectory(Paths.Data); var temp = path + ".tmp";
            File.WriteAllText(temp, json); File.Move(temp, path, true); previous = json;
        }
    }
}
