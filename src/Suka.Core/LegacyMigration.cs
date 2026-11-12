using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
namespace SukaControl;

internal static class LegacyMigration
{
    static string OldInstall => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MiControl");
    static string OldData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiControl");
    static string OwnerProfile(string sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + new SecurityIdentifier(sid).Value);
        return Environment.ExpandEnvironmentVariables(key?.GetValue("ProfileImagePath") as string ?? throw new IOException("Owner profile is unavailable."));
    }
    public static void Prepare(string sid)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MiControlHardware"))
        {
            if (key != null) { using var service = new ServiceController("MiControlHardware"); if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } }
        }
        try { using var pipe = new NamedPipeClientStream(".", "MiControl.UI." + sid, PipeDirection.InOut); pipe.Connect(700); pipe.WriteByte(2); pipe.ReadByte(); } catch (System.TimeoutException) { } catch (IOException) { }
        foreach (var process in Process.GetProcessesByName("MiControl")) using (process)
        {
            try { if (process.SessionId != 0 && process.MainModule?.FileName?.Equals(Path.Combine(OldInstall, "MiControl.exe"), StringComparison.OrdinalIgnoreCase) == true && !process.WaitForExit(10000)) throw new IOException("Close MiControl before upgrading."); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        string local = Path.Combine(OwnerProfile(sid), "AppData", "Local");
        CopyOwned(OldData, ServiceInstaller.DataDirectory);
        CopyOwned(Path.Combine(local, "MiControl"), Path.Combine(local, "SukaControl"));
        // Ownership is transferred, never reclassified as newly installed ICC data.
        string previous = Path.Combine(OldData, "install-state.json"), next = Path.Combine(ServiceInstaller.DataDirectory, "install-state.json");
        if (File.Exists(previous) && File.Exists(next))
        {
            var old = JsonSerializer.Deserialize<ServiceInstaller.InstallState>(File.ReadAllText(previous));
            var current = JsonSerializer.Deserialize<ServiceInstaller.InstallState>(File.ReadAllText(next));
            if (old != null && current != null) { foreach (var pair in old.AddedProfiles) current.AddedProfiles.TryAdd(pair.Key, pair.Value); File.WriteAllText(next, JsonSerializer.Serialize(current, Store.Json)); }
        }
    }
    static void CopyOwned(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if ((new DirectoryInfo(source).Attributes & FileAttributes.ReparsePoint) != 0) return;
        Directory.CreateDirectory(target);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string destination = Path.Combine(target, item.Name);
            if (item is DirectoryInfo) CopyOwned(item.FullName, destination);
            else if (!File.Exists(destination)) File.Copy(item.FullName, destination);
        }
    }
    public static void Complete(string sid)
    {
        using (var run = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Run", true))
        using (var approved = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true))
        {
            if (run?.GetValue("MiControl") is string old && old.Contains("MiControl.exe", StringComparison.OrdinalIgnoreCase) && run.GetValue("SukaControl") == null)
            {
                run.SetValue("SukaControl", $"\"{Path.Combine(ServiceInstaller.InstallDirectory, "SukaControl.exe")}\" --tray");
                if (approved?.GetValue("MiControl") is byte[] value) approved.SetValue("SukaControl", value);
            }
            run?.DeleteValue("MiControl", false); approved?.DeleteValue("MiControl", false);
        }
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MiControlHardware"))
            if (service != null) { var info = new ProcessStartInfo("sc.exe", "delete MiControlHardware") { UseShellExecute = false, CreateNoWindow = true }; using var process = Process.Start(info)!; process.WaitForExit(); if (process.ExitCode != 0) throw new IOException("Could not remove the previous service registration."); }
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MiControl", false);
        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MiControl.lnk"); if (File.Exists(shortcut)) File.Delete(shortcut);
        string oldLocal = Path.Combine(OwnerProfile(sid), "AppData", "Local", "MiControl");
        foreach (string root in new[] { OldInstall, OldData, oldLocal })
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.EndsWith("\\MiControl", StringComparison.OrdinalIgnoreCase) || resolved.Equals(Path.GetPathRoot(resolved), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid migration cleanup path.");
            RemoveTree(resolved);
        }
    }
    static void RemoveTree(string path)
    {
        if (!Directory.Exists(path)) return;
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
            foreach (var item in directory.EnumerateFileSystemInfos()) { if (item is DirectoryInfo) RemoveTree(item.FullName); else item.Delete(); }
        directory.Delete();
    }
}
