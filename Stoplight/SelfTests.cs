using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stoplight;

internal static class SelfTests
{
    private static readonly List<string> passed = [], failures = [];
    public static async Task Run(string output)
    {
        Check("Main chats count once; subagents, side conversations, and archives excluded", () =>
        {
            var store = new ChatStore(); store.Connection(true, "test");
            store.Snapshot("local", "main", "owner", 1, State("inProgress"));
            store.Snapshot("local", "main", "owner", 1, State("inProgress"));
            var subagent = State("inProgress"); subagent["source"] = new JsonObject { ["subAgent"] = new JsonObject() };
            store.Snapshot("local", "subagent", "owner", 1, subagent);
            var side = State("inProgress"); side["sideConversation"] = true; store.Snapshot("local", "side", "owner", 1, side);
            store.Seed([new("main", "Main", "", "", 1, false), new("side", "Helper", "", "", 1, false)], null);
            store.Snapshot("local", "archived", "owner", 1, State("inProgress")); store.Archive("local", "archived", true);
            Assert(store.View().Running == 1 && store.View().Chats.Count == 1);
        });
        Check("Persisted unread list is filtered by VS Code main-chat metadata", () =>
        {
            var store = new ChatStore(); store.Seed([new("main", "Test", "D:\\work", "", 1, false)], new HashSet<string> { "main", "subagent" });
            Assert(store.View().Unread == 1); store.Read("local", "main", false); Assert(store.View().Unread == 0);
        });
        Check("Canonical history completion and unread patches update counts", () =>
        {
            var store = new ChatStore(); store.Snapshot("local", "chat", "owner", 1, State("inProgress"));
            var changes = JsonNode.Parse("""[{"op":"replace","path":["threadRuntimeStatus","type"],"value":"idle"},{"op":"replace","path":["turnHistory","history","entitiesByKey","turn-1","status"],"value":"completed"},{"op":"replace","path":["hasUnreadTurn"],"value":true}]""")!.AsArray();
            Assert(store.Patch("local", "chat", "owner", 1, 2, changes)); Assert(store.View().Running == 0 && store.View().Unread == 1);
            store.Read("local", "chat", false); Assert(store.View().Unread == 0);
        });
        Check("Finished unread excludes active, waiting, failed, and already-read chats", () =>
        {
            var store = new ChatStore(); store.Connection(true, "test");
            foreach (var (id, status) in new[] { ("finished", "completed"), ("running", "inProgress"), ("waiting", "inProgress"), ("failed", "failed") })
            {
                var state = State(status); state["hasUnreadTurn"] = true;
                if (id == "waiting") state["threadRuntimeStatus"]!["activeFlags"] = new JsonArray("waitingOnUserInput");
                store.Snapshot("local", id, "owner", 1, state);
            }
            store.Snapshot("local", "read", "owner", 1, State("completed"));
            var view = store.View();
            Assert(view.FinishedUnread == 1 && view.AwaitingInput == 1 && view.Running == 1 && view.Errors == 1);
            Assert(view.Chats.Single(c => c.FinishedUnread).Id == "finished");
            Assert(view.Chats.Single(c => c.AwaitingInput).Id == "waiting");
            Assert(view.Chats.Single(c => c.Running).Id == "running");
            store.Read("local", "finished", false); Assert(store.View().FinishedUnread == 0);
        });
        Check("Approval and input requests move from running to waiting and back", () =>
        {
            foreach (var flag in new[] { "waitingOnApproval", "waitingOnUserInput" })
            {
                var store = new ChatStore(); store.Snapshot("local", "chat", "owner", 1, State("inProgress"));
                Assert(store.View().Running == 1 && store.View().AwaitingInput == 0);
                var changes = new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = new JsonArray("threadRuntimeStatus", "activeFlags"), ["value"] = new JsonArray(flag) });
                Assert(store.Patch("local", "chat", "owner", 1, 2, changes));
                Assert(store.View().Running == 0 && store.View().AwaitingInput == 1 && store.View().FinishedUnread == 0);
                changes[0]!["value"] = new JsonArray();
                Assert(store.Patch("local", "chat", "owner", 2, 3, changes));
                Assert(store.View().Running == 1 && store.View().AwaitingInput == 0);
                var finished = State("completed"); finished["hasUnreadTurn"] = true;
                store.Snapshot("local", "chat", "owner", 4, finished);
                Assert(store.View().FinishedUnread == 1 && store.View().Running == 0 && store.View().AwaitingInput == 0);
            }
        });
        Check("Saved unread replies count green while saved errors are excluded", () =>
        {
            var store = new ChatStore();
            store.Seed([new("finished", "Finished", "", "", 1, false), new("failed", "Failed", "", "", 1, false)], new HashSet<string> { "finished", "failed" });
            store.RestoreSavedState("finished", new("turn", false, true, true), false);
            store.RestoreError("failed", true);
            Assert(store.View().FinishedUnread == 1 && store.View().AwaitingInput == 0 && store.View().Running == 0);
        });
        Check("Cold-start unread requires successful completion evidence", () =>
        {
            foreach (var eventType in new[] { "task_started", "task_complete", "turn_aborted", "error", "unknown" })
            {
                var path = Path.Combine(Path.GetTempPath(), "stoplight-fixture-" + Guid.NewGuid().ToString("N") + ".jsonl");
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = eventType, turn_id = "turn", will_retry = false } }) + "\n");
                    var store = new ChatStore();
                    store.Seed([new("chat", "Title", "", path, 1, false)], new HashSet<string> { "chat" });
                    Assert(store.View().FinishedUnread == 0);
                    var result = LocalData.ReadLatestState(path)!;
                    store.RestoreSavedState("chat", result, LocalData.ResolveError(null, result, 1));
                    Assert(store.View().FinishedUnread == (eventType == "task_complete" ? 1 : 0));
                    Assert(store.View().Running == 0 && store.View().AwaitingInput == 0);
                }
                finally { File.Delete(path); }
            }
        });
        Check("Saved completion after disconnect recovers green; a newer unfinished turn clears it", () =>
        {
            var store = new ChatStore(); var active = State("inProgress"); active["hasUnreadTurn"] = true;
            store.Seed([new("chat", "Title", "", "", 1, false)], null);
            store.Snapshot("local", "chat", "owner", 1, active); store.OwnerDisconnected("owner");
            store.Seed([new("chat", "Title", "", "", 2, false)], new HashSet<string> { "chat" });
            store.RestoreSavedState("chat", new("one", false, true, true), false);
            Assert(store.View().FinishedUnread == 1);
            store.Seed([new("chat", "Title", "", "", 3, false)], new HashSet<string> { "chat" });
            Assert(store.View().FinishedUnread == 0);
            store.RestoreSavedState("chat", new("two", false, false, false), false);
            Assert(store.View().FinishedUnread == 0);
            // Completion can happen within the same metadata timestamp second.
            store.RestoreSavedState("chat", new("two", false, true, true), false);
            Assert(store.View().FinishedUnread == 1);
        });
        Check("Saved evidence supports idle summaries without overwriting live turns", () =>
        {
            var store = new ChatStore(); store.Seed([new("chat", "Title", "", "", 1, false)], new HashSet<string> { "chat" });
            var summary = State("completed"); summary.Remove("turnHistory"); summary["hasUnreadTurn"] = true;
            store.Snapshot("local", "chat", "owner", 1, summary);
            store.RestoreSavedState("chat", new("one", false, true, true), false);
            Assert(store.View().FinishedUnread == 1);
            store.Snapshot("local", "chat", "owner", 2, summary); Assert(store.View().FinishedUnread == 1);
            var active = State("inProgress"); active["hasUnreadTurn"] = true;
            store.Snapshot("local", "chat", "owner", 3, active);
            store.RestoreSavedState("chat", new("old", false, true, true), false);
            Assert(store.View().FinishedUnread == 0 && store.View().Running == 1);
        });
        Check("Read broadcasts survive unrelated patches and explicit unread patches still apply", () =>
        {
            var store = new ChatStore(); var state = State("completed"); state["hasUnreadTurn"] = true;
            store.Snapshot("local", "chat", "owner", 1, state); store.Read("local", "chat", false);
            Assert(store.Patch("local", "chat", "owner", 1, 2, JsonNode.Parse("""[{"op":"replace","path":["title"],"value":"Renamed"}]""")!.AsArray()));
            Assert(store.View().FinishedUnread == 0 && store.View().Unread == 0);
            store.Read("local", "chat", true);
            Assert(store.Patch("local", "chat", "owner", 2, 3, JsonNode.Parse("""[{"op":"replace","path":["title"],"value":"Renamed again"}]""")!.AsArray()));
            Assert(store.View().FinishedUnread == 1);
            Assert(store.Patch("local", "chat", "owner", 3, 4, JsonNode.Parse("""[{"op":"replace","path":["hasUnreadTurn"],"value":false}]""")!.AsArray()));
            Assert(store.View().FinishedUnread == 0);
        });
        Check("Disconnect clears waiting without misclassifying an unfinished unread turn", () =>
        {
            foreach (var disconnectOwner in new[] { false, true })
            {
                var store = new ChatStore(); var state = State("inProgress"); state["hasUnreadTurn"] = true;
                state["threadRuntimeStatus"]!["activeFlags"] = new JsonArray("waitingOnApproval");
                store.Snapshot("local", "chat", "owner", 1, state);
                Assert(store.View().AwaitingInput == 1 && store.View().FinishedUnread == 0);
                if (disconnectOwner) store.OwnerDisconnected("owner"); else store.Connection(false, "Disconnected");
                Assert(store.View().AwaitingInput == 0 && store.View().Running == 0 && store.View().FinishedUnread == 0);
            }
        });
        Check("Terminal errors count; retryable and older errors do not", () =>
        {
            var store = new ChatStore(); var state = State("completed");
            state["turnHistory"]!["history"]!["entitiesByKey"]!["turn-1"]!["items"] = JsonNode.Parse("""[{"type":"error","willRetry":true,"message":"Retrying"}]""");
            store.Snapshot("local", "chat", "owner", 1, state); Assert(store.View().Errors == 0);
            state["turnHistory"]!["history"]!["entitiesByKey"]!["turn-1"]!["items"]![0]!["willRetry"] = false;
            store.Snapshot("local", "chat", "owner", 2, state); Assert(store.View().Errors == 1);
            var history = state["turnHistory"]!["history"]!;
            history["entitiesByKey"]!["turn-2"] = JsonNode.Parse("""{"turnId":"2","status":"completed","items":[]}""");
            history["islands"]![0]!["entries"]!.AsArray().Add(new JsonObject { ["value"] = "turn-2" });
            store.Snapshot("local", "chat", "owner", 3, state); Assert(store.View().Errors == 0);
        });
        Check("Legacy turn failures and system errors count", () =>
        {
            var store = new ChatStore(); var s = State("failed"); s.Remove("turnHistory"); s["turns"] = JsonNode.Parse("""[{"turnId":"x","status":"failed","items":[]}]""");
            store.Snapshot("local", "chat", "owner", 1, s); Assert(store.View().Errors == 1);
            var system = State("completed"); system["threadRuntimeStatus"]!["type"] = "systemError";
            store.Snapshot("local", "chat", "owner", 2, system); Assert(store.View().Errors == 1);
        });
        Check("Revision gaps, duplicates, and owner replacement are handled", () =>
        {
            var store = new ChatStore(); store.Snapshot("local", "chat", "one", 4, State("inProgress"));
            Assert(!store.Patch("local", "chat", "one", 6, 7, []));
            Assert(store.Patch("local", "chat", "one", 3, 4, []));
            store.Snapshot("local", "chat", "two", 1, State("completed"));
            Assert(!store.Patch("local", "chat", "one", 4, 5, [])); Assert(store.View().Running == 0);
        });
        Check("Disconnect clears live running state without erasing unread", () =>
        {
            var store = new ChatStore(); var s = State("inProgress"); s["hasUnreadTurn"] = true;
            store.Snapshot("local", "chat", "owner", 1, s); store.Connection(false, "Disconnected");
            Assert(store.View().Running == 0 && store.View().Unread == 1 && store.View().LiveChats == 0);
        });
        Check("Patch arrays use insertion/removal and literal object keys", () =>
        {
            var root = JsonNode.Parse("""{"a/b":["A","C"]}""")!;
            foreach (var p in JsonNode.Parse("""[{"op":"add","path":["a/b",1],"value":"B"},{"op":"remove","path":["a/b",0]}]""")!.AsArray()) JsonPatches.Apply(root, p!);
            Assert(root.ToJsonString() == "{\"a/b\":[\"B\",\"C\"]}");
        });
        Check("Saved terminal errors survive restart and clear on a newer turn", () =>
        {
            var saved = new SavedError("failed-turn", 100);
            Assert(LocalData.ResolveError(saved, new("failed-turn", false, false), 90) == true);
            Assert(LocalData.ResolveError(saved, new("new-turn", false, false), 110) == false);
            Assert(LocalData.ResolveError(saved, new("failed-turn", false, true), 100) == false);
            Assert(LocalData.ResolveError(saved, new("", null, false), 90) == true);
        });
        Check("Deleted chat metadata removes it from all counters", () =>
        {
            var store = new ChatStore(); store.Seed([new("chat", "Title", "", "", 1, false)], new HashSet<string> { "chat" });
            store.Snapshot("local", "chat", "owner", 1, State("inProgress")); store.Seed([], new HashSet<string>());
            Assert(store.View().Running == 0 && store.View().Unread == 0 && store.View().Chats.Count == 0);
        });
        Check("Protocol mismatch remains visible after local metadata refresh", () =>
        {
            var store = new ChatStore(); store.Connection(true, "test"); store.UnsupportedProtocol(); store.Warning(null);
            Assert(!store.View().Connected && store.View().Warning != null);
        });
        Check("Unknown patch formats request resynchronization", () =>
        {
            var store = new ChatStore(); store.Snapshot("local", "chat", "owner", 1, State("inProgress"));
            Assert(!store.Patch("local", "chat", "owner", 1, 2, JsonNode.Parse("""[{"op":"replace","path":["missing","field"],"value":1}]""")!.AsArray()));
            Assert(store.View().LiveChats == 0);
        });
        await CheckAsync("Real named-pipe framing survives fragmented reads", async () =>
        {
            var name = "stoplight-test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var waiting = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await waiting;
            var bytes = Encoding.UTF8.GetBytes("{\"test\":\"✓\"}"); var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
            var read = Frames.Read(client, timeout.Token);
            foreach (var b in header.Concat(bytes)) await server.WriteAsync(new byte[] { b }, timeout.Token);
            Assert(J.Text((await read)["test"]) == "✓");
        });
        await CheckAsync("Invalid and truncated frames are rejected", async () =>
        {
            await using var invalid = new MemoryStream(new byte[4]);
            try { await Frames.Read(invalid, CancellationToken.None); throw new Exception("Invalid frame accepted"); } catch (InvalidDataException) { }
            await using var truncated = new MemoryStream(new byte[] { 5, 0, 0, 0, 123 });
            try { await Frames.Read(truncated, CancellationToken.None); throw new Exception("Truncated frame accepted"); } catch (EndOfStreamException) { }
        });
        await CheckAsync("All saved chats are subscribed; live states reconnect after owner restart", async () =>
        {
            var name = "stoplight-monitor-test-" + Guid.NewGuid().ToString("N");
            var savedChats = Enumerable.Range(0, 120).Select(i => new ChatMetadata("old-chat-" + i, "Old chat", "", "", 1, false)).ToArray();
            using var monitor = new IpcMonitor(name, loadLocalData: false, initialMetadata: savedChats); _ = monitor.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var phase = "start"; var received = 0;
            try
            {
            for (var round = 0; round < 2; round++)
            {
                phase = "connect round " + round;
                await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(timeout.Token);
                phase = "initialize round " + round;
                var hello = await Frames.Read(server, timeout.Token); Assert(J.Text(hello["method"]) == "initialize");
                await Frames.Write(server, new JsonObject { ["type"] = "response", ["method"] = "initialize", ["resultType"] = "success",
                    ["requestId"] = J.Text(hello["requestId"]), ["result"] = new JsonObject { ["clientId"] = "test-observer" } }, timeout.Token);
                await Frames.Write(server, Broadcast("thread-stream-following-changed", 1, new JsonObject { ["hostId"] = "local", ["conversationId"] = "test-chat", ["following"] = true }), timeout.Token);
                phase = "subscriptions round " + round;
                JsonNode follow; var subscribed = new HashSet<string>();
                do { follow = await Frames.Read(server, timeout.Token); received++; subscribed.Add(J.Text(follow["params"]?["conversationId"])); }
                while (J.Text(follow["params"]?["conversationId"]) != "test-chat");
                Assert(savedChats.All(c => subscribed.Contains(c.Id)));
                Assert(J.Text(follow["method"]) == "thread-stream-following-changed");
                var snapshot = State(round == 0 ? "inProgress" : "completed"); snapshot["hasUnreadTurn"] = round == 1;
                await Frames.Write(server, Broadcast("thread-stream-state-changed", 11, new JsonObject { ["hostId"] = "local", ["conversationId"] = "test-chat",
                    ["change"] = new JsonObject { ["type"] = "snapshot", ["revision"] = 1, ["conversationState"] = snapshot } }), timeout.Token);
                phase = "snapshot round " + round;
                await Wait(() => monitor.Store.View().Chats.Any(c => c.Id == "test-chat" && c.Live && c.Running == (round == 0)), timeout.Token);
                if (round == 0) { phase = "disconnect round " + round; server.Disconnect(); await Wait(() => !monitor.Store.View().Connected, timeout.Token); }
                else
                {
                    phase = "read broadcast and unrelated patch";
                    await Wait(() => monitor.Store.View().FinishedUnread == 1, timeout.Token);
                    await Frames.Write(server, Broadcast("thread-read-state-changed", 3, new JsonObject { ["hostId"] = "local", ["conversationId"] = "test-chat", ["hasUnreadTurn"] = false }), timeout.Token);
                    await Wait(() => monitor.Store.View().FinishedUnread == 0, timeout.Token);
                    await Frames.Write(server, Broadcast("thread-stream-state-changed", 11, new JsonObject { ["hostId"] = "local", ["conversationId"] = "test-chat",
                        ["change"] = new JsonObject { ["type"] = "patches", ["baseRevision"] = 1, ["revision"] = 2,
                            ["patches"] = JsonNode.Parse("""[{"op":"replace","path":["title"],"value":"Renamed"}]""") } }), timeout.Token);
                    await Wait(() => monitor.Store.View().Patches == 1, timeout.Token);
                    Assert(monitor.Store.View().FinishedUnread == 0);
                }
            }
            }
            catch (OperationCanceledException e)
            { throw new Exception($"Timed out at {phase}; received {received} subscriptions; {monitor.Store.View().Connection}", e); }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = passed.Count, Failed = failures.Count, Tests = passed, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
    private static JsonObject Broadcast(string method, int version, JsonObject p) => new() { ["type"] = "broadcast", ["method"] = method, ["version"] = version, ["sourceClientId"] = "test-owner", ["params"] = p };
    private static async Task Wait(Func<bool> predicate, CancellationToken ct) { while (!predicate()) await Task.Delay(25, ct); }
    private static void Check(string name, Action action) { try { action(); passed.Add(name); } catch (Exception e) { failures.Add(name + ": " + e.Message); } }
    private static async Task CheckAsync(string name, Func<Task> action) { try { await action(); passed.Add(name); } catch (Exception e) { failures.Add(name + ": " + e.Message); } }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static JsonObject State(string status) => new()
    {
        ["source"] = "vscode", ["title"] = "Test chat", ["hasUnreadTurn"] = false,
        ["threadRuntimeStatus"] = new JsonObject { ["type"] = status == "inProgress" ? "active" : "idle", ["activeFlags"] = new JsonArray() },
        ["turns"] = new JsonArray(), ["turnHistory"] = new JsonObject { ["kind"] = "canonical", ["history"] = new JsonObject
        {
            ["entitiesByKey"] = new JsonObject { ["turn-1"] = new JsonObject { ["turnId"] = "one", ["status"] = status, ["items"] = new JsonArray() } },
            ["islands"] = new JsonArray(new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["value"] = "turn-1" }) })
        } }
    };
    public static MonitorView SampleView() => new(true, "Connected to Codex in VS Code", null, 1, 1, 1, 4, 5, 120, 2,
    [new("local:one", "one", "local", "Build the Stoplight tray app", "D:\\stoplight", true, true, false, false, "Still running", DateTimeOffset.UtcNow),
     new("local:two", "two", "local", "Review the authentication changes", "D:\\projects\\portal", true, false, false, false, "Awaiting user input", DateTimeOffset.UtcNow, AwaitingInput: true),
     new("local:three", "three", "local", "Fix the dashboard layout", "D:\\projects\\portal", true, false, true, false, "Finished and unread", DateTimeOffset.UtcNow, FinishedUnread: true),
     new("local:four", "four", "local", "Run the release checks", "D:\\projects\\tools", true, false, false, true, "Error", DateTimeOffset.UtcNow)]);
}
