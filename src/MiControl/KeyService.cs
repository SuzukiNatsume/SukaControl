using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;

namespace SukaControl;

internal record KeyState(bool FnLocked, bool WinLocked, int Brightness, bool KeyboardEnabled, long Revision, int PerformanceMode = 0);
internal record KeyAction(long Id, string Name, long Created);

// Firmware decides whether a physical key is F1-F12 or an OEM shortcut. Never hook or reinject it.
internal sealed class KeyService : IDisposable
{
    readonly Firmware firmware;
    readonly Lighting lighting;
    readonly ConcurrentQueue<byte[]> events = new();
    readonly Queue<KeyAction> actions = new();
    readonly Dictionary<string, long> lastAction = new();
    readonly List<ManagementEventWatcher> watchers = new();
    readonly SessionAgent launcher;
    readonly CancellationTokenSource stop = new();
    readonly Task sessionWorker;
    long lastState, lastLight, lastPerformance, revision, actionId;
    KeyState state = new(false, false, -1, false, 0);
    public KeyService(Firmware firmware, Lighting lighting, string ownerSid)
    {
        this.firmware = firmware; this.lighting = lighting; launcher = new(ownerSid);
        using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM meta_class WHERE __CLASS LIKE 'HID_EVENT%'");
        using var classes = query.Get();
        foreach (ManagementBaseObject type in classes)
        using (type)
        {
            string guid; try { guid = type.Qualifiers["guid"].Value?.ToString() ?? ""; } catch { continue; }
            if (!guid.Contains("46c93e13-ee9b-4262-8488-563bca757fef", StringComparison.OrdinalIgnoreCase)) continue;
            var watcher = new ManagementEventWatcher(@"root\wmi", $"SELECT * FROM {type["__CLASS"]}");
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["InstanceName"] is string instance && instance.EndsWith(@"\MIFS_0", StringComparison.OrdinalIgnoreCase) && e.NewEvent["EventDetail"] is byte[] data)
                { events.Enqueue(data); Store.Log("KEY firmware " + Convert.ToHexString(data)); }
            };
            watcher.Start(); watchers.Add(watcher); Store.Log("KEY listening " + type["__CLASS"]);
        }
        if (watchers.Count == 0) throw new IOException("Firmware shortcut events are unavailable.");
        sessionWorker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { launcher.EnsureRunning(); } catch (Exception ex) { Store.Log("Key session: " + ex.Message); }
                try { await Task.Delay(1500, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }
    internal static string? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 1) return null;
        return data[1] switch { 1 when data[2] == 0 => "project", 24 when data[2] == 1 => "f7", 10 when data[2] == 0 => "show", 27 when data[2] == 0 => "settings", _ => null };
    }
    public async Task<(KeyState State, KeyAction? Action)> Read(bool agent, long ack)
    {
        while (events.TryDequeue(out var data))
        {
            if (data.Length >= 3 && data[0] == 1)
            {
                if (data[1] == 7) lastState = 0; // Fn+Esc is already handled by the firmware.
                if (data[1] == 5 && data[2] is 0 or 2 or 5 or 10) lastLight = 0; // F10 brightness is also native.
                if (data[1] == 0x16) lastPerformance = 0; // Native Fn+K already changed the mode. Only refresh; never bind plain K or cycle again.
            }
            string? action = Decode(data);
            long now = Environment.TickCount64;
            if (action == null || lastAction.TryGetValue(action, out var last) && now - last < 250) continue;
            lastAction[action] = now;
            while (actions.Count >= 16) actions.Dequeue();
            actions.Enqueue(new(++actionId, action, now));
        }
        long time = Environment.TickCount64;
        bool fn = state.FnLocked, win = state.WinLocked, enabled = state.KeyboardEnabled; int brightness = state.Brightness, performance = state.PerformanceMode;
        if (time - lastState >= 1500) { fn = firmware.FnLocked(); win = firmware.WinLocked(); lastState = time; }
        if (time - lastPerformance >= 1500) { performance = firmware.Performance(); lastPerformance = time; }
        if (time - lastLight >= 400)
        {
            try { var config = await lighting.ReadConfig(); brightness = config[8]; enabled = (config[12] & 2) != 0; lastLight = time; }
            catch (Exception ex) { lastLight = time; Store.Log("Key light state: " + ex.Message); }
        }
        if (fn != state.FnLocked || win != state.WinLocked || brightness != state.Brightness || enabled != state.KeyboardEnabled || performance != state.PerformanceMode) revision++;
        state = new(fn, win, brightness, enabled, revision, performance);
        while (actions.TryPeek(out var queued) && (queued.Id <= ack || time - queued.Created > 5000)) actions.Dequeue();
        return (state, agent && actions.TryPeek(out var next) ? next : null);
    }
    public void InvalidateLight() => lastLight = 0;
    public void InvalidateState() => lastState = 0;
    public void InvalidatePerformance() => lastPerformance = 0;
    public void Dispose()
    {
        stop.Cancel(); sessionWorker.GetAwaiter().GetResult(); launcher.Dispose();
        foreach (var watcher in watchers) { try { watcher.Stop(); } catch { } watcher.Dispose(); }
        stop.Dispose();
    }
}

internal static class DesktopKeyAgent
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Local\SukaControl.Keys." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out bool first);
        if (!first) return;
        long ack = 0; int failures = 0;
        while (failures < 3)
        {
            try
            {
                var reply = HardwareClient.Send(new(0x41, Ack: ack));
                if (!reply.Ok) throw new IOException(reply.Error);
                failures = 0;
                if (reply.Action is { } action && action.Id > ack)
                {
                    try { Execute(action.Name); Store.Log("KEY action " + action.Name); }
                    catch (Exception ex) { Store.Log("Key action: " + ex); }
                    ack = action.Id;
                }
            }
            catch (Exception ex) { failures++; Store.Log("Key agent: " + ex.Message); }
            Thread.Sleep(100);
        }
    }
    static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    static void Show(string args = "")
    {
        if (args.Length == 0 && UserSession.Send(1)) return;
        Process.Start(new ProcessStartInfo(Path.Combine(ServiceInstaller.InstallDirectory, "SukaControl.exe"), args) { UseShellExecute = true });
    }
    static void Execute(string name)
    {
        switch (name)
        {
            case "project": Shell(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe")); break;
            case "settings": Shell("ms-settings:"); break;
            case "show": Show(); break;
            case "f7":
                var preferences = Preferences.Load(); if (!preferences.CaptureF7) return;
                if (UserSession.Send(4)) return;
                switch (preferences.F7Action)
                {
                    case "copilot": using (var protocol = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("ms-copilot")) Shell(protocol != null ? "ms-copilot:" : "https://copilot.microsoft.com/"); break;
                    case "launchProgram": if (File.Exists(preferences.Program)) Shell(preferences.Program); break;
                    case "toggleEye": Show("--perform-f7"); break;
                }
                break;
        }
    }
}
