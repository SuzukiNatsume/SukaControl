using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SukaControl;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [DllImport("kernel32.dll")] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] internal static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        public static DisplayDevice New() => new() { cb = Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };
    }
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool InstallColorProfile(string? machine, string file);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool UninstallColorProfile(string? machine, string file, bool delete);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsDisassociateColorProfileFromDevice(uint scope, string profile, string device);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsAssociateColorProfileWithDevice(uint scope, string profile, string device);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsSetDefaultColorProfile(uint scope, string device, uint type, uint subtype, uint id, string? profile);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsGetDefaultColorProfile(uint scope, string device, uint type, uint subtype, uint id, uint size, StringBuilder profile);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsGetUsePerUserProfiles(string device, uint deviceClass, out bool value);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WcsSetUsePerUserProfiles(string device, uint deviceClass, bool value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr wp, string lp, uint flags, uint timeout, out UIntPtr result);
    internal static void Check(bool ok, string action) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), action); }
}

internal record DisplayTarget(string GdiName, string DeviceId, string Name, string ColorKey)
{
    public override string ToString() => $"{GdiName} · {Name}";
}

internal sealed record ColorSnapshot(string ColorKey, string[] Profiles, bool PerUser, bool Existed)
{
    public string[] MissingParents { get; init; } = [];
    const string KeyPrefix = @"\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\";
    string RegistryPath
    {
        get
        {
            if (!ColorKey.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) || ColorKey[KeyPrefix.Length..].Length != 4 || !ColorKey[KeyPrefix.Length..].All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid display color key.");
            return @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}\" + ColorKey[KeyPrefix.Length..];
        }
    }
    public static ColorSnapshot Capture(DisplayTarget target)
    {
        var snapshot = new ColorSnapshot(target.ColorKey, [], false, false);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(snapshot.RegistryPath);
        var missing = new List<string>();
        string? parent = Path.GetDirectoryName(snapshot.RegistryPath);
        const string icmRoot = @"Software\Microsoft\Windows NT\CurrentVersion\ICM";
        while (parent != null && (parent.Equals(icmRoot, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(icmRoot + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            using var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(parent);
            if (existing == null) missing.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
        return snapshot with { Profiles = key?.GetValue("ICMProfile") as string[] ?? [], PerUser = key?.GetValue("UsePerUserProfiles") is int n && n != 0, Existed = key != null, MissingParents = missing.ToArray() };
    }
    public void Restore()
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryPath))
            foreach (string profile in key?.GetValue("ICMProfile") as string[] ?? []) Native.WcsDisassociateColorProfileFromDevice(1, profile, ColorKey);
        foreach (string profile in Profiles)
        {
            if (Path.GetFileName(profile) != profile) throw new InvalidDataException("Invalid profile name.");
            Native.Check(Native.WcsAssociateColorProfileWithDevice(1, profile, ColorKey), "Restore color association");
        }
        Native.Check(Native.WcsSetUsePerUserProfiles(ColorKey, 0x6d6e7472, PerUser), "Restore profile scope");
        if (!Existed) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
        foreach (string parent in MissingParents.OrderByDescending(p => p.Length))
        {
            const string root = @"Software\Microsoft\Windows NT\CurrentVersion\ICM";
            if (!parent.Equals(root, StringComparison.OrdinalIgnoreCase) && !parent.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid color registry path.");
            bool empty;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(parent)) empty = key != null && key.SubKeyCount == 0 && key.ValueCount == 0;
            if (empty) Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(parent, false);
        }
    }
}

