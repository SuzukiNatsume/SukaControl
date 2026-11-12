using System.Text.Json;
using SukaControl;

var root = Path.Combine(Path.GetTempPath(), "SukaControl-memory-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Test(string name, Action<string> run)
{
    string path = Path.Combine(root, name + ".json");
    run(path); passed++; Console.WriteLine("PASS " + name);
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
int Saved(string path) { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.GetProperty("Mode").GetInt32(); }
void Seed(string path, int mode) => File.WriteAllText(path, JsonSerializer.Serialize(new { Mode = mode }));
try
{
    Test("first-start-records-actual-without-write", p => { int writes = 0; new PerformanceMemory(p, () => 2, _ => writes++, _ => { }).Restore(); Assert(writes == 0 && Saved(p) == 2); });
    Test("matching-mode-does-not-write", p => { Seed(p, 4); int writes = 0; new PerformanceMemory(p, () => 4, _ => writes++, _ => { }).Restore(); Assert(writes == 0 && Saved(p) == 4); });
    Test("different-mode-restores-and-verifies", p => { Seed(p, 3); int mode = 1; var writes = new List<int>(); new PerformanceMemory(p, () => mode, m => { writes.Add(m); mode = m; }, _ => { }).Restore(); Assert(writes.SequenceEqual([3]) && mode == 3 && Saved(p) == 3); });
    Test("restore-error-falls-back-balanced", p => { Seed(p, 4); int mode = 2; var writes = new List<int>(); new PerformanceMemory(p, () => mode, m => { writes.Add(m); if (m == 4) throw new IOException("AC unavailable"); mode = m; }, _ => { }).Restore(); Assert(writes.SequenceEqual([4, 1]) && mode == 1 && Saved(p) == 1); });
    Test("readback-mismatch-falls-back-balanced", p => { Seed(p, 3); int mode = 2; var writes = new List<int>(); new PerformanceMemory(p, () => mode, m => { writes.Add(m); if (m == 1) mode = m; }, _ => { }).Restore(); Assert(writes.SequenceEqual([3, 1]) && Saved(p) == 1); });
    Test("read-error-still-attempts-fallback", p => { Seed(p, 4); bool recovered = false; new PerformanceMemory(p, () => recovered ? 1 : throw new IOException(), m => { Assert(m == 1); recovered = true; }, _ => { }).Restore(); Assert(recovered && Saved(p) == 1); });
    Test("failed-fallback-does-not-claim-success", p => { Seed(p, 4); var logs = new List<string>(); new PerformanceMemory(p, () => 2, _ => throw new IOException(), logs.Add).Restore(); Assert(Saved(p) == 4 && logs.Any(s => s.StartsWith("Performance balanced fallback failed"))); });
    Test("corrupt-state-does-not-write-hardware", p => { File.WriteAllText(p, "broken"); int writes = 0; new PerformanceMemory(p, () => 2, _ => writes++, _ => { }).Restore(); Assert(writes == 0 && Saved(p) == 2); });
    Test("invalid-mode-is-ignored", p => { Seed(p, 9); int writes = 0; new PerformanceMemory(p, () => 1, _ => writes++, _ => { }).Restore(); Assert(writes == 0 && Saved(p) == 1); });
    Test("native-mode-change-is-checkpointed", p => { var memory = new PerformanceMemory(p, () => 2, _ => { }, _ => { }); memory.Remember(3); memory.Remember(0); Assert(Saved(p) == 3); memory.Remember(4); Assert(Saved(p) == 4); });
    Test("shutdown-reads-latest-and-freezes-later-observations", p => { var memory = new PerformanceMemory(p, () => 4, _ => { }, _ => { }); memory.Remember(2); memory.Finish(); memory.Remember(1); memory.Finish(); Assert(Saved(p) == 4); });
    Test("shutdown-read-error-preserves-last-good", p => { var memory = new PerformanceMemory(p, () => throw new IOException(), _ => { }, _ => { }); memory.Remember(3); memory.Finish(); Assert(Saved(p) == 3); });
    Console.WriteLine($"{passed} performance persistence tests passed");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "SukaControl-memory-tests-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
