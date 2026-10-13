# Codex integration

Stoplight is an unofficial, read-only Windows companion for the existing Codex VS Code extension. It does not start a separate Codex app-server or send prompts, approvals, or read-state changes.

## Live connection

The tested extension exposes `\\.\pipe\codex-ipc`. Frames contain a four-byte little-endian payload length followed by UTF-8 JSON. Stoplight initializes its own IPC client and registers temporary follower subscriptions for local, unarchived main chats. Conversation state arrives through `thread-stream-state-changed` snapshots and incremental patches. The implementation tracks revisions and owners, and requests a fresh snapshot after a revision gap or unsupported patch shape.

Stream protocol 11 and read protocol 3 were verified against extension version `26.930.61225`. This pipe protocol is private and differs from the documented app-server JSON-RPC interface. Future extension versions may change it.

Outgoing writes use a separate queue so subscribing to a large saved-chat list does not block incoming snapshots. Reconnection restores subscriptions. Disconnection clears live state and displays unknown waiting/running counts.

## Counters

| Counter | Sources |
| --- | --- |
| Finished and unread (green) | Completed latest turn with unread flag/read broadcasts; saved successful-completion events plus VS Code unread IDs for inactive chats |
| Awaiting user input (red) | Active live turn with `waitingOnApproval` or `waitingOnUserInput` in runtime `activeFlags` |
| Still running (yellow) | Active runtime or in-progress latest turn, excluding awaiting-input and failed chats |

Both canonical turn history and legacy turn arrays are supported. Each chat counts in at most one tray category. Saved unread IDs alone do not establish completion. Unknown, unfinished, aborted, and failed saved turns are excluded from green. Saved completion events recover replies that finish while disconnected, without overriding an active live turn. Idle summaries without turn history can retain or recover confirmed saved completion evidence.

Read broadcasts update both the displayed unread flag and the in-memory patch base, so unrelated stream patches cannot restore a stale unread flag. Saved lifecycle evidence for unread chats is rechecked every five seconds even if their metadata timestamp is unchanged, allowing completion recovery within the same timestamp second. Active live turns remain authoritative.

Errors remain available in the chat list and diagnostics. Latest-turn terminal errors, failed turns, runtime system errors, and cached terminal events for inactive chats establish error state. Retryable errors and ordinary tool/command failures do not independently mark a chat failed.

## Local metadata

Read-only SQLite queries discover native Windows Codex chat metadata under `CODEX_HOME` or the default user-profile `.codex` directory. VS Code's saved extension state supplies unread IDs. Metadata filtering excludes subagents, archived chats, and non-VS Code threads. Saved metadata/read state is refreshed periodically while live broadcasts supply immediate updates.

Cached errors contain identifiers and timestamps. Historical cold-start error recovery is limited to explicit terminal events available in saved session tails. Conversation snapshots remain in memory and are not persisted as transcripts.

## Validation boundary

The implementation was connected to a real native Windows VS Code instance and received live snapshots and patches. Automated tests cover unread/error and awaiting-input transitions, metadata filters, owner changes, revision gaps, framing, reconnects, and subscriptions for 120 saved chats. Real multiple-window operation, remote/WSL hosts, alternative VS Code user-data directories, and complete real-user error/read transitions need further validation.

`investigation/probe-codex-ipc.cjs` is an optional Node-based development observer. Run `node .\investigation\probe-codex-ipc.cjs 30000 --follow` to observe state summaries for 30 seconds. It is not required to build or run Stoplight.
