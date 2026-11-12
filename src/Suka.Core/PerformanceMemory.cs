using System.Text.Json;

namespace SukaControl;

// Service-owned state: only actual, validated firmware modes are persisted.
internal sealed class PerformanceMemory(string path, Func<int> read, Action<int> write, Action<string> log)
{
    sealed record Snapshot(int Mode, DateTime SavedAtUtc);
    readonly object gate = new();
    int lastSaved;
    bool finished;
    static bool Valid(int mode) => mode is >= 1 and <= 4;

    public void Remember(int mode, bool force = false)
    {
        lock (gate)
        {
            if (finished || !Valid(mode) || !force && mode == lastSaved) return;
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(mode, DateTime.UtcNow));
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, path, true);
                lastSaved = mode;
            }
            catch (Exception ex) { log("Performance state save: " + ex.Message); }
        }
    }
    public void Capture()
    {
        lock (gate)
        {
            try { Remember(read(), true); }
            catch (Exception ex) { log("Performance shutdown read: " + ex.Message); }
        }
    }
    public void Finish()
    {
        lock (gate) { if (finished) return; Capture(); finished = true; }
    }
    public void Restore()
    {
        lock (gate)
        {
            int desired;
            try
            {
                if (!File.Exists(path)) { Capture(); return; }
                desired = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(path))?.Mode ?? 0;
                if (!Valid(desired)) throw new InvalidDataException("Invalid saved mode.");
            }
            catch (Exception ex) { log("Performance saved state ignored: " + ex.Message); Capture(); return; }
            try
            {
                int current = read();
                if (current != desired) { write(desired); current = read(); }
                if (current != desired) throw new IOException("Restored mode did not match firmware readback.");
                Remember(current, true);
                log("Performance restored/confirmed " + current);
            }
            catch (Exception ex)
            {
                log("Performance restore failed; falling back to balanced: " + ex.Message);
                try
                {
                    // Try the fallback even if the preceding read failed.
                    write(1);
                    int actual = read();
                    if (actual != 1) throw new IOException("Balanced mode readback failed.");
                    Remember(actual, true);
                    log("Performance fallback confirmed balanced");
                }
                catch (Exception fallback) { log("Performance balanced fallback failed: " + fallback.Message); }
            }
        }
    }
}
