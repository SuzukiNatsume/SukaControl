using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SukaControl;

internal record HardwareRequest(byte Command, bool Write = false, byte Value = 0, byte Value2 = 0, byte[]? Payload = null, long Ack = 0);
internal record HardwareReply(bool Ok, byte[]? Data, string Model, string? Error = null, KeyState? Keys = null, KeyAction? Action = null, PerformancePolicy? Policy = null);

internal static class HardwareProtocol
{
    internal const string PipeName = "SukaService.Hardware.v1";
    public static bool Allowed(HardwareRequest request)
    {
        if (request.Command == 0x42) return !request.Write && request.Value == 0 && request.Value2 == 0 && request.Payload == null && request.Ack == 0;
        if (request.Command == 0x43) return request.Write && request.Value is 1 or 2 && request.Value2 is 0 or 1 && request.Payload == null && request.Ack == 0;
        if (request.Command is 0x40 or 0x41) return !request.Write && request.Value == 0 && request.Value2 == 0 && request.Payload == null && request.Ack >= 0 && (request.Command == 0x41 || request.Ack == 0);
        if (request.Command == 0x50)
        {
            if (!request.Write || request.Value2 != 0 || request.Ack != 0 || request.Payload is not { Length: <= 61 } p) return false;
            return request.Value switch { 1 => p.Length == 0, 2 => p.Length == 60 && p[0] is 0 or 1 && p[4] <= 3 && (p[0] == 0 || p[1] != 0xff), 4 => p.Length == 61, 0xb => p.Length is 6 or 16 && p[0] <= 1, 0xc => p.Length == 1 && p[0] <= 1, 0xd => p.Length == 3 && p[0] <= 1 && p[1] is >= 1 and <= 3, _ => false };
        }
        if (request.Payload != null || request.Ack != 0) return false;
        if (!request.Write) return request.Value2 == 0 && (request.Command is 0 or 8 or 9 or 11 && request.Value == 0 || request.Command == 10 && request.Value is 1 or 2 or 6);
        return request.Command == 8 && request.Value is >= 1 and <= 4 && request.Value2 == 0 || request.Command == 11 && request.Value is 0 or 1 && request.Value2 == 0 || request.Command == 10 && request.Value is 1 or 2 or 6 && request.Value2 is 0 or 1;
    }
    public static async Task Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value); if (body.Length > 4096) throw new InvalidDataException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length); await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    public static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token); int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 4096) throw new InvalidDataException("Invalid request size.");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, token); return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException();
    }
}

