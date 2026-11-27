using IOTLinkAPI.Configs;
using System.IO;
using static IOTLinkAPI.Configs.ConfigurationManager;

namespace IOTLinkAPI.Helpers
{
    public static class ApplicationConfigHelper
    {
        internal static readonly string EngineConfigPath = Path.Combine(PathHelper.ConfigPath(), "configuration.yaml");

        public static void Init()
        {
            Configuration configuration = GetEngineConfig();
            if (configuration == null)
                LoggerHelper.Error("Cannot initialize application engine configuration.");
        }

        public static Configuration GetEngineConfig()
        {
            return ConfigurationManager.GetInstance().GetConfiguration(EngineConfigPath);
        }

        public static void SetEngineConfigReloadHandler(ConfigReloadedHandler configReloadedHandler)
        {
            Configuration configuration = GetEngineConfig();
            if (configuration == null)
                return;

            ConfigurationManager.GetInstance().SetReloadHandler(EngineConfigPath, configReloadedHandler, Platform.ConfigType.CONFIGURATION_ENGINE);
        }
    }
}
