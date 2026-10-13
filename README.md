# Stoplight

Stoplight is a Windows tray companion for Codex in VS Code. It puts three numbered status icons beside your clock, so you can see which chats have finished, which need your attention, and which are still working.

![Stoplight dashboard showing finished unread replies, chats awaiting user input, and running chats](docs/preview.png)

*Example dashboard with sample chats. Click a tray icon to open its matching chat list.*

| Icon | Meaning |
| --- | --- |
| Green | Finished and unread — a completed reply you have not read yet |
| Red | Awaiting user input — a question or approval request needs your response |
| Yellow | Still running — Codex is actively working |

Each chat counts in at most one category. Stoplight tracks local, unarchived main chats from the Codex VS Code extension; subagents are excluded.

## Use Stoplight

- Hover an icon to see its status and full count. Large counts display as `99+`.
- Click an icon to view its matching chats. Use **All statuses**, search, or **Show saved chats** to explore the list.
- Double-click a chat to open it in Codex. Reading a reply in VS Code clears its unread status.
- Close the dashboard to keep Stoplight running in the tray. Right-click an icon and choose **Exit Stoplight** to quit.

Stoplight reconnects automatically when VS Code restarts. Gray dashes mean waiting and running counts are unknown while disconnected. Confirmed finished unread replies remain available. Failed chats appear in the dashboard.

## Works well with TrayBridge

[TrayBridge](https://github.com/TehBandit/traybridge) brings Windows 11's native tray icons to additional monitors and lets you choose which icons appear on each display. It pairs well with Stoplight, keeping your chat status icons visible on the monitors where you work.

## Install on Windows

**Requirements:** Windows x64, VS Code, and the OpenAI Codex extension. Windows 11 is recommended.

1. Install the Codex extension in VS Code, sign in, and open the Codex panel.
2. Download `Stoplight-v0.1.1-win-x64.zip` from the [latest release](https://github.com/TehBandit/stoplight/releases/latest).
3. Extract the **entire ZIP** into a writable folder you plan to keep, such as `C:\Users\YourName\Apps\Stoplight`.
4. Double-click `Start-Stoplight.cmd`. You can also launch `dist\Stoplight.exe` directly.
5. If the icons are hidden, open the **^** tray overflow beside the clock and drag the Stoplight icons onto the taskbar.

The download includes the .NET runtime. Run Stoplight as the same Windows user and at the same privilege level as VS Code.

### Start automatically after a restart

1. Press **Win+R**, enter `shell:startup`, and press Enter.
2. Create a shortcut to your extracted `dist\Stoplight.exe` in that folder.

Stoplight will launch when you sign into Windows. Keep the extracted folder in the same location so the shortcut continues to work.

### Update an existing installation

Choose **Exit Stoplight** from a tray icon's right-click menu, extract the new release into the same installation folder, and launch it again. Keep `dist\data` to preserve runtime state. Your startup shortcut continues to work if the executable stays at the same path.

## Build from source

Use Windows PowerShell 5.1 or later, Git, and an internet connection for the initial downloads:

```powershell
git clone https://github.com/TehBandit/stoplight.git
cd stoplight
powershell -ExecutionPolicy Bypass -File .\setup.ps1
.\Start-Stoplight.cmd
```

The setup script downloads a project-local .NET 10 SDK and builds the portable app in `dist`. For later builds, run `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish`.

## Troubleshooting

If the icons stay gray, open the Codex panel in VS Code and check that both apps run under the same Windows user and privilege level. If startup fails, inspect `dist\data\startup-error.txt`.

See [development and diagnostics](docs/DEVELOPMENT.md) for tests and connection checks, or [integration details](docs/INTEGRATION.md) for how monitoring works.

Stoplight is an unofficial integration with Codex's private local protocol. Extension updates may require a Stoplight update. Native Windows sessions are supported; remote/WSL hosts and alternative VS Code user-data directories have not been validated. Monitoring reads local state and does not modify chats or Codex settings.
