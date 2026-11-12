using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SukaControl;
// Temporary read-only investigation: evaluate _STA, never an EC write or arbitrary method.
internal static class AcpiStatusProbe
{
    public static object Run()
    {
        using var handle = CreateFile(@"\\?\GLOBALROOT\Device\00000060", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return new { opened = false, error = Marshal.GetLastWin32Error() };
        byte[] input = [0x41, 0x65, 0x69, 0x42, 0x5f, 0x53, 0x54, 0x41];
        byte[] output = new byte[1024];
        bool success = DeviceIoControl(handle, 0x32c004, input, input.Length, output, output.Length, out int returned, IntPtr.Zero);
        return new { opened = true, success, error = success ? 0 : Marshal.GetLastWin32Error(), reply = Convert.ToHexString(output.AsSpan(0, Math.Clamp(returned, 0, output.Length))), account = System.Security.Principal.WindowsIdentity.GetCurrent().Name };
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
