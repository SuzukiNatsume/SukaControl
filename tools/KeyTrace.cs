using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
class KeyTrace {
 delegate IntPtr Hook(int code,IntPtr wp,IntPtr lp);
 static Hook callback=OnKey;static IntPtr handle;
 static IntPtr OnKey(int code,IntPtr wp,IntPtr lp){if(code>=0){int key=Marshal.ReadInt32(lp);if(key==0x47||key>=0x70||key>=0x10&&key<=0x12||key==0x1b||key==0x5b||key==0x5c){File.AppendAllText(@"D:\Suzuki\Xiaomi\research\fn-g-keyboard-trace.txt",DateTimeOffset.Now.ToString("O")+" vk="+key.ToString("X2")+" scan="+Marshal.ReadInt32(lp,4).ToString("X2")+" flags="+Marshal.ReadInt32(lp,8).ToString("X2")+" msg="+wp.ToInt64().ToString("X")+Environment.NewLine);}}return CallNextHookEx(handle,code,wp,lp);}
 [STAThread]static void Main(){handle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);var timer=new Timer();timer.Interval=90000;timer.Tick+=delegate{Application.ExitThread();};timer.Start();Application.Run();UnhookWindowsHookEx(handle);}
 [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,Hook cb,IntPtr module,uint thread);
 [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
 [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wp,IntPtr lp);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
}
