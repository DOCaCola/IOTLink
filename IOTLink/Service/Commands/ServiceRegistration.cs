using IOTLinkAPI.Helpers;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace IOTLink.Service.Commands
{
    internal static class ServiceRegistration
    {
        private const string Name = PathHelper.APP_FOLDER_NAME;

        internal static void Install()
        {
            string executable = Path.Combine(AppContext.BaseDirectory, PathHelper.APP_SERVICE_NAME + ".exe");
            RunSc("create", Name, "binPath=", "\"" + executable + "\"", "start=", "auto", "DisplayName=", "IOT Link");
            try
            {
                RunSc("description", Name, "Service to provide Internet Of Things (IOT) integration using MQTT.");
                using var service = new ServiceController(Name);
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            catch
            {
                RunSc("delete", Name);
                throw;
            }
        }

        internal static void Uninstall()
        {
            using (var service = new ServiceController(Name))
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending)
                        service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            RunSc("delete", Name);
        }

        private static void RunSc(params string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new Win32Exception(process.ExitCode);
        }
    }
}
