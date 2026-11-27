using IOTLinkAgent.Agent.WSClient;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.Events;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IOTLinkAgent.Agent
{
    internal class AgentMain
    {
        private static readonly int MINIMUM_DELAY_CONFIG_CHANGE = 1;

        private static AgentMain _instance;

        private DateTime _lastConfigChange;

        public static AgentMain GetInstance()
        {
            if (_instance == null)
                _instance = new AgentMain();

            return _instance;
        }

        private AgentMain()
        {
            LoggerHelper.Trace("AgentMain instance created.");
            ApplicationConfigHelper.Init();
            ApplicationConfigHelper.SetEngineConfigReloadHandler(OnConfigChanged);
        }

        internal Task RunAsync(List<string> args)
        {
            var uri = new Uri(string.Concat(args));
            LoggerHelper.Verbose("Initializing WebSocketClient - Server URI: {0}", uri);
            return WebSocketClient.GetInstance().RunAsync(uri, SetupAgent);
        }

        private void OnConfigChanged(object sender, ConfigReloadEventArgs e)
        {
            if (_lastConfigChange == null || _lastConfigChange.AddSeconds(MINIMUM_DELAY_CONFIG_CHANGE) <= DateTime.Now)
            {
                LoggerHelper.Info("Changes to configuration.yaml detected. Reloading.");
                AgentAddonManager addonManager = AgentAddonManager.GetInstance();

                SetupAgent();
                addonManager.Raise_OnConfigReloadHandler(this, e);

                _lastConfigChange = DateTime.Now;
            }
        }

        private void SetupAgent()
        {
            LoggerHelper.Debug("Initializing AgentAddonManager");

            AgentAddonManager addonManager = AgentAddonManager.GetInstance();
            addonManager.LoadAddons();
        }
    }
}
