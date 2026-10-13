using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Stoplight;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(20, 24, 32), Surface = Color.FromArgb(30, 36, 47),
        Text = Color.FromArgb(234, 238, 244), Muted = Color.FromArgb(156, 168, 186),
        Green = Color.FromArgb(85, 218, 156), Amber = Color.FromArgb(255, 200, 91), Red = Color.FromArgb(255, 114, 123);
}

internal static class Badge
{
    public static Icon Create(Color color, int? count)
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var fillColor = count.HasValue ? color : Color.FromArgb(160, 170, 187);
        using var fill = new SolidBrush(fillColor); g.FillEllipse(fill, 2, 2, 60, 60);
        var label = count is null ? "–" : count > 99 ? "99+" : count.ToString();
        using var font = new Font("Segoe UI", label!.Length > 2 ? 22 : label.Length > 1 ? 29 : 34, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(13, 22, 27));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, ink, new RectangleF(0, -1, 64, 64), format);
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

internal sealed class Dashboard : Form
{
    private readonly Label connection = new(), note = new(), footer = new();
    private readonly Label[] counts = [new(), new(), new()];
    private readonly DataGridView grid = new();
    private readonly CheckBox history = new() { Text = "Show saved chats", AutoSize = true };
    private readonly TextBox search = new() { PlaceholderText = "Find a chat or project", BorderStyle = BorderStyle.FixedSingle };
    private MonitorView? current;
    private string filter = "all", lastSignature = "";
    public Dashboard()
    {
        Text = "Stoplight · Codex status"; ClientSize = new Size(920, 580); MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Background; ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        using var badge = Badge.Create(Theme.Green, 0); Icon = (Icon)badge.Clone();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(24) };
        foreach (var height in new float[] { 68, 100, 45, 30 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(layout);
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "Stoplight", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
        connection.SetBounds(2, 39, 800, 24); connection.ForeColor = Theme.Muted; header.Controls.Add(connection); layout.Controls.Add(header, 0, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 8, 0, 8) };
        for (var i = 0; i < 3; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(i == 0 ? 0 : 8, 0, 0, 0), Cursor = Cursors.Hand };
            var color = new[] { Theme.Green, Theme.Red, Theme.Amber }[i];
            var title = new Label { Text = new[] { "FINISHED AND UNREAD", "AWAITING USER INPUT", "STILL RUNNING" }[i], AutoSize = true, ForeColor = color, Location = new Point(15, 12), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            counts[i].SetBounds(12, 31, 240, 49); counts[i].Font = new Font("Segoe UI", 27, FontStyle.Bold); counts[i].ForeColor = Theme.Text;
            panel.Controls.Add(title); panel.Controls.Add(counts[i]); cards.Controls.Add(panel, i, 0);
            var choice = new[] { "finished", "waiting", "running" }[i];
            void ClickCard(object? sender, EventArgs args) { SetFilter(filter == choice ? "all" : choice); }
            panel.Click += ClickCard; title.Click += ClickCard; counts[i].Click += ClickCard;
        }
        layout.Controls.Add(cards, 0, 1);
        var toolbar = new Panel { Dock = DockStyle.Fill };
        search.SetBounds(0, 8, 330, 29); search.BackColor = Theme.Surface; search.ForeColor = Theme.Text; search.TextChanged += (_, _) => Rebuild();
        history.Location = new Point(347, 10); history.CheckedChanged += (_, _) => Rebuild();
        toolbar.Padding = new Padding(0, 5, 0, 5);
        var all = MakeButton("All statuses", (_, _) => SetFilter("all")); all.Dock = DockStyle.Right; all.Width = 140;
        toolbar.Controls.AddRange([search, history, all]); layout.Controls.Add(toolbar, 0, 2);
        note.Dock = DockStyle.Fill; note.ForeColor = Theme.Muted; note.Font = new Font("Segoe UI", 9); note.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(note, 0, 3);
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false; grid.BackgroundColor = Theme.Background; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(8) };
        grid.ColumnHeadersHeight = 36; grid.DefaultCellStyle = new() { BackColor = Theme.Background, ForeColor = Theme.Text,
            SelectionBackColor = Color.FromArgb(47, 58, 74), SelectionForeColor = Theme.Text, Padding = new Padding(8, 4, 8, 4) };
        grid.GridColor = Theme.Surface; grid.RowTemplate.Height = 43; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", FillWeight = 25 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Chat", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", FillWeight = 30 });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is ChatView chat) OpenChat(chat); };
        var context = new ContextMenuStrip(); context.Items.Add("Open in VS Code", null, (_, _) => { if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is ChatView chat) OpenChat(chat); });
        context.Items.Add("Copy chat ID", null, (_, _) => { if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is ChatView chat) Clipboard.SetText(chat.Id); }); grid.ContextMenuStrip = context;
        layout.Controls.Add(grid, 0, 4);
        footer.Dock = DockStyle.Fill; footer.ForeColor = Theme.Muted; footer.Font = new Font("Segoe UI", 9); footer.TextAlign = ContentAlignment.BottomLeft;
        footer.Text = "Double-click a chat to open it in VS Code. Closing this window keeps Stoplight in the tray."; layout.Controls.Add(footer, 0, 5);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 75, 92); button.Click += click; return button;
    }
    public void UpdateView(MonitorView view)
    {
        current = view; connection.Text = view.Connection + (view.Connected ? $" · {view.LiveChats} live chats" : "");
        connection.ForeColor = view.Connected ? Theme.Green : Theme.Muted;
        counts[0].Text = view.FinishedUnread.ToString();
        counts[1].Text = view.Connected ? view.AwaitingInput.ToString() : "–";
        counts[2].Text = view.Connected ? view.Running.ToString() : "–";
        note.Text = view.Warning ?? (filter == "all" ? "Main Codex chats · local VS Code windows · subagents excluded" : $"Showing {filter} chats · click the card again to clear");
        var signature = filter + history.Checked + search.Text + string.Join("|", view.Chats.Select(c => c.Key + c.Status + c.Title + c.Cwd + c.Live));
        if (signature != lastSignature) { lastSignature = signature; Rebuild(); }
    }
    public void SetFilter(string value) { filter = value; Rebuild(); if (current != null) UpdateView(current); }
    private void Rebuild()
    {
        if (current == null) return;
        var selected = grid.SelectedRows.Count > 0 ? (grid.SelectedRows[0].Tag as ChatView)?.Key : null;
        var rows = current.Chats.Where(c => history.Checked || c.Live || c.Unread || c.Error).Where(c => filter switch
            { "finished" => c.FinishedUnread, "waiting" => c.AwaitingInput, "running" => c.Running, _ => true })
            .Where(c => c.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || c.Cwd.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Take(500);
        grid.Rows.Clear();
        foreach (var chat in rows)
        {
            var index = grid.Rows.Add(chat.Status, chat.Title, chat.Cwd); var row = grid.Rows[index]; row.Tag = chat;
            row.Cells[0].Style.ForeColor = chat.AwaitingInput ? Theme.Red : chat.Running ? Theme.Amber : chat.FinishedUnread ? Theme.Green : Theme.Muted;
            row.Cells[1].ToolTipText = chat.Title; row.Cells[2].ToolTipText = chat.Cwd;
            if (chat.Key == selected) row.Selected = true;
        }
    }
    private static void OpenChat(ChatView chat)
    {
        try { Process.Start(new ProcessStartInfo("vscode://openai.chatgpt/local/" + Uri.EscapeDataString(chat.Id)) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show("VS Code could not be opened. The chat ID is " + chat.Id, "Stoplight", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly IpcMonitor monitor = new();
    private readonly NotifyIcon[] icons = [new(), new(), new()];
    private readonly Color[] colors = [Theme.Green, Theme.Red, Theme.Amber];
    private readonly string[] labels = ["Finished and unread", "Awaiting user input", "Still running"];
    private readonly string[] filters = ["finished", "waiting", "running"];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly Dashboard dashboard = new();
    private readonly ToolStripMenuItem[] countItems = [new(), new(), new()];
    private readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    private readonly ContextMenuStrip menu = new();
    private readonly int?[] previous = [int.MinValue, int.MinValue, int.MinValue];
    private string lastStatus = "";
    public TrayApp()
    {
        menu.Items.Add("Open Stoplight", null, (_, _) => Show("all")); menu.Items.Add(statusItem); menu.Items.Add(new ToolStripSeparator());
        for (var i = 0; i < 3; i++)
        {
            var index = i; countItems[i].Click += (_, _) => Show(filters[index]); menu.Items.Add(countItems[i]);
            icons[i].ContextMenuStrip = menu; icons[i].MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Show(filters[index]); };
        }
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Exit Stoplight", null, (_, _) => ExitThread());
        _ = monitor.Start(); timer.Tick += (_, _) => Update(); timer.Start(); Update();
    }
    private void Show(string filter)
    {
        dashboard.UpdateView(monitor.Store.View()); dashboard.SetFilter(filter); dashboard.Show();
        if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
        dashboard.Activate();
    }
    private void Update()
    {
        var view = monitor.Store.View(); statusItem.Text = view.Connection;
        int?[] values = [view.FinishedUnread, view.Connected ? view.AwaitingInput : null, view.Connected ? view.Running : null];
        for (var i = 0; i < 3; i++)
        {
            var text = $"Stoplight · {labels[i]}: {(values[i]?.ToString() ?? "unknown")} · {(view.Connected ? "VS Code" : "disconnected")}";
            icons[i].Text = text[..Math.Min(text.Length, 127)]; countItems[i].Text = $"{labels[i]}: {values[i]?.ToString() ?? "unknown"}";
            if (previous[i] != values[i])
            {
                var old = icons[i].Icon; icons[i].Icon = Badge.Create(colors[i], values[i]); old?.Dispose(); previous[i] = values[i];
            }
            icons[i].Visible = true;
        }
        if (dashboard.Visible) dashboard.UpdateView(view);
        var status = System.Text.Json.JsonSerializer.Serialize(new { view.Connected, view.Connection, view.Warning,
            view.FinishedUnread, view.AwaitingInput, view.Running, view.Unread, view.Errors, view.LiveChats,
            TrayIcons = icons.Count(i => i.Visible) });
        if (status != lastStatus)
        {
            try { Directory.CreateDirectory(Paths.Data); File.WriteAllText(Path.Combine(Paths.Data, "status.json"), status); lastStatus = status; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    protected override void ExitThreadCore()
    {
        timer.Stop(); monitor.Dispose(); foreach (var icon in icons) { icon.Visible = false; var bitmap = icon.Icon; icon.Dispose(); bitmap?.Dispose(); }
        timer.Dispose(); dashboard.Dispose(); menu.Dispose(); base.ExitThreadCore();
    }
}
