# Development and diagnostics

Run `setup.ps1` to download a project-local .NET 10 SDK and publish the portable app. Downloaded tools and packages stay under `.tools`; setup does not change the system SDK or PATH. The PowerShell execution-policy override applies only to that process.

For subsequent builds:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish
```

The build prefers the local SDK and otherwise uses `dotnet` on PATH. Omit `-Publish` for a normal Release build. Exit the running tray app before replacing its executable.

## Self-tests

Tests cover chat filtering, unread/completion/error transitions, approval and input requests, saved completion recovery, read broadcasts followed by unrelated patches, revision handling, pipe framing, 120 saved-chat subscriptions, and reconnection.

```powershell
$test = Start-Process -FilePath .\dist\Stoplight.exe -ArgumentList '--self-test --output artifacts\self-test.json' -Wait -PassThru
Get-Content .\artifacts\self-test.json
if ($test.ExitCode -ne 0) { throw 'Self-tests failed.' }
```

## Live connection check

Observe the actual Codex connection for 20 seconds:

```powershell
Start-Process -FilePath .\dist\Stoplight.exe -ArgumentList '--diagnose 20 --output artifacts\live.json' -Wait
Get-Content .\artifacts\live.json
```

Diagnostics contain counts and connection statistics, without prompts, replies, or tool output. Diagnostic mode runs separately from the tray UI. `--preview --output docs\preview.png` renders the dashboard with sample chats for the README screenshot.

## Runtime state

The app stores runtime data in `data` beside its executable. The error cache contains chat/turn IDs and timestamps; `status.json` contains connection state and counter totals. Live snapshots remain in memory and are discarded on disconnect. Saved session tails provide completion and terminal-error evidence for inactive chats. A saved unread flag alone does not establish completion.

Stoplight does not write transcripts to logs or modify Codex settings, databases, or extension files. Runtime data, diagnostics, downloaded SDKs, and build output are excluded from Git and release packages.
