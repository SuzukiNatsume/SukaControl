using System.Text.Json;

namespace SukaControl;

internal sealed record PerformancePolicy(bool RestoreOnStartup = true, bool FullSpeedOnAC = false);

internal sealed class PerformanceAutomation
{
    readonly string path;
    readonly PerformanceMemory memory;
    readonly Func<int> read;
    readonly Action<int> write;
    readonly Func<bool?> power;
    readonly Action<string> log;
    readonly object gate = new();
    PerformancePolicy policy = new();
    bool? lastOnline;
    bool stopped;
    public PerformancePolicy Policy { get { lock (gate) return policy; } }
    public PerformanceAutomation(string path, PerformanceMemory memory, Func<int> read, Action<int> write, Func<bool?> power, Action<string> log)
    {
        this.path = path; this.memory = memory; this.read = read; this.write = write; this.power = power; this.log = log;
        try { if (File.Exists(path)) policy = JsonSerializer.Deserialize<PerformancePolicy>(File.ReadAllBytes(path)) ?? new(); }
        catch (Exception ex) { log("Performance options ignored: " + ex.Message); }
    }
    public void Start()
    {
        lock (gate)
        {
            lastOnline = power();
            // On AC, the explicitly enabled full-speed policy takes priority over saved mode.
            if (policy.FullSpeedOnAC && lastOnline == true) ApplyFullSpeed();
            else if (policy.RestoreOnStartup) memory.Restore();
            else memory.Capture();
        }
    }
    public void Set(byte option, bool enabled)
    {
        lock (gate)
        {
            if (stopped) throw new IOException("Service is shutting down.");
            var next = option switch { 1 => policy with { RestoreOnStartup = enabled }, 2 => policy with { FullSpeedOnAC = enabled }, _ => throw new ArgumentOutOfRangeException(nameof(option)) };
            string temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(next)); file.Flush(true); }
            File.Move(temporary, path, true);
            bool enablingAC = !policy.FullSpeedOnAC && next.FullSpeedOnAC;
            policy = next;
            if (enablingAC)
            {
                lastOnline = power();
                if (lastOnline == true) ApplyFullSpeed();
            }
        }
    }
    public void RefreshPower()
    {
        lock (gate)
        {
            if (stopped) return;
            bool? online = power();
            if (online == null) return;
            bool connected = online == true && lastOnline != true;
            lastOnline = online;
            if (policy.FullSpeedOnAC && connected) ApplyFullSpeed();
        }
    }
    void ApplyFullSpeed()
    {
        try
        {
            if (read() != 4) write(4);
            if (read() != 4) throw new IOException("Full speed readback failed.");
            memory.Capture(); log("AC automatic full speed confirmed");
        }
        catch (Exception ex)
        {
            log("AC automatic full speed failed; trying balanced: " + ex.Message);
            try { write(1); if (read() != 1) throw new IOException("Balanced readback failed."); memory.Capture(); }
            catch (Exception fallback) { log("AC balanced fallback failed: " + fallback.Message); }
        }
    }
    public T Manual<T>(Func<T> operation)
    {
        lock (gate)
        {
            if (stopped) throw new IOException("Service is shutting down.");
            T result = operation(); memory.Capture(); return result;
        }
    }
    public void Finish() { lock (gate) { stopped = true; memory.Finish(); } }
}
