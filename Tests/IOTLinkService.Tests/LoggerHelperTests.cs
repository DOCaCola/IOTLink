using System;
using System.IO;
using System.Linq;
using System.Threading;
using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static IOTLinkAPI.Helpers.LoggerHelper;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class LoggerHelperTests
    {
        [TestMethod]
        public void FiltersBeforeCallerLookupAndUpdatesSettingsOnEngineReload()
        {
            // The logger and configuration watchers normally live for the lifetime of the agent or service.
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var domain = AppDomain.CreateDomain("Logger reload test", null, AppDomain.CurrentDomain.SetupInformation);
                try
                {
                    domain.SetData("LogDirectory", directory);
                    domain.DoCallBack(ExerciseLogger);
                }
                finally
                {
                    AppDomain.Unload(domain);
                }

                var lines = File.ReadAllLines(Directory.GetFiles(directory, "*.log").Single());
                CollectionAssert.AreEqual(new[] { "default info", "enabled debug", "enabled trace", "system at level zero" },
                    lines.Select(line => line.Split(new[] { "]: " }, StringSplitOptions.None).Last()).ToArray());
                StringAssert.Contains(lines[2], "[TRACE][" + typeof(LoggerHelperTests).FullName + "]");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void ExerciseLogger()
        {
            string directory = (string)AppDomain.CurrentDomain.GetData("LogDirectory");
            string path = Path.Combine(directory, "configuration.yaml");
            File.WriteAllText(path, "logging: {}\n");
            var logger = new LoggerHelper(path, directory);
            logger.WriteLog(LogLevel.INFO, "default info");

            // A stack walk on every suppressed call allocates far more than this allowance.
            AppDomain.MonitoringIsEnabled = true;
            var domain = AppDomain.CurrentDomain;
            long before = domain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < 2000; i++)
            {
                logger.WriteLog(LogLevel.DEBUG, "default debug");
                logger.WriteLog(LogLevel.TRACE, "default trace");
            }
            Assert.IsTrue(domain.MonitoringTotalAllocatedMemorySize - before < 128 * 1024);

            using (var changed = new AutoResetEvent(false))
            {
                var configs = ConfigurationManager.GetInstance();
                configs.SetReloadHandler(path, (s, e) => changed.Set(), IOTLinkAPI.Platform.ConfigType.CONFIGURATION_ENGINE);
                File.WriteAllText(path, "logging:\n  level: 8\n");
                Assert.IsTrue(changed.WaitOne(TimeSpan.FromSeconds(15)));
                logger.WriteLog(LogLevel.DEBUG, "enabled debug");
                logger.WriteLog(LogLevel.TRACE, "enabled trace");
                logger.WriteLog(LogLevel.TRACE_LOOP, "suppressed trace loop");

                File.WriteAllText(path, "logging:\n  level: 0\n");
                Assert.IsTrue(changed.WaitOne(TimeSpan.FromSeconds(15)));
                logger.WriteLog(LogLevel.INFO, "suppressed info");
                logger.WriteLog(LogLevel.SYSTEM, "system at level zero");

                File.WriteAllText(path, "logging:\n  enabled: false\n  level: 10\n");
                Assert.IsTrue(changed.WaitOne(TimeSpan.FromSeconds(15)));
                logger.WriteLog(LogLevel.SYSTEM, "disabled system");
            }

            logger.Flush();
        }
    }
}
