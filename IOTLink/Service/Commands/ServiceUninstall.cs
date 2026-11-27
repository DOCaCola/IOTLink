using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.Windows;
using System;
using System.Linq;
using System.ServiceProcess;

namespace IOTLink.Service.Commands
{
    class ServiceUninstall : ICommand
    {
        private const string COMMAND_LINE = "uninstall";

        public string GetCommandLine()
        {
            return COMMAND_LINE;
        }

        public int ExecuteCommand(string[] args)
        {
            if (Program.IsService)
            {
                LoggerHelper.Verbose("Command {0} - Running on non-interactive mode. Skipping", COMMAND_LINE);
                return -1;
            }

            bool quiet = args.Contains("quiet", StringComparer.OrdinalIgnoreCase);
            try
            {
                bool serviceExists = ServiceController.GetServices().Any(s => s.ServiceName == "IOTLink");
                if (!serviceExists)
                {
                    LoggerHelper.Verbose("Command {0} - Service is currently not installed. Skipping", COMMAND_LINE);
                    if (!quiet)
                        WindowsAPI.ShowMessage("Service Installer", "Service is currently not installed.");
                    return -1;
                }

                ServiceRegistration.Uninstall();
                if (!quiet)
                    WindowsAPI.ShowMessage("Service Installer", "Service is uninstalled sucessfully.");

                LoggerHelper.Verbose("Command {0} - Service is uninstalled sucessfully.", COMMAND_LINE);
                return 0;
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("Command {0} - Error while running command: {1}", COMMAND_LINE, ex.ToString());
                if (!quiet)
                    WindowsAPI.ShowMessage("Service Installer", "Uninstall failed. Please, run as an administrator.");
            }

            return -1;
        }
    }
}
