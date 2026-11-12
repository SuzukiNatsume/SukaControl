using System.Text.Json;

namespace SukaControl;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--language")) L.SetForPreview(args.SkipWhile(x => x != "--language").Skip(1).FirstOrDefault() ?? "en");
        if (args.Contains("--test-keys")) { Diagnostics.TestKeys(); return; }
        if (args.Contains("--test-eye-limit")) { Diagnostics.TestEyeLimit(); return; }
        if (args.Contains("--install-service"))
        {
            try { ServiceInstaller.Install(args.SkipWhile(x => x != "--owner-sid").Skip(1).FirstOrDefault()); }
            catch (Exception ex) { Store.Log(ex.ToString()); File.WriteAllText(Store.PathFor("install-result.json"), JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, Store.Json)); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--uninstall"))
        {
            try
            {
                if (!Store.Admin) { using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args)) { UseShellExecute = true, Verb = "runas" }); child?.WaitForExit(); Environment.ExitCode = child?.ExitCode ?? 1; return; }
                if (!args.Contains("--quiet") && MessageBox.Show(L.T("uninstallConfirm"), "SukaControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ServiceInstaller.Uninstall();
            }
            catch (Exception ex) { Store.Log(ex.ToString()); if (!args.Contains("--quiet")) MessageBox.Show(L.Error(ex), "SukaControl"); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--exit")) { UserSession.Send(2); return; }
        if (args.Contains("--cleanup-user")) { if (!UserSession.Send(3)) ServiceInstaller.RestoreUserState(); return; }
        if (args.Contains("--enable-startup-only")) { StartupTask.Set(true); return; }
        if (args.Contains("--selftest")) { Diagnostics.SelfTest(); return; }
        if (args.Contains("--diagnose")) { Diagnostics.Write(args.SkipWhile(x => x != "--diagnose").Skip(1).FirstOrDefault()).GetAwaiter().GetResult(); return; }
        if (args.Contains("--exercise-keyboard")) { Diagnostics.ExerciseKeyboard().GetAwaiter().GetResult(); return; }
        if (args.Contains("--validate")) { Diagnostics.Validate().GetAwaiter().GetResult(); return; }
        using var mutex = new Mutex(true, @"Local\SukaControl-" + Environment.UserName, out var first);
        bool startup = args.Contains("--startup") && !args.Contains("--perform-f7");
        if (!first) { if (startup) return; if (args.Contains("--perform-f7")) UserSession.Send(1); if (!UserSession.Send(args.Contains("--perform-f7") ? (byte)4 : (byte)1)) MessageBox.Show(L.T("alreadyRunning"), "SukaControl"); return; }
        var app = new System.Windows.Application { ThemeMode = System.Windows.ThemeMode.System };
        app.DispatcherUnhandledException += (_, e) => { Store.Log(e.Exception.ToString()); System.Windows.MessageBox.Show(L.Error(e.Exception), "SukaControl"); e.Handled = true; };
        var window = new FluentWindow();
        using var session = new UserSession(async command => await window.Dispatcher.InvokeAsync(async () => { if (command == 1) window.Restore(); else if (command == 4) await window.RunF7(); else await window.Exit(command == 3); }).Task.Unwrap());
        if (args.Contains("--perform-f7")) window.Dispatcher.BeginInvoke(async () => { await window.HardwareReady; await window.RunF7(); });
        if (args.Contains("--test-performance-ui")) window.Loaded += async (_, _) => { await window.HardwareReady; var result = await window.TestPerformanceUI(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "performance-ui-test.json"), JsonSerializer.Serialize(result, Store.Json)); await window.Exit(); };
        if (args.Contains("--test-lighting-ui")) window.Loaded += async (_, _) => { await window.HardwareReady; var result = await window.TestLightingUI(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "lighting-ui-test.json"), JsonSerializer.Serialize(result, Store.Json)); await window.Exit(); };
        if (args.Contains("--test-software-lighting-ui")) window.Loaded += async (_, _) => { await window.HardwareReady; var result = await window.TestSoftwareLightingUI(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "software-lighting-ui-test.json"), JsonSerializer.Serialize(result, Store.Json)); await window.Exit(); };
        if (args.Contains("--test-lighting-readback-ui")) window.Loaded += async (_, _) => { await window.HardwareReady; var result = await window.TestLightingReadbackUI(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "lighting-readback-ui-test.json"), JsonSerializer.Serialize(result, Store.Json)); await window.Exit(); };
        if (args.Contains("--test-audio-lighting-ui")) window.Loaded += async (_, _) => { await window.HardwareReady; var result = await window.TestAudioLightingUI(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "audio-lighting-ui-test.json"), JsonSerializer.Serialize(result, Store.Json)); await window.Exit(); };
        if (args.Contains("--screenshot")) window.Loaded += async (_, _) => { await Task.Delay(3500); string page = args.SkipWhile(x => x != "--page").Skip(1).FirstOrDefault() ?? "monitor"; window.SelectPage(page); await Task.Delay(200); window.SavePreview(Path.Combine(AppContext.BaseDirectory, "preview-" + L.Language + "-" + page + ".png")); await window.Exit(); };
        if (startup) { app.MainWindow = window; app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose; window.StartInTray(); app.Run(); }
        else app.Run(window);
    }
}

