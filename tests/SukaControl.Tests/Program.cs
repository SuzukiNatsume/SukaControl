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
    byte[] Config(byte effect = 11, byte brightness = 2) { var c = new byte[65]; c[1] = 0x57; c[2] = 0xab; c[3] = 1; c[5] = effect; c[8] = brightness; c[12] = 0xb6; c[18] = 10; c[19] = 20; c[20] = 30; return c; }
    Test("hardware-lighting-matches-OEM-common-settings-prefix", p => { var original = Config(); var payload = Lighting.SettingsPayload(original, true); Assert(payload.Length == 60 && payload[0] == 1 && payload.Skip(1).SequenceEqual(original.Skip(5).Take(59))); Assert(HardwareProtocol.Allowed(new(0x50, true, 2, Payload: payload))); });
    Test("direct-lighting-does-not-send-save", p => { var payload = Lighting.SettingsPayload(Config(), false); Assert(payload[0] == 0 && HardwareProtocol.Allowed(new(0x50, true, 2, Payload: payload))); });
    Test("software-pixel-mode-cannot-overwrite-boot-preset", p => { var payload = Lighting.SettingsPayload(Config(0xff), true); Assert(payload[0] == 0); payload[0] = 1; Assert(!HardwareProtocol.Allowed(new(0x50, true, 2, Payload: payload))); var realMode = Lighting.SettingsPayload(Config(0), true); Assert(realMode[0] == 1 && HardwareProtocol.Allowed(new(0x50, true, 2, Payload: realMode))); });
    Test("OEM-logo-and-cover-status-bits-are-distinct", p => { Assert(Lighting.AreaEnabled(0x20, 1) && !Lighting.AreaEnabled(0x20, 2)); Assert(Lighting.AreaEnabled(0x10, 2) && !Lighting.AreaEnabled(0x10, 1)); Assert(Lighting.AreaMask(0) == 3); });
    Test("explicit-lighting-off-is-preserved", p => { var state = Config(11, 0); state[12] &= 0xfd; var payload = Lighting.SettingsPayload(state, true); Assert(payload[4] == 0 && (payload[8] & 2) == 0); });
    Test("unknown-lighting-prefix-and-brightness-rejected", p => { var payload = Lighting.SettingsPayload(Config(), true); payload[0] = 2; Assert(!HardwareProtocol.Allowed(new(0x50, true, 2, Payload: payload))); payload[0] = 1; payload[4] = 4; Assert(!HardwareProtocol.Allowed(new(0x50, true, 2, Payload: payload))); });
    Test("malformed-lighting-readback-rejected", p => { bool rejected = false; try { Lighting.SettingsPayload(new byte[65], true); } catch (ArgumentException) { rejected = true; } Assert(rejected); });
    Test("lighting-apply-never-saves-and-close-rolls-back", p => { var h = new FakeLighting(); var session = new LightingSession(h); session.Apply(() => h.Brightness(3)).GetAwaiter().GetResult(); Assert(session.Pending && h.Config[8] == 3 && h.Saves == 0); session.Revert().GetAwaiter().GetResult(); Assert(!session.Pending && h.Config[8] == 2 && h.Saves == 0); });
    Test("lighting-save-is-explicit-and-keeps-applied-state", p => { var h = new FakeLighting(); var session = new LightingSession(h); session.Apply(() => h.Brightness(3)).GetAwaiter().GetResult(); session.Save().GetAwaiter().GetResult(); session.Revert().GetAwaiter().GetResult(); Assert(h.Saves == 1 && h.Config[8] == 3 && !session.Pending); });
    Test("several-previews-revert-to-original-baseline", p => { var h = new FakeLighting(); var session = new LightingSession(h); session.Apply(() => h.Brightness(1)).GetAwaiter().GetResult(); session.Apply(() => h.Brightness(3)).GetAwaiter().GetResult(); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[8] == 2 && h.Saves == 0); });
    Test("failed-lighting-save-keeps-rollback-available", p => { var h = new FakeLighting { FailSave = true }; var session = new LightingSession(h); session.Apply(() => h.Brightness(1)).GetAwaiter().GetResult(); bool failed = false; try { session.Save().GetAwaiter().GetResult(); } catch (IOException) { failed = true; } Assert(failed && session.Pending); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[8] == 2); });
    Test("failed-preview-action-can-still-roll-back", p => { var h = new FakeLighting(); var session = new LightingSession(h); try { session.Apply(async () => { await h.Brightness(3); throw new IOException(); }).GetAwaiter().GetResult(); } catch (IOException) { } session.Revert().GetAwaiter().GetResult(); Assert(h.Config[8] == 2 && h.Saves == 0); });
    Test("accent-preview-restores-mode-and-RGB", p => { var h = new FakeLighting(); var session = new LightingSession(h); session.Apply(() => { h.Logo[5] = 2; h.Color[6] = 99; return Task.CompletedTask; }).GetAwaiter().GetResult(); session.Revert().GetAwaiter().GetResult(); Assert(h.Logo[5] == 0 && h.Color[6] == 10 && h.Saves == 0); });
    Test("save-command-and-RGB-read-are-bounded", p => { Assert(HardwareProtocol.Allowed(new(0x50, true, 5, Payload: []))); Assert(!HardwareProtocol.Allowed(new(0x50, true, 5, Payload: [1]))); Assert(HardwareProtocol.Allowed(new(0x50, true, 0xa, Payload: [1])) && !HardwareProtocol.Allowed(new(0x50, true, 0xa, Payload: [2]))); Assert(HardwareProtocol.Allowed(new(0x50, true, 4, Payload: new byte[61])) && !HardwareProtocol.Allowed(new(0x50, true, 4, Payload: new byte[60]))); });
    Test("software-state-cannot-be-saved-to-controller", p => { var h = new FakeLighting(); var session = new LightingSession(h); session.Apply(() => { h.Config[5] = 0; return Task.CompletedTask; }).GetAwaiter().GetResult(); bool failed = false; try { session.Save().GetAwaiter().GetResult(); } catch (IOException) { failed = true; } Assert(failed && h.Saves == 0 && session.Pending); session.Revert().GetAwaiter().GetResult(); });
    Test("disabled-live-state-can-be-saved-without-reenabling", p => { var h = new FakeLighting(); h.Config[5] = 0; h.Config[12] = 0; var session = new LightingSession(h); session.Apply(() => Task.CompletedTask).GetAwaiter().GetResult(); session.Save().GetAwaiter().GetResult(); Assert(h.Saves == 1 && h.Config[5] == 11 && h.Config[12] == 0 && !session.Pending); });
    Test("firmware-FF-off-sentinel-is-saveable-and-revertible", p => { var h = new FakeLighting { OffSentinel = true }; h.Config[12] = 0; var session = new LightingSession(h); session.Apply(() => Task.CompletedTask).GetAwaiter().GetResult(); session.Revert().GetAwaiter().GetResult(); session.Apply(() => Task.CompletedTask).GetAwaiter().GetResult(); session.Save().GetAwaiter().GetResult(); Assert(h.Saves == 1 && h.Config[12] == 0 && !session.Pending); });
    Test("failed-off-save-keeps-original-state-for-rollback", p => { var h = new FakeLighting { FailSave = true, OffSentinel = true }; var before = h.Config[12]; var session = new LightingSession(h); session.Apply(() => { h.Config[12] = 0; return Task.CompletedTask; }).GetAwaiter().GetResult(); try { session.Save().GetAwaiter().GetResult(); } catch (IOException) { } Assert(session.Pending && h.Saves == 0); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[12] == before); });
    Test("off-preview-does-not-save-and-rolls-back", p => { var h = new FakeLighting(); var original = h.Config[12]; var session = new LightingSession(h); session.Apply(() => { h.Config[12] = 0; return Task.CompletedTask; }).GetAwaiter().GetResult(); Assert(h.Saves == 0); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[12] == original && h.Saves == 0); });
    Test("saved-follow-chain-stays-off-in-new-session", p => { var h = new FakeLighting(); h.Config[12] = 0xb3; var session = new LightingSession(h); session.Apply(() => { h.Config[12] &= 0xfc; return Task.CompletedTask; }).GetAwaiter().GetResult(); session.Save().GetAwaiter().GetResult(); var next = new LightingSession(h); Assert(h.Config[12] == 0x80 && h.Saves == 1 && !next.Pending); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[12] == 0x80); });
    Test("independent-areas-do-not-follow-keyboard-off", p => { Assert(Lighting.SavedAreaFlags(0x74) == 0x74); });
    Test("logo-follow-bar-closes-when-bar-off", p => { Assert(Lighting.SavedAreaFlags(0xa3) == 0x83); });
    Test("logo-follow-keyboard-ignores-independent-bar", p => { Assert(Lighting.SavedAreaFlags(0x34) == 0x14); });
    Test("save-never-reenables-already-off-followers", p => { Assert(Lighting.SavedAreaFlags(3) == 3); });
    Test("fallback-from-live-keeps-disabled-area-flags", p => { var state = Config(0); state[12] = 0x80; var known = Config(2); known[12] = 0x33; var result = Lighting.HardwareFallback(state, known); Assert(result[5] == 2 && result[12] == 0x80 && state[5] == 0); });
    Test("software-preview-uses-hardware-fallback-for-rollback", p => { var h = new FakeLighting(); var fallback = (byte[])h.Config.Clone(); h.Config[5] = 0xff; var session = new LightingSession(h, () => fallback); session.Apply(() => Task.CompletedTask).GetAwaiter().GetResult(); session.Revert().GetAwaiter().GetResult(); Assert(h.Config[5] == 11 && h.Saves == 0); });
    Test("audio-right-channel-is-not-ignored", p => Assert(Math.Abs(AudioSampleDecoder.ReadFrame([0, 0, 0, 64], 16, false) - .5f) < .001f));
    Test("audio-PCM32-is-not-treated-as-float", p => Assert(Math.Abs(AudioSampleDecoder.ReadFrame(BitConverter.GetBytes(1073741824), 32, false) - .5f) < .001f));
    Test("audio-PCM24-sign-extension", p => Assert(AudioSampleDecoder.ReadFrame([0, 0, 128], 24, false) == -1f && AudioSampleDecoder.ReadFrame([0, 0, 64], 24, false) == .5f));
    Test("audio-float-handles-invalid-channel", p => { var values = BitConverter.GetBytes(float.NaN).Concat(BitConverter.GetBytes(.25f)).ToArray(); Assert(AudioSampleDecoder.ReadFrame(values, 32, true) == .25f); });
    Test("audio-PCM8-midpoint-is-silence", p => Assert(AudioSampleDecoder.ReadFrame([128], 8, false) == 0));
    Test("lighting-delayed-readback-is-confirmed", p => { int reads = 0; var result = Lighting.Confirm(() => Task.FromResult(new byte[] { (byte)(++reads < 3 ? 1 : 3) }), state => state[0] == 3, "failed", 4, 0).GetAwaiter().GetResult(); Assert(result[0] == 3 && reads == 3); });
    Test("lighting-readback-retry-is-bounded", p => { int reads = 0; bool failed = false; try { Lighting.Confirm(() => { reads++; return Task.FromResult(new byte[] { 1 }); }, state => state[0] == 3, "failed", 3, 0).GetAwaiter().GetResult(); } catch (IOException) { failed = true; } Assert(failed && reads == 3); });
    Console.WriteLine($"{passed} performance, lighting and audio tests passed");
}

finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "SukaControl-memory-tests-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}

sealed class FakeLighting : ILighting
{
    public byte[] Config { get; } = new byte[65];
    public byte[] Logo { get; } = new byte[65];
    readonly byte[] bar = new byte[65], barColor = new byte[65];
    public byte[] Color { get; } = new byte[65];
    public int Saves;
    public bool FailSave;
    public bool OffSentinel;
    public FakeLighting()
    {
        foreach (var (state, command) in new[] { (Config, 1), (Logo, 12), (bar, 12), (Color, 10), (barColor, 10) }) { state[1] = 0x57; state[2] = 0xab; state[3] = (byte)command; }
        Config[5] = 11; Config[8] = 2; Config[12] = 0x32; bar[4] = barColor[4] = 1; bar[5] = 3; Color[6] = barColor[6] = 10; Color[7] = barColor[7] = 20; Color[8] = barColor[8] = 30;
    }
    public Task Brightness(byte value) { Config[8] = value; return Task.CompletedTask; }
    public Task<byte[]> ReadConfig() { var value = (byte[])Config.Clone(); if (OffSentinel && !Lighting.AreaEnabled(value[12], 0)) value[5] = 0xff; return Task.FromResult(value); }
    public Task Save() { if (FailSave) throw new IOException(); Saves++; return Task.CompletedTask; }
    public Task<byte[]> Exchange(byte command, params byte[] payload)
    {
        if (command == 1) return ReadConfig();
        if (command == 12) return Task.FromResult((byte[])(payload[0] == 0 ? Logo : bar).Clone());
        if (command == 10) return Task.FromResult((byte[])(payload[0] == 0 ? Color : barColor).Clone());
        if (command == 2) Array.Copy(payload, 0, Config, 4, 60);
        if (command == 13) Array.Copy(payload, 0, payload[0] == 0 ? Logo : bar, 4, 3);
        if (command == 11) Array.Copy(payload, 3, payload[0] == 0 ? Color : barColor, 6, 3);
        return Task.FromResult(Array.Empty<byte>());
    }
}
