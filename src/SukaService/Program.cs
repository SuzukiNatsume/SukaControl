using SukaControl;
using System.ServiceProcess;
namespace SukaService;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if (args.Contains("--key-agent")) { DesktopKeyAgent.Run(); return; }
        Store.ServiceMode = true;
        ServiceBase.Run(new ControlService());
    }
}
