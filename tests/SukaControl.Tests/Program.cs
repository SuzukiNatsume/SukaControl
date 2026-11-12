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
PerformanceAutomation Automation(string p, Func<int> read, Action<int> write, Func<bool?> power, PerformancePolicy? policy = null)
{
    if (policy != null) File.WriteAllText(p + ".options", JsonSerializer.Serialize(policy));
    return new(p + ".options", new PerformanceMemory(p, read, write, _ => { }), read, write, power, _ => { });
}
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
    Test("policy-defaults-preserve-existing-behavior", p => { var a = Automation(p, () => 1, _ => throw new Exception(), () => true); Assert(a.Policy.RestoreOnStartup && !a.Policy.FullSpeedOnAC); });
    Test("disabled-startup-restore-never-writes-mode", p => { Seed(p, 4); int writes = 0; var a = Automation(p, () => 2, _ => writes++, () => false, new(false, false)); a.Start(); Assert(writes == 0 && Saved(p) == 2); });
    Test("AC-startup-takes-priority-over-restoration", p => { Seed(p, 2); int mode = 1; var writes = new List<int>(); var a = Automation(p, () => mode, m => { mode = m; writes.Add(m); }, () => true, new(true, true)); a.Start(); Assert(writes.SequenceEqual([4]) && Saved(p) == 4); });
    Test("battery-startup-still-restores-if-enabled", p => { Seed(p, 2); int mode = 1; var a = Automation(p, () => mode, m => mode = m, () => false, new(true, true)); a.Start(); Assert(mode == 2); });
    Test("AC-edge-only-and-respects-manual-override", p => { int mode = 2; bool ac = false; var writes = new List<int>(); var a = Automation(p, () => mode, m => { mode = m; writes.Add(m); }, () => ac, new(false, true)); a.Start(); ac = true; a.RefreshPower(); a.Manual(() => mode = 2); a.RefreshPower(); Assert(mode == 2 && writes.SequenceEqual([4])); ac = false; a.RefreshPower(); Assert(mode == 2); ac = true; a.RefreshPower(); Assert(mode == 4 && writes.SequenceEqual([4, 4])); });
    Test("enabling-on-AC-applies-once", p => { int mode = 2; var writes = new List<int>(); var a = Automation(p, () => mode, m => { mode = m; writes.Add(m); }, () => true, new(false, false)); a.Start(); a.Set(2, true); a.Manual(() => mode = 2); a.Set(2, true); a.Set(1, true); a.RefreshPower(); Assert(mode == 2 && writes.SequenceEqual([4])); });
    Test("disabled-AC-automation-does-not-switch", p => { int mode = 2; bool ac = false; var a = Automation(p, () => mode, _ => throw new Exception(), () => ac, new(false, true)); a.Start(); a.Set(2, false); ac = true; a.RefreshPower(); Assert(mode == 2 && !a.Policy.FullSpeedOnAC); });
    Test("unknown-power-does-not-retrigger-AC", p => { int mode = 2; bool? ac = true; var writes = new List<int>(); var a = Automation(p, () => mode, m => { mode = m; writes.Add(m); }, () => ac, new(false, true)); a.Start(); a.Manual(() => mode = 2); ac = null; a.RefreshPower(); ac = true; a.RefreshPower(); Assert(mode == 2 && writes.SequenceEqual([4])); });
    Test("already-full-speed-never-writes-again", p => { var a = Automation(p, () => 4, _ => throw new Exception(), () => true, new(false, true)); a.Start(); a.RefreshPower(); Assert(Saved(p) == 4); });
    Test("AC-full-speed-failure-uses-balanced-fallback", p => { int mode = 2; var writes = new List<int>(); var a = Automation(p, () => mode, m => { writes.Add(m); if (m == 4) throw new IOException(); mode = m; }, () => true, new(false, true)); a.Start(); Assert(mode == 1 && Saved(p) == 1 && writes.SequenceEqual([4, 1])); });
    Test("policy-survives-service-recreation", p => { var a = Automation(p, () => 2, _ => { }, () => false); a.Set(1, false); a.Set(2, true); var b = Automation(p, () => 2, _ => { }, () => false); Assert(!b.Policy.RestoreOnStartup && b.Policy.FullSpeedOnAC); });
    Test("shutdown-prevents-late-AC-switch", p => { int mode = 2; bool ac = false; var a = Automation(p, () => mode, _ => throw new Exception(), () => ac, new(false, true)); a.Start(); a.Finish(); ac = true; a.RefreshPower(); Assert(Saved(p) == 2); });
    Test("policy-IPC-rejects-unrelated-operations", p => { Assert(HardwareProtocol.Allowed(new(0x42)) && HardwareProtocol.Allowed(new(0x43, true, 1, 0)) && HardwareProtocol.Allowed(new(0x43, true, 2, 1))); Assert(!HardwareProtocol.Allowed(new(0x42, true)) && !HardwareProtocol.Allowed(new(0x43, true, 0, 1)) && !HardwareProtocol.Allowed(new(0x43, true, 2, 2)) && !HardwareProtocol.Allowed(new(0x43, true, 1, 1, Payload: []))); });
    Console.WriteLine($"{passed} performance persistence and automation tests passed");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "SukaControl-memory-tests-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
