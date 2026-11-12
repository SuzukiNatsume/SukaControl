using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SukaControl;

internal interface ILighting
{
    Task<byte[]> Exchange(byte command, params byte[] payload);
    Task<byte[]> ReadConfig();
    Task Save();
}
internal sealed class Lighting(bool allowRemote = true) : ILighting
{
    readonly SemaphoreSlim gate = new(1);
    public string? DevicePath { get; private set; }
    public string Status { get; private set; } = "尚未检测";
    public void Detect()
    {
        var guid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        IntPtr list = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (uint i = 0; ; i++)
            {
                var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, i, ref item)) break;
                SetupDiGetDeviceInterfaceDetail(list, ref item, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(list, ref item, buffer, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4) ?? "";
                    if (!path.Contains("vid_1a86&pid_e302&mi_02", StringComparison.OrdinalIgnoreCase)) continue;
                    DevicePath = path; Status = "WCH RGB · HID 直连"; return;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new IOException("未找到 Redmi G Pro 2024 RGB 控制器（1A86:E302 MI_02）。");
        }
        finally { SetupDiDestroyDeviceInfoList(list); }
    }
    public static byte[] Report(byte command, params byte[] payload)
    {
        if (payload.Length > 61) throw new ArgumentOutOfRangeException(nameof(payload));
        var report = new byte[65]; report[1] = 0x57; report[2] = 0xab; report[3] = command; payload.CopyTo(report, 4); return report;
    }
    public async Task<byte[]> Exchange(byte command, params byte[] payload)
    {
        if (allowRemote && !Store.ServiceMode && HardwareClient.Installed)
        {
            var reply = await Task.Run(() => HardwareClient.Send(new(0x50, true, command, Payload: payload)));
            if (!reply.Ok) throw new IOException(reply.Error);
            return reply.Data ?? throw new IOException("No lighting response.");
        }
        await gate.WaitAsync();
        try
        {
            if (DevicePath == null) Detect();
            using var handle = CreateFile(DevicePath!, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开灯效设备；请关闭其他灯效控制工具。");
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 65, true);
            using var timeout = new CancellationTokenSource(1800);
            var request = Report(command, payload);
            await stream.WriteAsync(request, timeout.Token);
            // The per-pixel color command is write-only (OEM uses WriteFile, not a request/reply).
            if (command is 0x0b or 4) { if (command == 0x0b) Store.Log($"HID write-only {Convert.ToHexString(request)}"); return request; }
            var reply = new byte[65]; int n = await stream.ReadAsync(reply, timeout.Token);
            // This unnumbered HID returns 64 bytes on Windows, without the report ID.
            int start = n > 2 && reply[0] == 0x57 ? 0 : 1;
            if (n < start + 4 || reply[start] != 0x57 || reply[start + 1] != 0xab || reply[start + 2] != command) throw new IOException("灯效设备返回了不匹配的数据包。");
            var normalized = new byte[65]; Array.Copy(reply, start, normalized, 1, Math.Min(n - start, 64));
            Store.Log($"HID {Convert.ToHexString(request)} => {Convert.ToHexString(normalized)}");
            return normalized;
        }
        catch (OperationCanceledException) { throw new IOException("灯效设备响应超时，未确认设置成功。"); }
        finally { gate.Release(); }
    }
    public Task<byte[]> ReadConfig() => Exchange(1);
    public Task<byte[]> ReadKeyboard() => ReadConfig();
    // This is the OEM common-settings selector, not the flash-save operation (command 0x05).
    internal const byte NormalSettings = 1;
    internal const byte DirectSettings = 0;
    internal static byte[] SettingsPayload(byte[] state, bool normalMode)
    {
        if (state.Length < 64 || state[1] != 0x57 || state[2] != 0xab || state[3] != 1) throw new ArgumentException("Invalid lighting configuration.");
        var payload = state.Skip(4).Take(60).ToArray();
        // A software-driven per-pixel mode cannot run before the desktop is available.
        payload[0] = normalMode && payload[1] != 0xff ? NormalSettings : DirectSettings;
        return payload;
    }

    public async Task Keyboard(byte effect, int brightness, int speed, Color color, int direction = 1)
    {
        if (effect is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10)) throw new ArgumentOutOfRangeException(nameof(effect));
        var config = await ReadConfig();
        // B21 common-setting encoder (OEM 2.1.1.413, 0x180192d80).
        // Match the OEM B21 CommonSetting encoder: ordinary settings use report[4] = 1.
        var p = new byte[60]; p[0] = NormalSettings; p[1] = effect == 9 ? (byte)11 : effect;
        p[2] = 0; p[3] = (byte)Math.Clamp(direction, 0, 1); p[4] = (byte)Math.Clamp(brightness, 0, 3); p[5] = (byte)Math.Clamp(speed, 1, 4); p[7] = 0; p[8] = (byte)(config[12] | 2);
        if (effect == 9) { p[2] = 0xff; p[14] = (byte)(color.R / 2); p[15] = (byte)(color.G / 2); p[16] = (byte)(color.B / 2); }
        await Exchange(2, p);
        var state = await ReadKeyboard();
        if (state[5] != p[1] || state[8] != p[4]) throw new IOException("灯效已发送，但键盘效果或亮度回读不一致。");
    }
    public async Task Accent(bool logo, byte effect, Color color)
    {
        if (effect is not (2 or 3 or 4 or 5 or 9) || (!logo && effect == 5)) throw new ArgumentOutOfRangeException(nameof(effect));
        var current = await ReadConfig();
        var config = SettingsPayload(current, true);
        // OEM switches cover bit 2 and logo bits 6..7 using read-modify-write.
        config[8] = logo ? (byte)((config[8] & 0x3f) | (effect == 4 ? 0 : effect == 5 ? 0x80 : 0x40)) : (byte)(effect == 4 ? config[8] & ~4 : config[8] | 4);
        config[8] |= logo ? (byte)0x10 : (byte)0x20;
        await Exchange(2, config);
        if (effect is 4 or 5) { var verify = await ReadConfig(); if (verify[12] != config[8]) throw new IOException("灯区联动回读不一致。"); return; }
        await Exchange(0xd, logo ? (byte)0 : (byte)1, effect == 9 ? (byte)1 : effect, effect is 9 or 2 ? (byte)0xff : (byte)0);
        byte[] rgb = logo ? new byte[] { 0, 0, 0x7f, color.R, color.G, color.B } : new byte[] { 1, 0, 0x7f, color.R, color.G, color.B, 1, 0x7f, color.R, color.G, color.B, 2, 0x7f, color.R, color.G, color.B };
        await Exchange(0xb, rgb);
        var result = await Exchange(0xc, logo ? (byte)0 : (byte)1);
        if (result[5] != (effect == 9 ? 1 : effect)) throw new IOException("灯区模式回读不一致。");
    }

    public async Task Enable(int area, bool enabled)
    {
        byte bit = area switch { 0 => 2, 1 => 0x10, 2 => 0x20, _ => throw new ArgumentOutOfRangeException(nameof(area)) };
        var current = await ReadConfig(); var payload = SettingsPayload(current, true);
        payload[8] = enabled ? (byte)(payload[8] | bit) : (byte)(payload[8] & ~bit);
        await Exchange(2, payload); var after = await ReadConfig();
        if (((after[12] & bit) != 0) != enabled) throw new IOException("灯区开关回读不一致。");
    }
    public async Task Save()
    {
        // OEM B21PayloadWriter::SaveEffectParams sends 57 AB 05 with no payload.
        await Exchange(5);
    }

    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
