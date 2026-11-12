using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;

namespace SukaControl;

internal sealed class UserSession : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    static string Name => "SukaControl.UI." + WindowsIdentity.GetCurrent().User!.Value;
    public UserSession(Func<byte, Task> action)
    {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        worker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1, 1, acl);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(3000);
                    var data = new byte[1]; await pipe.ReadExactlyAsync(data, timeout.Token);
                    if (data[0] is < 1 or > 4) continue;
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token); await pipe.FlushAsync(timeout.Token);
                    await action(data[0]);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Store.Log("UI pipe: " + ex.Message); }
            }
        });
    }
    public static bool Send(byte command)
    {
        try { using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification); pipe.Connect(1000); pipe.WriteByte(command); return pipe.ReadByte() == 1; } catch { return false; }
    }
    public void Dispose() { stop.Cancel(); _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default); }
}