internal static class HardwareClient
{
    public static bool Installed => Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SukaService") is { } key && DisposeTrue(key);
    static bool DisposeTrue(RegistryKey key) { key.Dispose(); return true; }
    public static HardwareReply Send(HardwareRequest request) => SendAsync(request).GetAwaiter().GetResult();
    static async Task<HardwareReply> SendAsync(HardwareRequest request)
    {
        using var timeout = new CancellationTokenSource(3500);
        using var pipe = new NamedPipeClientStream(".", HardwareProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(800, timeout.Token); await HardwareProtocol.Write(pipe, request, timeout.Token); return await HardwareProtocol.Read<HardwareReply>(pipe, timeout.Token);
    }
}

internal sealed class ControlService : ServiceBase
{
    readonly CancellationTokenSource stop = new();
    Task? worker;
    readonly object lifecycleGate = new();
    PerformanceAutomation? automation;
    public ControlService() { ServiceName = "SukaService"; CanStop = true; CanShutdown = true; CanHandlePowerEvent = true; AutoLog = false; }
    protected override void OnStart(string[] args) { worker = Task.Run(() => Serve(stop.Token)); }
    void CapturePerformance() { lock (lifecycleGate) automation?.Finish(); }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            if (powerStatus is PowerBroadcastStatus.PowerStatusChange or PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
                lock (lifecycleGate) automation?.RefreshPower();
        }
        catch (Exception ex) { Store.Log("Power notification: " + ex.Message); }
        return true;
    }
    protected override void OnShutdown() { CapturePerformance(); stop.Cancel(); worker?.Wait(TimeSpan.FromSeconds(5)); base.OnShutdown(); }
    protected override void OnStop() { CapturePerformance(); stop.Cancel(); worker?.Wait(TimeSpan.FromSeconds(5)); }
    async Task Serve(CancellationToken token)
    {
        try
        {
            string owner = File.ReadAllText(Path.Combine(ServiceInstaller.DataDirectory, "owner.sid")).Trim(); var ownerSid = new SecurityIdentifier(owner);
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new(ownerSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            using var firmware = new Firmware(false); firmware.Connect();
            var memory = new PerformanceMemory(Path.Combine(ServiceInstaller.DataDirectory, "performance-state.json"), firmware.Performance, firmware.SetPerformance, Store.Log);
            var performance = new PerformanceAutomation(Path.Combine(ServiceInstaller.DataDirectory, "performance-options.json"), memory, firmware.Performance, firmware.SetPerformance,
                () => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus switch { System.Windows.Forms.PowerLineStatus.Online => true, System.Windows.Forms.PowerLineStatus.Offline => false, _ => (bool?)null }, Store.Log);
            performance.Start();
            lock (lifecycleGate) automation = performance;
            try
            {
            var lighting = new Lighting(false);
            using var keys = new KeyService(firmware, lighting, owner);
            while (!token.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(HardwareProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl);
                try
                {
                    await pipe.WaitForConnectionAsync(token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(4000);
                    HardwareReply reply;
                    try
                    {
                        var request = await HardwareProtocol.Read<HardwareRequest>(pipe, timeout.Token);
                        if (!HardwareProtocol.Allowed(request)) throw new UnauthorizedAccessException("Unsupported hardware operation.");
                        if (request.Command is 0x40 or 0x41)
                        {
                            performance.RefreshPower();
                            var state = await keys.Read(request.Command == 0x41, request.Ack);
                            memory.Remember(state.State.PerformanceMode);
                            reply = new(true, null, firmware.Model, Keys: state.State, Action: state.Action, Policy: performance.Policy);
                        }
                        else if (request.Command is 0x42 or 0x43)
                        {
                            if (request.Command == 0x43) { performance.Set(request.Value, request.Value2 == 1); keys.InvalidatePerformance(); }
                            reply = new(true, null, firmware.Model, Policy: performance.Policy);
                        }
                        else if (request.Command == 0x50)
                        {
                            var data = await lighting.Exchange(request.Value, request.Payload!);
                            if (request.Value == 2) keys.InvalidateLight();
                            reply = new(true, data, firmware.Model);
                        }
                        else
                        {
                            byte[]? data = request.Command == 0 ? null : request.Write && request.Command == 8
                                ? performance.Manual(() => firmware.Call(request.Command, true, request.Value, request.Value2))
                                : firmware.Call(request.Command, request.Write, request.Value, request.Value2);
                            if (request.Write && request.Command == 10) keys.InvalidateState();
                            if (request.Write && request.Command == 8) keys.InvalidatePerformance();
                            reply = new(true, data, firmware.Model);
                        }
                    }
                    catch (Exception ex) { Store.Log(ex.ToString()); reply = new(false, null, firmware.Model, ex.Message); }
                    await HardwareProtocol.Write(pipe, reply, timeout.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Store.Log("Hardware pipe: " + ex); }
            }
            }
            finally { lock (lifecycleGate) automation = null; }
        }
        catch (Exception ex) { Store.Log("Hardware service: " + ex); Environment.ExitCode = 1; }
    }
}

internal static class ProfileOwnership
{
    internal static readonly string[] Names = ["xiaomi_D65P3_base.icc", "xiaomi_D65P3TosRGBCali.icc", "xiaomi_sRGB_base.icc"];
    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    internal static bool RecordMissing(IDictionary<string, string> owned, string source, string installed)
    {
        if (File.Exists(installed)) return false;
        string name = Path.GetFileName(source);
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid bundled profile.");
        owned[name] = Hash(source); return true;
    }
    internal static bool CanRemove(string name, string hash, string installed)
    {
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid profile ownership record.");
        return File.Exists(installed) && Hash(installed).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    internal static void Test()
    {
        // Isolated files exercise another computer's first-install, upgrade and modified-file cases.
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SukaControlOwnershipTest-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, Names[0]), installed = Path.Combine(root, "installed.icc");
        try
        {
            File.WriteAllText(source, "bundled profile"); File.WriteAllText(installed, "preexisting profile");
            var owned = new Dictionary<string, string>();
            if (RecordMissing(owned, source, installed) || owned.Count != 0) throw new Exception("Preexisting ICC must not be owned.");
            File.Delete(installed);
            if (!RecordMissing(owned, source, installed)) throw new Exception("New ICC must be tracked.");
            File.Copy(source, installed);
            if (!CanRemove(Names[0], owned[Names[0]], installed)) throw new Exception("New ICC must be removable.");
            if (RecordMissing(owned, source, installed) || owned.Count != 1) throw new Exception("Upgrade must retain original ownership.");
            File.WriteAllText(installed, "user replacement");
            if (CanRemove(Names[0], owned[Names[0]], installed)) throw new Exception("A user replacement must be preserved.");
        }
        finally { File.Delete(source); File.Delete(installed); Directory.Delete(root); }
    }
}

internal static class ServiceInstaller
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SukaControl");
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SukaControl");
    internal sealed class InstallState
    {
        public string OwnerSid { get; set; } = "";
        public Dictionary<string, string> AddedProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    static InstallState ReadState() { try { return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(Path.Combine(DataDirectory, "install-state.json"))) ?? new(); } catch { return new(); } }
    public static void Install(string? ownerSid)
    {
        if (!Store.Admin) throw new UnauthorizedAccessException();
        using (var preflight = new Firmware(false)) preflight.Connect();
        var owner = new SecurityIdentifier(ownerSid ?? WindowsIdentity.GetCurrent().User!.Value);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "SukaService.exe"))) throw new IOException("SukaService.exe is missing from the installation package.");
        LegacyMigration.Prepare(owner.Value);
        UserSession.Send(2);
        try { using var old = new ServiceController("SukaService"); if (old.Status != ServiceControllerStatus.Stopped) { old.Stop(); old.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } } catch (InvalidOperationException) { }
        string source = Path.GetFullPath(AppContext.BaseDirectory), target = Path.GetFullPath(InstallDirectory);
        foreach (var process in Process.GetProcessesByName("SukaControl"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try { if (process.MainModule?.FileName?.Equals(Path.Combine(target, "SukaControl.exe"), StringComparison.OrdinalIgnoreCase) == true && process.SessionId != 0 && !process.WaitForExit(10000)) throw new IOException("Close MiControl before updating."); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        if (!source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !source.TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            try { using var old = new ServiceController("SukaService"); if (old.Status != ServiceControllerStatus.Stopped) { old.Stop(); old.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8)); } } catch (InvalidOperationException) { }
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".deps.json") && !relative.EndsWith(".runtimeconfig.json") || relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".hex", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid install path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
            }
        }
        // Executable and configuration must not be writable by a normal desktop process.
        var folderSecurity = new DirectorySecurity(); folderSecurity.SetAccessRuleProtection(true, false);
        folderSecurity.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid }) folderSecurity.AddAccessRule(new(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folderSecurity.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(target).SetAccessControl(folderSecurity);
        Directory.CreateDirectory(DataDirectory); new DirectoryInfo(DataDirectory).SetAccessControl(folderSecurity);
        File.WriteAllText(Path.Combine(DataDirectory, "owner.sid"), owner.Value);
        var state = ReadState(); state.OwnerSid = owner.Value;
        foreach (string profile in Directory.GetFiles(Path.Combine(target, "Profiles"), "*.icc"))
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", Path.GetFileName(profile));
            if (ProfileOwnership.RecordMissing(state.AddedProfiles, profile, installed))
            {
                File.WriteAllText(Path.Combine(DataDirectory, "install-state.json"), JsonSerializer.Serialize(state, Store.Json));
                Native.Check(Native.InstallColorProfile(null, profile), "Install ICC profile");
            }
        }
        File.WriteAllText(Path.Combine(DataDirectory, "install-state.json"), JsonSerializer.Serialize(state, Store.Json));
        string executable = Path.Combine(target, "SukaControl.exe");
        string serviceExecutable = Path.Combine(target, "SukaService.exe");
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SukaService"))
        {
            if (key == null) Sc("create", "SukaService", "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "DisplayName=", "SukaService");
            else Sc("config", "SukaService", "binPath=", $"\"{serviceExecutable}\"", "start=", "auto");
        }
        Sc("description", "SukaService", "Hardware and firmware shortcut service for SukaControl.");
        Sc("failure", "SukaService", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000");
        using var service = new ServiceController("SukaService"); service.Refresh(); if (service.Status != ServiceControllerStatus.Running) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)); }
        HardwareReply? ping = null;
        for (int retry = 0; retry < 15; retry++) { try { ping = HardwareClient.Send(new(0)); if (ping.Ok) break; } catch { } Thread.Sleep(200); }
        if (ping?.Ok != true) throw new IOException("SukaService did not become ready.");
        using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SukaControl"))
        {
            key.SetValue("DisplayName", "SukaControl"); key.SetValue("DisplayVersion", "0.2.7"); key.SetValue("Publisher", "SuzukiNatsume"); key.SetValue("InstallLocation", target); key.SetValue("DisplayIcon", executable); key.SetValue("UninstallString", $"\"{executable}\" --uninstall"); key.SetValue("QuietUninstallString", $"\"{executable}\" --uninstall --quiet"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        }
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SukaControl.lnk"), executable);
        LegacyMigration.Complete(owner.Value);
        File.WriteAllText(Store.PathFor("install-result.json"), JsonSerializer.Serialize(new { success = true, directory = target, service = service.ServiceName }, Store.Json));
    }
    static void CreateShortcut(string path, string executable)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try { dynamic shortcut = shell.CreateShortcut(path); try { shortcut.TargetPath = executable; shortcut.WorkingDirectory = Path.GetDirectoryName(executable); shortcut.Description = "SukaControl"; shortcut.Save(); } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); } }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    public static void RestoreUserState()
    {
        foreach (var snapshot in Preferences.Load().ColorBackups.Values) snapshot.Restore();
        StartupTask.Set(false);
    }
    public static void Uninstall()
    {
        if (!Store.Admin) throw new UnauthorizedAccessException();
        UserSession.Send(3);
        foreach (var process in Process.GetProcessesByName("SukaControl"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.SessionId == 0 || process.MainModule?.FileName?.Equals(Path.Combine(InstallDirectory, "SukaControl.exe"), StringComparison.OrdinalIgnoreCase) != true) continue;
                    // The unelevated uninstaller waits for this process; only close the desktop UI.
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero && !process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(5000); }
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        RestoreUserState();
        var state = ReadState();
        try { using var service = new ServiceController("SukaService"); if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } Sc("delete", "SukaService"); } catch (InvalidOperationException) { }
        var colorRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
        foreach (var (name, hash) in state.AddedProfiles)
        {
            string path = Path.Combine(colorRoot, name);
            if (!ProfileOwnership.CanRemove(name, hash, path)) continue;
            foreach (var display in DisplayControl.Displays()) Native.WcsDisassociateColorProfileFromDevice(1, name, display.ColorKey);
            Native.UninstallColorProfile(null, name, true);
            if (File.Exists(path)) File.Delete(path);
        }
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SukaControl", false);
        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "SukaControl.lnk"); if (File.Exists(shortcut)) File.Delete(shortcut);
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "SukaControl.lnk"); if (File.Exists(desktop)) File.Delete(desktop);
        var userPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Store.Root };
        if (!string.IsNullOrEmpty(state.OwnerSid))
        {
            using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + new SecurityIdentifier(state.OwnerSid).Value);
            if (profile?.GetValue("ProfileImagePath") is string root && Path.IsPathFullyQualified(root)) userPaths.Add(Path.Combine(Environment.ExpandEnvironmentVariables(root), "AppData", "Local", "SukaControl"));
            using var run = Registry.Users.OpenSubKey(state.OwnerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("SukaControl", false);
        }
        string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var allowed = userPaths.Append(InstallDirectory).Append(DataDirectory).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (allowed.Any(p => !p.EndsWith(Path.DirectorySeparatorChar + "SukaControl", StringComparison.OrdinalIgnoreCase))) throw new IOException("Invalid cleanup target.");
        // Native PowerShell performs all filesystem cleanup in one shell and never follows junctions.
        string script = $"$ErrorActionPreference='Stop'; try {{ Wait-Process -Id {Environment.ProcessId} -Timeout 20 -ErrorAction SilentlyContinue }} catch {{ }}; " +
            "function Remove-OwnedPath([string]$p) { if (!(Test-Path -LiteralPath $p)) { return }; $item=Get-Item -LiteralPath $p -Force; " +
            "if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Remove-Item -LiteralPath $p -Force; return }; " +
            "if ($item.PSIsContainer) { foreach($c in Get-ChildItem -LiteralPath $p -Force) { Remove-OwnedPath $c.FullName } }; Remove-Item -LiteralPath $p -Force }; " +
            "$targets=@(" + string.Join(",", allowed.Select(Quote)) + "); foreach($target in $targets) { $resolved=[IO.Path]::GetFullPath($target); if (!$resolved.EndsWith('\\SukaControl',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid target' }; for($attempt=0;$attempt -lt 20;$attempt++) { try { Remove-OwnedPath $resolved; break } catch { Start-Sleep -Milliseconds 300 } } }";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-WindowStyle"); info.ArgumentList.Add("Hidden"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); Process.Start(info);
    }
    static void Sc(params string[] args)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(output + error);
    }
}
