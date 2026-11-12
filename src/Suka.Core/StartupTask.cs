using Microsoft.Win32;
namespace SukaControl;
internal static class StartupTask
{
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public static bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunPath);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovalPath);
            return run?.GetValue("SukaControl") is string command && command.Contains("SukaControl.exe", StringComparison.OrdinalIgnoreCase) && !(approved?.GetValue("SukaControl") is byte[] data && data.Length > 0 && data[0] is 3 or 7);
        }
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) key.SetValue("SukaControl", $"\"{Path.Combine(ServiceInstaller.InstallDirectory, "SukaControl.exe")}\" --tray");
        else key.DeleteValue("SukaControl", false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovalPath, true); approved?.DeleteValue("SukaControl", false);
    }
}
