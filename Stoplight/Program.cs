using System.Text.Json;

namespace Stoplight;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { SelfTests.Run(Output(args, "self-test.json")).GetAwaiter().GetResult(); return; }
        if (args.Contains("--diagnose"))
        {
            var index = Array.IndexOf(args, "--diagnose");
            var seconds = index + 1 < args.Length && int.TryParse(args[index + 1], out var n) ? Math.Clamp(n, 1, 300) : 15;
            Diagnose(seconds, Output(args, "diagnostic.json")).GetAwaiter().GetResult(); return;
        }
        if (args.Contains("--preview"))
        {
            var path = Output(args, "preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var form = new Dashboard(); form.UpdateView(SelfTests.SampleView()); form.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(path); return;
        }
        using var singleton = new Mutex(true, @"Local\Stoplight.CodexTray", out var created);
        if (!created) return;
        try { Application.Run(new TrayApp()); }
        catch (Exception e)
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Path.Combine(Paths.Data, "startup-error.txt"), e.GetType().Name + ": " + e.Message);
            MessageBox.Show("Stoplight could not start. See data/startup-error.txt beside the app.", "Stoplight", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { singleton.ReleaseMutex(); }
    }
    private static string Output(string[] args, string name)
    {
        var index = Array.IndexOf(args, "--output");
        return Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Paths.Data, name));
    }
    private static async Task Diagnose(int seconds, string path)
    {
        using var monitor = new IpcMonitor(); _ = monitor.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds)); var v = monitor.Store.View();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            v.Connected, v.Connection, v.Warning, v.Running, v.Unread, v.Errors, v.LiveChats,
            v.Snapshots, v.Patches, v.ReadChanges, SavedChats = v.Chats.Count,
            LiveStates = v.Chats.Where(c => c.Live).Select(c => new { c.Status, c.Running, c.Unread, c.Error })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
