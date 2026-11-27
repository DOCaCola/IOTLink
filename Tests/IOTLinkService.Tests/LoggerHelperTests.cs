using System;
using System.IO;
using System.Diagnostics;
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
                var executable = Path.ChangeExtension(typeof(LoggerHelperTests).Assembly.Location, ".exe");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
                start.ArgumentList.Add(directory);
                using var child = Process.Start(start);
                string error = child.StandardError.ReadToEnd();
                child.WaitForExit();
                Assert.AreEqual(0, child.ExitCode, error);

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

        // The child process owns the same process-lifetime log file and watchers as a real host.
        public static int Main(string[] args)
        {
            try
            {
                ExerciseLogger(args.Single());
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void ExerciseLogger(string directory)
        {
            string path = Path.Combine(directory, "configuration.yaml");
            File.WriteAllText(path, "logging: {}\n");
            var logger = new LoggerHelper(path, directory);
            logger.WriteLog(LogLevel.INFO, "default info");

            // A stack walk on every suppressed call allocates far more than this allowance.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                logger.WriteLog(LogLevel.DEBUG, "default debug");
                logger.WriteLog(LogLevel.TRACE, "default trace");
            }
            Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024);

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
