using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text.Json;

namespace SukaControl;

internal static class Store
{
    public static bool ServiceMode { get; set; }
    public static string Root => Path.Combine(Environment.GetFolderPath(ServiceMode ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData), "SukaControl");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string PathFor(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name); }
    static readonly object logGate = new();
    public static void Log(string message) { try { lock (logGate) { string path = PathFor("activity.log"); if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, PathFor("activity.log.1"), true); File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n"); } } catch { } }
    public static bool Admin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

internal sealed class Preferences
{
    public string F7Action { get; set; } = "copilot";
    public string Program { get; set; } = "";
    public bool CaptureF7 { get; set; } = true;
    public int Warmth { get; set; } = 35;
    public string Display { get; set; } = "";
    public Dictionary<string, string> OriginalProfiles { get; set; } = new();
    public Dictionary<string, ColorSnapshot> ColorBackups { get; set; } = new();
    public static Preferences Load() { try { var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Store.PathFor("settings.json"))) ?? new(); if (value.F7Action is "toggleOverlay" or "showApp" or "cyclePerformance") value.F7Action = "copilot"; return value; } catch { return new(); } }
    public void Save()
    {
        string temporary = Store.PathFor("settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, Store.Json)); File.Move(temporary, Store.PathFor("settings.json"), true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class Firmware : IDisposable
{
    readonly object gate = new();
    readonly bool allowRemote;
    bool remote;
    public bool UsesService => remote;
    public Firmware(bool allowRemote = true) => this.allowRemote = allowRemote;
    ManagementObject? instance;
    public string Model { get; private set; } = "未知型号";
    public string Status { get; private set; } = "尚未连接";
    public bool Supported => Model.Equals("Redmi G Pro 2024", StringComparison.OrdinalIgnoreCase);
    public void Connect()
    {
        if (allowRemote && HardwareClient.Installed)
        {
            try
            {
                var reply = HardwareClient.Send(new(0));
                if (!reply.Ok) throw new IOException(reply.Error);
                Model = reply.Model; if (!Supported) throw new NotSupportedException(Model);
                remote = true; Status = "SukaControl Hardware Service"; return;
            }
            catch (Exception ex) { Store.Log("Service connection: " + ex.Message); if (!Store.Admin) throw; }
        }
        using (var search = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
        using (var results = search.Get()) foreach (ManagementObject o in results) using (o) Model = (string)o["Model"];
        if (!Supported) throw new NotSupportedException($"当前硬件协议仅适配 Redmi G Pro 2024，检测到 {Model}。");
        using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM MICommonInterface");
        using var devices = query.Get();
        foreach (ManagementObject o in devices) { instance = o; break; }
        if (instance == null) throw new IOException("未找到 MICommonInterface 固件接口。");
        Status = "固件已连接（独立 WMI）";
    }
    public static byte[] Packet(byte command, bool write, byte value = 0, byte value2 = 0)
    {
        var data = new byte[32]; data[1] = write ? (byte)0xfb : (byte)0xfa; data[3] = command; data[4] = value; data[6] = value2; return data;
    }
    public byte[] Call(byte command, bool write = false, byte value = 0, byte value2 = 0)
    {
        lock (gate)
        {
            byte[] bytes;
            if (remote)
            {
                var reply = HardwareClient.Send(new(command, write, value, value2));
                if (!reply.Ok) throw new IOException(reply.Error);
                bytes = reply.Data ?? throw new IOException("固件未返回数据。");
            }
            else
            {
                if (instance == null) throw new IOException("固件未连接；请以管理员身份启动。");
                using var input = instance.GetMethodParameters("MiInterface"); input["InData"] = Packet(command, write, value, value2);
                using var output = instance.InvokeMethod("MiInterface", input, null);
                bytes = output?["OutData"] as byte[] ?? throw new IOException("固件未返回数据。");
            }
            Store.Log($"WMI {(write ? "SET" : "GET")} {command:X2} {value:X2} {value2:X2}: {Convert.ToHexString(bytes)}");
            if (bytes.Length < 8 || bytes[1] != 0x80 || (!write && bytes[3] != command)) throw new IOException($"固件拒绝命令 {command:X2}：{Convert.ToHexString(bytes)}");
            return bytes;
        }
    }
    public int Performance() => Call(8)[4];
    public bool FnLocked() => Call(0x0a, false, 1)[6] == 1;
    public bool WinLocked() => Call(0x0a, false, 2)[6] == 1;
    public void SetFnLocked(bool locked) => SetKeyLock(1, locked);
    public void SetWinLocked(bool locked) => SetKeyLock(2, locked);
    void SetKeyLock(byte key, bool locked)
    {
        Call(0x0a, true, key, locked ? (byte)1 : (byte)0); Thread.Sleep(300);
        if ((Call(0x0a, false, key)[6] == 1) != locked) throw new IOException("按键锁定状态回读不一致。");
    }
    // OEM WMAA GET 0x0B reads BIOS IGDS. Do not substitute a saved UI preference.
    public int GraphicsMode() => Call(0x0b)[4];
    public void SetGraphicsMode(int mode)
    {
        if (mode is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(mode));
        Call(0x0b, true, (byte)mode);
        Store.Log($"Graphics requested {mode}; reboot required");
    }
    public bool Overdrive() => Call(0x0a, false, 6)[6] == 1;
    public void SetOverdrive(bool on)
    {
        Call(0x0a, true, 6, on ? (byte)1 : (byte)0);
        Thread.Sleep(500);
        if (Overdrive() != on) throw new IOException("屏幕加速回读校验失败。");
    }
    public void SetPerformance(int mode)
    {
        if (mode is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode >= 3 && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) throw new IOException("高性能模式需要接通电源。");
        Call(8, true, (byte)mode);
        Thread.Sleep(250);
        if (Performance() != mode) throw new IOException("固件回读的性能档位与请求不符，未确认切换成功。");
    }
    public void Dispose() => instance?.Dispose();
}

internal record Sample(double Cpu, double Memory, double UsedGb, double TotalGb, string Gpu, double? GpuUsage, double? GpuTemp, double? GpuPower, double? GpuClock, double? CpuTemp, int? CpuFan, int? GpuFan, string Note);

internal sealed class Telemetry
{
    ulong lastIdle, lastKernel, lastUser;
    public async Task<Sample> Read(Firmware fw)
    {
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        ulong total = kernel + user - lastKernel - lastUser, free = idle - lastIdle;
        double cpu = lastKernel == 0 || total == 0 ? 0 : 100.0 * (total - Math.Min(total, free)) / total;
        lastIdle = idle; lastKernel = kernel; lastUser = user;
        var mem = new Native.MemoryStatus(); mem.Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(mem); Native.GlobalMemoryStatusEx(ref mem);
        double gb = 1024.0 * 1024 * 1024;
        double? temp = null; int? fan1 = null, fan2 = null; string note = "";
        try { var f = fw.Call(9); if (f.Length >= 14) { int t = BitConverter.ToInt32(f, 10); if (t is > 0 and < 120) temp = t; int cf = BitConverter.ToUInt16(f, 4), gf = BitConverter.ToInt32(f, 6); if (cf is >= 0 and < 20000) fan1 = cf; if (gf is >= 0 and < 20000) fan2 = gf; } } catch (Exception ex) { note = ex.Message; }
        string gpu = "未检测到 NVIDIA GPU"; double? usage = null, gt = null, power = null, clock = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,utilization.gpu,temperature.gpu,power.draw,clocks.gr --format=csv,noheader,nounits") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p != null)
            {
                using var timeout = new CancellationTokenSource(2500);
                var output = await p.StandardOutput.ReadToEndAsync(timeout.Token); await p.WaitForExitAsync(timeout.Token);
                if (p.ExitCode == 0) { var fields = output.Split('\n')[0].Split(',').Select(x => x.Trim()).ToArray(); if (fields.Length >= 5) { gpu = fields[0]; usage = Number(fields[1]); gt = Number(fields[2]); power = Number(fields[3]); clock = Number(fields[4]); } }
            }
        }
        catch { gpu = "GPU 数据不可用（需要 NVIDIA 显卡驱动）"; }
        return new(cpu, mem.Load, (mem.TotalPhysical - mem.AvailablePhysical) / gb, mem.TotalPhysical / gb, gpu, usage, gt, power, clock, temp, fan1, fan2, note);
    }
    static double? Number(string text) => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