internal sealed class DisplayControl : IDisposable
{
    readonly Dictionary<string, ushort[]> ramps = new();
    readonly Dictionary<string, string> rampDevices = new();
    public void ReconcileDisplays(IReadOnlyList<DisplayTarget> connected)
    {
        // A recycled GDI name must never apply a disconnected monitor's calibration to a new monitor.
        foreach (var name in ramps.Keys.ToArray())
            if (!connected.Any(d => d.GdiName == name && rampDevices.GetValueOrDefault(name) == d.DeviceId))
            { ramps.Remove(name); rampDevices.Remove(name); }
        if (ramps.Count == 0) EyeEnabled = false;
    }
    public bool EyeEnabled { get; private set; }
    public static List<DisplayTarget> Displays()
    {
        var result = new List<DisplayTarget>();
        for (uint i = 0; ; i++)
        {
            var adapter = Native.DisplayDevice.New(); if (!Native.EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & 1) == 0 || (adapter.StateFlags & 8) != 0) continue;
            for (uint j = 0; ; j++)
            {
                var monitor = Native.DisplayDevice.New(); if (!Native.EnumDisplayDevices(adapter.DeviceName, j, ref monitor, 1)) break;
                if ((monitor.StateFlags & 1) != 0) result.Add(new(adapter.DeviceName, monitor.DeviceID, monitor.DeviceString, monitor.DeviceKey));
            }
        }
        return result;
    }
    public static string CurrentProfile(DisplayTarget display)
    {
        bool user = Native.WcsGetUsePerUserProfiles(display.ColorKey, 0x6d6e7472, out var enabled) && enabled;
        var text = new StringBuilder(2048);
        if (Native.WcsGetDefaultColorProfile(user ? 1u : 0u, display.ColorKey, 0, 0, 0, 4096, text)) return text.ToString();
        return "";
    }
    public static string[] Profiles() => Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color"))
        .Concat(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Profiles")) ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Profiles")) : Array.Empty<string>())
        .Where(x => Path.GetExtension(x).Equals(".icc", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".icm", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName).ToArray();
    public static void ApplyProfile(DisplayTarget display, string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("找不到色彩配置", file);
        var header = File.ReadAllBytes(file);
        if (header.Length < 128 || Encoding.ASCII.GetString(header, 36, 4) != "acsp" || Encoding.ASCII.GetString(header, 16, 4) != "RGB ") throw new IOException("请选择 RGB ICC/ICM 显示器配置。");
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", Path.GetFileName(file));
        if (!File.Exists(installed)) Native.Check(Native.InstallColorProfile(null, file), "安装色彩配置失败");
        Native.Check(Native.WcsSetUsePerUserProfiles(display.ColorKey, 0x6d6e7472, true), "启用当前用户色彩设置失败");
        Native.Check(Native.WcsAssociateColorProfileWithDevice(1, Path.GetFileName(file), display.ColorKey), "关联显示器色彩配置失败");
        Native.Check(Native.WcsSetDefaultColorProfile(1, display.ColorKey, 0, 0, 0, Path.GetFileName(file)), "设置默认色彩配置失败");
        if (!Path.GetFileName(CurrentProfile(display)).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) throw new IOException("色彩配置回读校验失败。");
        Native.SendMessageTimeout(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "ICM", 2, 1000, out _);
        Store.Log($"ICC {display.DeviceId} => {file}");
    }
    public void Eye(DisplayTarget display, bool enabled, int warmth)
    {
        IntPtr dc = Native.CreateDC("DISPLAY", display.GdiName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new IOException("无法打开显示器。");
        try
        {
            if (!ramps.TryGetValue(display.GdiName, out var original))
            {
                original = new ushort[768]; if (!Native.GetDeviceGammaRamp(dc, original)) throw new IOException("显卡驱动不提供 Gamma 读取；HDR 模式下可能不可用。");
                ramps[display.GdiName] = original;
                rampDevices[display.GdiName] = display.DeviceId;
            }
            var ramp = enabled ? EyeRamp(original, warmth) : (ushort[])original.Clone();
            if (!Native.SetDeviceGammaRamp(dc, ramp)) throw new IOException("显卡未能应用本次色温调整。");
            EyeEnabled = enabled;
        }
        finally { Native.DeleteDC(dc); }
    }
    internal static ushort[] EyeRamp(ushort[] original, int warmth)
    {
        if (original.Length != 768) throw new ArgumentException("Invalid gamma ramp.");
        var ramp = (ushort[])original.Clone();
        double strength = Math.Clamp(warmth, 0, 100) / 100.0;
        if (strength == 0) return ramp;
        double[] factors = [1, 1 - .2 * strength, 1 - .45 * strength];
        for (int channel = 0; channel < 3; channel++) for (int i = 0; i < 256; i++)
        {
            // GDI requires every entry to remain within 32768 of the identity ramp.
            // Keep headroom even when the existing ICC calibration has a reduced white point.
            int identity = i * 257, value = (int)Math.Round(original[channel * 256 + i] * factors[channel]);
            ramp[channel * 256 + i] = (ushort)Math.Clamp(value, Math.Max(0, identity - 32000), Math.Min(65535, identity + 32000));
        }
        return ramp;
    }
    public void Dispose()
    {
        ReconcileDisplays(Displays());
        foreach (var (name, ramp) in ramps) { var dc = Native.CreateDC("DISPLAY", name, null, IntPtr.Zero); if (dc != IntPtr.Zero) { Native.SetDeviceGammaRamp(dc, ramp); Native.DeleteDC(dc); } }
        ramps.Clear();
        rampDevices.Clear();
        EyeEnabled = false;
    }
}
