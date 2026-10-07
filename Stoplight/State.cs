using System.Text.Json.Nodes;

namespace Stoplight;

internal static class J
{
    public static string Text(JsonNode? n, string fallback = "") => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static long Number(JsonNode? n, long fallback = 0) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : fallback;
}

internal sealed record ChatMetadata(string Id, string Title, string Cwd, string Rollout, long Updated, bool Archived);
internal sealed record ChatView(string Key, string Id, string Host, string Title, string Cwd, bool Live,
    bool Running, bool Unread, bool Error, string Status, DateTimeOffset Updated, string LatestTurnId = "");
internal sealed record MonitorView(bool Connected, string Connection, string? Warning, int Running, int Unread,
    int Errors, int LiveChats, long Snapshots, long Patches, long ReadChanges, IReadOnlyList<ChatView> Chats);

internal sealed class ChatEntry
{
    public required string Id;
    public string Host = "local", Title = "Codex chat", Cwd = "", Owner = "", Runtime = "unknown";
    public bool Eligible, Archived, Live, Unread, Error, Running;
    public bool Seeded, Excluded;
    public string LatestTurnId = "";
    public DateTimeOffset Updated = DateTimeOffset.UtcNow;
    public JsonNode? State;
    public long Revision;
}

internal sealed class ChatStore
{
    private readonly Dictionary<string, ChatEntry> chats = [];
    private readonly object gate = new();
    private bool connected;
    private string connection = "Connecting to VS Code…";
    private string? warning;
    private string? protocolWarning;
    private long snapshots, patches, readChanges;
    public static string Key(string host, string id) => host + ":" + id;
    private ChatEntry Get(string host, string id)
    {
        var key = Key(host, id);
        if (!chats.TryGetValue(key, out var chat)) chats[key] = chat = new() { Id = id, Host = host };
        return chat;
    }
    public void Connection(bool isConnected, string message)
    {
        lock (gate)
        {
            connected = isConnected; connection = message;
            if (isConnected) protocolWarning = null;
            if (!isConnected) foreach (var c in chats.Values) { c.Live = false; c.Running = false; c.State = null; c.Owner = ""; }
        }
    }
    public void Warning(string? message) { lock (gate) warning = message; }
    public void UnsupportedProtocol()
    {
        lock (gate)
        {
            protocolWarning = "This Codex version uses a different status protocol. An app update is needed.";
            connected = false; connection = "Codex protocol update required";
            foreach (var c in chats.Values) { c.Live = false; c.Running = false; c.State = null; }
        }
    }
    public void Seed(IEnumerable<ChatMetadata> metadata, IReadOnlySet<string>? unread)
    {
        lock (gate)
        {
            var records = metadata.ToArray(); var known = records.Select(m => m.Id).ToHashSet();
            foreach (var c in chats.Values.Where(c => c.Host == "local" && c.Seeded && !known.Contains(c.Id)))
            { c.Archived = true; c.Live = false; c.Running = false; c.State = null; }
            foreach (var m in records)
            {
                var c = Get("local", m.Id); c.Eligible = !c.Excluded; c.Archived = m.Archived; c.Seeded = true;
                if (c.State == null) { c.Title = m.Title; c.Cwd = m.Cwd; c.Updated = DateTimeOffset.FromUnixTimeSeconds(m.Updated); }
                // Refresh persisted read state only for chats with no live owner; broadcasts win for live chats.
                if (unread != null && !c.Live) c.Unread = unread.Contains(m.Id);
            }
        }
    }
    public void RestoreError(string id, bool error, string turnId = "")
    {
        lock (gate)
        {
            var c = Get("local", id);
            if (!c.Live) { c.Error = error; if (!string.IsNullOrEmpty(turnId)) c.LatestTurnId = turnId; }
        }
    }
    public void Read(string host, string id, bool unread)
    {
        lock (gate) { Get(host, id).Unread = unread; readChanges++; }
    }
    public void Archive(string host, string id, bool archived) { lock (gate) Get(host, id).Archived = archived; }
    public void OwnerDisconnected(string owner)
    {
        lock (gate) foreach (var c in chats.Values.Where(c => c.Owner == owner))
        { c.Live = false; c.Running = false; c.State = null; c.Owner = ""; }
    }
    public bool Snapshot(string host, string id, string owner, long revision, JsonNode state)
    {
        lock (gate)
        {
            var c = Get(host, id);
            // A new owner's snapshot replaces the prior revision sequence.
            if (c.Owner == owner && c.State != null && revision < c.Revision) return true;
            c.State = state.DeepClone(); c.Owner = owner; c.Revision = revision; c.Live = true;
            Extract(c); snapshots++; return true;
        }
    }
    public bool Patch(string host, string id, string owner, long baseRevision, long revision, JsonArray changes)
    {
        lock (gate)
        {
            var c = Get(host, id);
            if (c.Owner != owner) return false;
            if (revision <= c.Revision) return true; // Duplicate frame.
            if (c.State == null || c.Revision != baseRevision) return false;
            try
            {
                foreach (var change in changes) JsonPatches.Apply(c.State, change!);
                c.Revision = revision; Extract(c); patches++; return true;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            { c.State = null; c.Live = false; c.Running = false; return false; }
        }
    }
    private static void Extract(ChatEntry c)
    {
        var state = c.State!;
        var source = state["source"];
        var sourceText = J.Text(source);
        var isSubagent = source is JsonObject so && (so.ContainsKey("subAgent") || so.ContainsKey("subagent"));
        c.Excluded = state["parentThreadId"] != null || isSubagent || J.Bool(state["sideConversation"]) || J.Bool(state["ephemeral"]);
        if (c.Excluded) c.Eligible = false;
        else if (sourceText == "vscode" || J.Text(state["originator"]) == "codex_vscode") c.Eligible = true;
        // Metadata may already establish eligibility when older snapshots omit source.
        c.Title = J.Text(state["title"], c.Title); c.Cwd = J.Text(state["cwd"], c.Cwd);
        c.Unread = J.Bool(state["hasUnreadTurn"]); c.Runtime = J.Text(state["threadRuntimeStatus"]?["type"], "unknown");
        var turns = new List<JsonNode>();
        if (state["turnHistory"]?["history"]?["entitiesByKey"] is JsonObject entities)
        {
            // Canonical islands specify chronological order; dictionary insertion order need not.
            if (state["turnHistory"]?["history"]?["islands"] is JsonArray islands)
                foreach (var island in islands) if (island?["entries"] is JsonArray entries)
                    foreach (var entry in entries) if (entities[J.Text(entry?["value"])] is { } turn) turns.Add(turn);
            if (turns.Count == 0) turns.AddRange(entities.Select(x => x.Value).OfType<JsonNode>()
                .OrderBy(x => J.Number(x["turnStartedAtMs"])));
        }
        else if (state["turns"] is JsonArray legacy) turns.AddRange(legacy.OfType<JsonNode>());
        var latest = turns.LastOrDefault(t => !string.IsNullOrEmpty(J.Text(t["turnId"])) || !string.IsNullOrEmpty(J.Text(t["status"])));
        var status = J.Text(latest?["status"]);
        c.LatestTurnId = J.Text(latest?["turnId"]);
        c.Running = c.Runtime == "active" || status == "inProgress";
        c.Error = c.Runtime == "systemError" || status == "failed" ||
            (latest?["items"] is JsonArray items && items.Any(i => J.Text(i?["type"]) == "error" &&
                i?["willRetry"] is JsonValue retry && retry.TryGetValue<bool>(out var b) && !b));
        c.Updated = DateTimeOffset.UtcNow;
    }
    public MonitorView View()
    {
        lock (gate)
        {
            var rows = chats.Select(x => (x.Key, Chat: x.Value)).Where(x => x.Chat.Eligible && !x.Chat.Archived)
                .Select(x => { var c = x.Chat; var flags = c.State?["threadRuntimeStatus"]?["activeFlags"] as JsonArray;
                    var waiting = flags?.Any(f => J.Text(f) is "waitingOnApproval" or "waitingOnUserInput") == true;
                    var status = c.Error ? "Error" : c.Running ? waiting ? "Waiting for you" : "Running" : c.Unread ? "Unread reply" : c.Live ? "Ready" : "Saved chat";
                    return new ChatView(x.Key, c.Id, c.Host, c.Title, c.Cwd, c.Live, c.Running, c.Unread, c.Error, status, c.Updated, c.LatestTurnId); })
                .OrderByDescending(c => c.Running).ThenByDescending(c => c.Error).ThenByDescending(c => c.Unread).ThenByDescending(c => c.Updated).ToArray();
            return new(connected, connection, protocolWarning ?? warning, rows.Count(c => c.Running), rows.Count(c => c.Unread),
                rows.Count(c => c.Error), rows.Count(c => c.Live), snapshots, patches, readChanges, rows);
        }
    }
}

internal static class JsonPatches
{
    public static void Apply(JsonNode root, JsonNode patch)
    {
        var path = patch["path"] as JsonArray ?? throw new InvalidOperationException("Missing patch path");
        var op = J.Text(patch["op"]);
        if (op is not ("add" or "replace" or "remove") || path.Count == 0) throw new InvalidOperationException("Unsupported patch");
        JsonNode parent = root;
        for (var i = 0; i < path.Count - 1; i++) parent = Child(parent, path[i]) ?? throw new InvalidOperationException("Missing patch parent");
        var value = patch["value"]?.DeepClone(); var last = path[^1];
        if (parent is JsonObject obj)
        {
            var key = J.Text(last);
            if (op != "add" && !obj.ContainsKey(key)) throw new InvalidOperationException("Missing patch field");
            if (op == "remove") obj.Remove(key); else obj[key] = value;
        }
        else if (parent is JsonArray array)
        {
            var index = checked((int)J.Number(last, -1));
            if (index < 0 || index > array.Count || (op != "add" && index == array.Count)) throw new InvalidOperationException("Invalid patch index");
            if (op == "add") array.Insert(index, value); else if (op == "remove") array.RemoveAt(index); else array[index] = value;
        }
        else throw new InvalidOperationException("Invalid patch container");
    }
    private static JsonNode? Child(JsonNode parent, JsonNode? segment) => parent switch
    {
        JsonObject obj => obj[J.Text(segment)],
        JsonArray arr => arr[checked((int)J.Number(segment, -1))],
        _ => throw new InvalidOperationException("Invalid patch path")
    };
}