internal static class Diagnostics
{
    public static void TestEyeLimit()
    {
        var result = new Dictionary<string, object?>();
        IntPtr dc = IntPtr.Zero; ushort[]? original = null;
        try
        {
            var display = DisplayControl.Displays().First(); dc = Native.CreateDC("DISPLAY", display.GdiName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new IOException("No display DC.");
            original = new ushort[768]; Native.Check(Native.GetDeviceGammaRamp(dc, original), "Read original gamma");
            for (int warmth = 1; warmth <= 100; warmth++)
            {
                var ramp = DisplayControl.EyeRamp(original, warmth);
                for (int c = 0; c < 3; c++) for (int i = 0; i < 256; i++)
                    if (Math.Abs(ramp[c * 256 + i] - i * 257) > 32000 || i > 0 && ramp[c * 256 + i] < ramp[c * 256 + i - 1]) throw new Exception("Gamma range/monotonicity failure.");
            }
            if (!DisplayControl.EyeRamp(original, 0).SequenceEqual(original)) throw new Exception("Zero warmth changed calibration.");
            var maximum = DisplayControl.EyeRamp(original, 100);
            Native.Check(Native.SetDeviceGammaRamp(dc, maximum), "Apply maximum warmth"); Thread.Sleep(100);
            var readback = new ushort[768]; Native.Check(Native.GetDeviceGammaRamp(dc, readback), "Read maximum warmth");
            int error = maximum.Zip(readback, (a, b) => Math.Abs(a - b)).Max();
            result["maximumWarmthReadbackError"] = error;
            result["requestedWhitePoint"] = new[] { maximum[255], maximum[511], maximum[767] };
            result["actualWhitePoint"] = new[] { readback[255], readback[511], readback[767] };
            if (error > 512) throw new Exception("Display did not apply the requested maximum warmth.");
            result["passed"] = true;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); Environment.ExitCode = 1; }
        finally
        {
            if (dc != IntPtr.Zero && original != null)
            {
                bool set = Native.SetDeviceGammaRamp(dc, original); var after = new ushort[768]; bool read = Native.GetDeviceGammaRamp(dc, after);
                bool restored = set && read && after.Zip(original, (a, b) => Math.Abs(a - b)).Max() <= 512;
                result["originalRestored"] = restored; if (!restored) { result["passed"] = false; Environment.ExitCode = 1; }
            }
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "eye-limit-test.json"), JsonSerializer.Serialize(result, Store.Json));
        }
    }
    public static void TestKeys()
    {
        var failures = new List<string>();
        void Check(bool value, string name) { if (!value) failures.Add(name); }
        Check(KeyService.Decode([1, 1, 0]) == "project" && KeyService.Decode([1, 24, 1]) == "f7" && KeyService.Decode([1, 10, 0]) == "show" && KeyService.Decode([1, 27, 0]) == "settings", "Firmware F6-F9 routing");
        Check(KeyService.Decode([1, 7, 0]) == null && KeyService.Decode([1, 7, 1]) == null && KeyService.Decode([1, 5, 10]) == null, "No duplicate Fn+Esc/F10 action");
        Check(KeyService.Decode([1, 25, 1]) == null && KeyService.Decode([2, 24, 1]) == null && KeyService.Decode([1]) == null, "Ignore releases and unrelated events");
        Check(new byte[] { 1, 2, 3, 4 }.All(mode => KeyService.Decode([1, 0x16, mode]) == null), "Native performance notifications never request another mode change");
        Check(HardwareProtocol.Allowed(new(0x40)) && HardwareProtocol.Allowed(new(0x41, Ack: 1)) && !HardwareProtocol.Allowed(new(0x40, true)) && !HardwareProtocol.Allowed(new(0x41, Ack: -1)), "State/action IPC boundaries");
        Check(HardwareProtocol.Allowed(new(0x50, true, 1, Payload: [])) && !HardwareProtocol.Allowed(new(0x50, true, 0xff, Payload: [])) && !HardwareProtocol.Allowed(new(0x50, true, 2, Payload: new byte[61])), "Lighting IPC boundaries");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "key-tests.json"), JsonSerializer.Serialize(new { passed = failures.Count == 0, failures }, Store.Json)); Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
    public static async Task<string> Write(string? output = null)
    {
        var data = new Dictionary<string, object?> { ["time"] = DateTimeOffset.Now, ["admin"] = Store.Admin, ["vendorDependencies"] = Array.Empty<string>() };
        using var fw = new Firmware();
        try { fw.Connect(); data["model"] = fw.Model; data["performancePacket"] = Convert.ToHexString(fw.Call(8)); data["sensorPacket"] = Convert.ToHexString(fw.Call(9)); data["graphicsPacket"] = Convert.ToHexString(fw.Call(0x0b)); } catch (Exception ex) { data["firmwareError"] = ex.ToString(); data["model"] = fw.Model; }
        var lighting = new Lighting();
        data["hardwareService"] = fw.UsesService;
        try { lighting.Detect(); data["hidPath"] = lighting.DevicePath; data["hidConfig"] = Convert.ToHexString(await lighting.ReadConfig()); data["keyboard"] = Convert.ToHexString(await lighting.ReadKeyboard()); } catch (Exception ex) { data["hidError"] = ex.ToString(); }
        data["displays"] = DisplayControl.Displays().Select(d => new { d.GdiName, d.DeviceId, d.Name, profile = DisplayControl.CurrentProfile(d) });
        data["profiles"] = DisplayControl.Profiles();
        data["telemetry"] = await new Telemetry().Read(fw);
        output ??= Store.PathFor("diagnostic.json"); File.WriteAllText(output, JsonSerializer.Serialize(data, Store.Json)); return output;
    }
    public static void SelfTest()
    {
        var failures = new List<string>();
        void Check(bool test, string name) { if (!test) failures.Add(name); }
        var perf = Firmware.Packet(8, true, 4); Check(perf.Length == 32 && perf[1] == 0xfb && perf[3] == 8 && perf[4] == 4 && perf.Where((_, i) => i != 1 && i != 3 && i != 4).All(x => x == 0), "performance packet");
        var od = Firmware.Packet(0xa, true, 6, 1); Check(Convert.ToHexString(od).StartsWith("00FB000A06000100"), "overdrive packet");
        var report = Lighting.Report(0xd, 0, 1, 7); Check(report.Length == 65 && Convert.ToHexString(report).StartsWith("0057AB0D000107"), "HID cover packet");
        Check(DisplayControl.Profiles().Any(p => Path.GetFileName(p) == "xiaomi_sRGB_base.icc"), "preserved Xiaomi profile");
        Check(HardwareProtocol.Allowed(new(8, true, 4)) && HardwareProtocol.Allowed(new(10, true, 6, 1)), "service allowed controls");
        Check(!HardwareProtocol.Allowed(new(8, true, 255)) && !HardwareProtocol.Allowed(new(9, true)) && !HardwareProtocol.Allowed(new(10, true, 3, 1)) && !HardwareProtocol.Allowed(new(0, true)) && !HardwareProtocol.Allowed(new(23, true)), "service rejects unsupported writes");
        Check(new[] { "zh-CN", "zh-SG", "zh-Hans" }.All(x => L.Select(x) == "zh-CN") && new[] { "zh-TW", "zh-HK", "zh-Hant" }.All(x => L.Select(x) == "zh-TW") && L.Select("ja-JP") == "ja" && L.Select("en-US") == "en", "system language selection");
        try { ProfileOwnership.Test(); } catch (Exception ex) { failures.Add("ICC ownership: " + ex.Message); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.json"), JsonSerializer.Serialize(new { passed = failures.Count == 0, failures }, Store.Json));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
    public static async Task ExerciseKeyboard()
    {
        var lighting = new Lighting(); lighting.Detect();
        var before = await lighting.ReadKeyboard();
        File.WriteAllText(Store.PathFor("keyboard-before.hex"), Convert.ToHexString(before));
        var payload = before.Skip(4).Take(60).ToArray();
        try
        {
            var changed = (byte[])payload.Clone(); changed[4] = payload[4] == 2 ? (byte)1 : (byte)2;
            await lighting.Exchange(2, changed);
            var after = await lighting.ReadKeyboard();
            File.WriteAllText(Store.PathFor("keyboard-exercise.json"), JsonSerializer.Serialize(new { before = Convert.ToHexString(before), after = Convert.ToHexString(after), confirmed = after[8] == changed[4] }, Store.Json));
        }
        finally { await lighting.Exchange(2, payload); var restored = await lighting.ReadKeyboard(); File.WriteAllText(Store.PathFor("keyboard-restored.hex"), Convert.ToHexString(restored)); }
    }

    public static async Task Validate()
    {
        var results = new Dictionary<string, object?>();
        async Task Test(string name, Func<Task> test) { try { await test(); results[name] = new { passed = true }; } catch (Exception ex) { results[name] = new { passed = false, error = ex.ToString() }; } File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "validation.json"), JsonSerializer.Serialize(results, Store.Json)); }
        using var fw = new Firmware();
        await Test("firmware", () => { fw.Connect(); return Task.CompletedTask; });
        await Test("performance", () => { int before = fw.Performance(); try { foreach (int mode in new[] { 2, 1, 3, 4 }) fw.SetPerformance(mode); } finally { fw.SetPerformance(before); } return Task.CompletedTask; });
        await Test("overdrive", () => { bool before = fw.Overdrive(); try { fw.SetOverdrive(!before); } finally { fw.SetOverdrive(before); } return Task.CompletedTask; });
        await Test("graphicsSameMode", () => { int before = fw.GraphicsMode(); fw.SetGraphicsMode(before); return Task.CompletedTask; });
        await Test("keyboardLocks", () => { bool fn = fw.FnLocked(), win = fw.WinLocked(); try { fw.SetFnLocked(!fn); fw.SetWinLocked(!win); } finally { fw.SetFnLocked(fn); fw.SetWinLocked(win); } return Task.CompletedTask; });
        await Test("sensors", async () => { var s = await new Telemetry().Read(fw); if (s.CpuTemp == null || s.CpuFan == null || s.GpuFan == null) throw new IOException("固件传感器数据不全"); results["sensorValues"] = s; });
        var light = new Lighting();
        await Test("lighting", async () =>
        {
            var before = await light.ReadConfig();
            var logo = await light.Exchange(0xc, 0); var bar = await light.Exchange(0xc, 1);
            File.WriteAllText(Store.PathFor("lighting-original.json"), JsonSerializer.Serialize(new { config = Convert.ToHexString(before), logo = Convert.ToHexString(logo), bar = Convert.ToHexString(bar) }, Store.Json));
            try
            {
                foreach (byte effect in new byte[] { 9, 2, 1, 3, 5, 6, 7, 8, 10 }) await light.Keyboard(effect, 2, 2, Color.Coral);
                await light.Enable(0, false); await light.Enable(0, true);
                await light.Accent(true, 2, Color.Orange);
                await light.Accent(false, 2, Color.Cyan);
                foreach (bool top in new[] { true, false }) foreach (byte effect in new byte[] { 9, 3, 4 }) await light.Accent(top, effect, Color.Coral);
                await light.Accent(true, 5, Color.Coral);
                await light.Enable(1, false); await light.Enable(2, false);
            }
            finally
            {
                await light.Exchange(0xd, logo.Skip(4).Take(3).ToArray());
                await light.Exchange(0xd, bar.Skip(4).Take(3).ToArray());
                await light.Exchange(2, before.Skip(4).Take(60).ToArray());
                var restored = await light.ReadConfig();
                if (!restored.AsSpan(4).SequenceEqual(before.AsSpan(4))) throw new IOException("灯效原始配置恢复校验失败。");
            }
        });
        await Test("colorProfile", () =>
        {
            var d = DisplayControl.Displays().First(); string before = DisplayControl.CurrentProfile(d); var snapshot = ColorSnapshot.Capture(d);
            if (string.IsNullOrEmpty(before)) throw new IOException("未找到原配置，跳过切换以保留原状态。");
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
            try { DisplayControl.ApplyProfile(d, Path.Combine(root, "xiaomi_sRGB_base.icc")); }
            finally { snapshot.Restore(); }
            return Task.CompletedTask;
        });
        await Test("eyeMode", () => { using var display = new DisplayControl(); var d = DisplayControl.Displays().First(); display.Eye(d, true, 25); display.Eye(d, false, 25); return Task.CompletedTask; });
    }
}
