using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Stoplight;

internal static class Frames
{
    public const int MaxBytes = 256 * 1024 * 1024;
    public static async Task<JsonNode> Read(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size == 0 || size > MaxBytes) throw new InvalidDataException("Invalid Codex frame length.");
        var payload = new byte[size]; await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonNode.Parse(payload) ?? throw new InvalidDataException("Empty Codex message.");
    }
    public static async Task Write(Stream stream, JsonNode message, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken); await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

internal sealed class IpcMonitor : IDisposable
{
    public ChatStore Store { get; } = new();
    private readonly CancellationTokenSource stop = new();
    private Channel<JsonObject>? outgoing;
    private readonly ErrorCache cache = new();
    private readonly ConcurrentDictionary<string, (string Host, string Id)> subscriptions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSnapshotRequest = new();
    private readonly object metadataGate = new();
    private List<ChatMetadata> metadata = [];
    private readonly Dictionary<string, long> checkedErrors = [];
    private NamedPipeClientStream? pipe;
    private string? clientId;
    private readonly string pipeName;
    private readonly bool loadLocalData;
    private Task? runTask;
    public IpcMonitor(string pipeName = "codex-ipc", bool loadLocalData = true, IEnumerable<ChatMetadata>? initialMetadata = null)
    {
        this.pipeName = pipeName; this.loadLocalData = loadLocalData;
        metadata = initialMetadata?.ToList() ?? []; Store.Seed(metadata, null);
    }
    public Task Start()
    {
        runTask = Task.Run(async () => await Task.WhenAll(RefreshLocalData(), ConnectLoop(), DiscoverSavedChats()));
        return runTask;
    }
    private async Task RefreshLocalData()
    {
        if (!loadLocalData) return;
        var restored = cache.Read();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var rows = LocalData.ReadMetadata(); var unread = LocalData.ReadUnread();
                Store.Seed(rows, unread);
                foreach (var row in rows.Where(r => !r.Archived))
                {
                    var hasSaved = restored.TryGetValue(row.Id, out var saved);
                    if (hasSaved) Store.RestoreError(row.Id, true, saved!.TurnId);
                    if (!checkedErrors.TryGetValue(row.Id, out var updated) || updated != row.Updated ||
                        unread?.Contains(row.Id) == true)
                    {
                        try
                        {
                            if (LocalData.ReadLatestState(row.Rollout) is { } result)
                            {
                                // A failed turn may only persist its start event. Keep the observed
                                // failure until completion or a different turn provides newer evidence.
                                Store.RestoreSavedState(row.Id, result, LocalData.ResolveError(saved, result, row.Updated));
                            }
                        }
                        catch (IOException) { }
                        checkedErrors[row.Id] = row.Updated;
                    }
                }
                restored.Clear(); lock (metadataGate) metadata = rows;
                cache.Save(Store.View()); Store.Warning(null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException)
            { Store.Warning("Saved chat information is temporarily unavailable. Live monitoring continues."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task ConnectLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            clientId = null; subscriptions.Clear(); lastSnapshotRequest.Clear();
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            Task? writing = null;
            try
            {
                Store.Connection(false, "Connecting to VS Code…");
                using var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe = connection;
                await connection.ConnectAsync(2000, stop.Token);
                var channel = Channel.CreateUnbounded<JsonObject>(new() { SingleReader = true });
                outgoing = channel;
                writing = WriteLoop(connection, channel.Reader, cycle);
                await Send(new JsonObject { ["type"] = "request", ["method"] = "initialize", ["version"] = 0,
                    ["requestId"] = Guid.NewGuid().ToString(), ["sourceClientId"] = "initializing-client",
                    ["params"] = new JsonObject { ["clientType"] = "stoplight-observer" } });
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(8));
                while (!stop.IsCancellationRequested)
                {
                    var message = await Frames.Read(connection, clientId == null ? handshake.Token : cycle.Token);
                    await Handle(message);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Store.Connection(false, e is UnauthorizedAccessException ? "Cannot access VS Code connection" : "Waiting for VS Code · open Codex to connect");
            }
            finally
            {
                cycle.Cancel(); outgoing = null; pipe = null;
                if (writing != null) await writing;
            }
            try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { break; }
        }
        Store.Connection(false, "Stopped");
    }
    private async Task DiscoverSavedChats()
    {
        if (!loadLocalData) return;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (clientId != null && Store.View().Connected)
                {
                    List<ChatMetadata> rows; lock (metadataGate) rows = metadata.ToList();
                    foreach (var row in rows.Where(r => !r.Archived)) await Follow("local", row.Id);
                }
                await Task.Delay(3000, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private static async Task WriteLoop(Stream connection, ChannelReader<JsonObject> reader, CancellationTokenSource cycle)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cycle.Token)) await Frames.Write(connection, message, cycle.Token);
        }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { cycle.Cancel(); }
    }
    private Task Send(JsonObject message)
    {
        // Never wait for outbound pipe capacity on the receive loop: a peer may
        // need us to drain its snapshot before it can read our subscriptions.
        if (outgoing?.Writer.TryWrite(message) != true) throw new IOException("Codex connection closed.");
        return Task.CompletedTask;
    }
    private async Task Follow(string host, string id, bool force = false)
    {
        if (host != "local" || string.IsNullOrEmpty(id)) return;
        var key = ChatStore.Key(host, id); var added = subscriptions.TryAdd(key, (host, id));
        if (clientId == null || (!force && !added)) return;
        if (force && lastSnapshotRequest.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(1)) return;
        lastSnapshotRequest[key] = DateTimeOffset.UtcNow;
        await Send(new JsonObject { ["type"] = "broadcast", ["method"] = "thread-stream-following-changed", ["version"] = 1,
            ["sourceClientId"] = clientId, ["params"] = new JsonObject { ["hostId"] = host, ["conversationId"] = id, ["following"] = true } });
    }
    private async Task Handle(JsonNode message)
    {
        var type = J.Text(message["type"]); var method = J.Text(message["method"]); var p = message["params"];
        if (type == "client-discovery-request")
        {
            await Send(new JsonObject { ["type"] = "client-discovery-response", ["requestId"] = J.Text(message["requestId"]),
                ["response"] = new JsonObject { ["canHandle"] = false } }); return;
        }
        if (type == "request")
        {
            await Send(new JsonObject { ["type"] = "response", ["requestId"] = J.Text(message["requestId"]),
                ["resultType"] = "error", ["error"] = "observer-has-no-handlers" }); return;
        }
        if (type == "response" && method == "initialize" && J.Text(message["resultType"]) == "success")
        {
            clientId = J.Text(message["result"]?["clientId"]);
            if (string.IsNullOrEmpty(clientId)) throw new InvalidDataException("Missing client ID");
            Store.Connection(true, "Connected to Codex in VS Code");
            foreach (var topic in subscriptions.Values.ToArray()) await Follow(topic.Host, topic.Id, true);
            // Seed every known main chat so older background runs aren't skipped.
            List<ChatMetadata> rows; lock (metadataGate) rows = metadata.ToList();
            foreach (var row in rows.Where(r => !r.Archived)) await Follow("local", row.Id);
            return;
        }
        if (type != "broadcast") return;
        var host = J.Text(p?["hostId"], "local"); var id = J.Text(p?["conversationId"]); var owner = J.Text(message["sourceClientId"]);
        switch (method)
        {
            case "thread-stream-following-changed":
                if (J.Bool(p?["following"])) await Follow(host, id); break;
            case "client-status-changed":
                if (J.Text(p?["status"]) == "disconnected") Store.OwnerDisconnected(J.Text(p?["clientId"])); break;
            case "thread-stream-state-changed":
                if (J.Number(message["version"]) != 11)
                { Store.UnsupportedProtocol(); return; }
                var change = p?["change"]; if (change == null || host != "local") return;
                var revision = J.Number(change["revision"]);
                if (J.Text(change["type"]) == "snapshot" && change["conversationState"] is { } state)
                    Store.Snapshot(host, id, owner, revision, state);
                else if (J.Text(change["type"]) == "patches" && change["patches"] is JsonArray changes)
                    if (!Store.Patch(host, id, owner, J.Number(change["baseRevision"]), revision, changes)) await Follow(host, id, true);
                break;
            case "thread-read-state-changed":
                if (host == "local" && J.Number(message["version"]) == 3 && !string.IsNullOrEmpty(id)) Store.Read(host, id, J.Bool(p?["hasUnreadTurn"])); break;
            case "thread-archived": case "thread-unarchived":
                if (host == "local") Store.Archive(host, id, method == "thread-archived"); break;
        }
    }
    public void Dispose()
    {
        stop.Cancel(); pipe?.Dispose();
        if (loadLocalData) try { cache.Save(Store.View()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
