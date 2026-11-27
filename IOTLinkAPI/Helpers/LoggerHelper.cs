using IOTLinkAPI.Configs;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Events;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Timers;

namespace IOTLinkAPI.Helpers
{
#pragma warning disable 1591
    public class LoggerHelper
    {
        private static readonly Lazy<LoggerHelper> Instance = new Lazy<LoggerHelper>(() =>
            new LoggerHelper(ApplicationConfigHelper.EngineConfigPath, PathHelper.LogsPath()));
        private readonly string _configPath;
        private readonly string _logsPath;
        private readonly object _settingsLock = new object();
        private volatile LogSettings _settings;
        private bool _loadingSettings;

        private sealed class LogSettings
        {
            internal readonly bool Enabled;
            internal readonly LogLevel Level;

            internal LogSettings(Configuration config)
            {
                if (config != null)
                {
                    Enabled = config.GetValue("logging:enabled", true);
                    Level = (LogLevel)config.GetValue("logging:level", (int)LogLevel.INFO);
                }
            }
        }

        private StreamWriter _logWriter;
        private Timer _flushTimer;

        private DateTime _lastMessage = new DateTime(0L);
        private DateTime _lastFlush = new DateTime(0L);

        private readonly object writeLock = new object();

        private static readonly int FLUSH_MAX_INTERVAL = 60;
        private static readonly int FLUSH_MIN_INTERVAL = 1;

        public enum LogLevel
        {
            DISABLED,
            CRITICAL,
            ERROR,
            WARNING,
            INFO,
            VERBOSE,
            DEBUG,
            DEBUG_LOOP,
            TRACE,
            TRACE_LOOP,
            DATA_DUMP,
            SYSTEM
        }

        internal LoggerHelper(string configPath, string logsPath)
        {
            _configPath = configPath;
            _logsPath = logsPath;
            OpenLogFile();

            _flushTimer = new Timer();
            _flushTimer.Interval = 1000;
            _flushTimer.Elapsed += OnFlushInterval;
        }

        ~LoggerHelper()
        {
            CloseLogFile();
        }

        public void Flush()
        {
            lock (writeLock)
            {
                try
                {
                    if (_logWriter != null)
                    {
                        _logWriter.Flush();
                        _lastFlush = DateTime.UtcNow;
                    }
                }
                catch (Exception) { }
            }
        }

        private void WriteFile(string message = null)
        {
            lock (writeLock)
            {
                try
                {
                    if (_logWriter != null)
                    {
                        _logWriter.WriteLine(message);
                        _lastMessage = DateTime.Now;
                    }
                }
                catch (Exception) { }
            }
        }

        private void OpenLogFile()
        {
            try
            {
                if (!Directory.Exists(_logsPath))
                    Directory.CreateDirectory(_logsPath);

                string prefix = Environment.UserInteractive ? "AgentLog" : "ServiceLog";
                string date = DateTime.Now.ToString("yyyy_MM_dd");
                string filename = string.Format("{0}_{1}.log", prefix, date);
                string path = Path.Combine(_logsPath, filename);

                if (!File.Exists(path))
                    _logWriter = File.CreateText(path);
                else
                    _logWriter = File.AppendText(path);
            }
            catch (Exception) { }
        }

        private void CloseLogFile()
        {
            lock (writeLock)
            {
                try
                {
                    if (_logWriter != null)
                    {
                        _flushTimer.Stop();
                        _logWriter.Flush();
                        _logWriter.Close();
                        _logWriter = null;
                    }
                }
                catch (Exception) { }
            }
        }

        private void OnFlushInterval(object sender, ElapsedEventArgs e)
        {
            if (_lastFlush.AddSeconds(FLUSH_MAX_INTERVAL) < DateTime.UtcNow || _lastMessage.AddSeconds(FLUSH_MIN_INTERVAL) < DateTime.Now)
            {
                _flushTimer.Stop();
                Flush();
                _flushTimer.Start();
            }
        }

        private LogSettings LoadSettings()
        {
            lock (_settingsLock)
            {
                // The configuration reader and the watcher setup both use this logger.
                if (_settings != null || _loadingSettings)
                    return _settings;

                _loadingSettings = true;
                try
                {
                    var manager = ConfigurationManager.GetInstance();
                    var config = manager.GetConfiguration(_configPath);
                    if (config != null)
                    {
                        manager.SetReloadHandler(_configPath, OnSettingsReloaded, ConfigType.CONFIGURATION_ENGINE);
                        // A watcher may have reloaded the file between our first read and registration.
                        _settings = new LogSettings(manager.GetConfiguration(_configPath));
                    }
                    return _settings;
                }
                finally
                {
                    _loadingSettings = false;
                }
            }
        }

        private void OnSettingsReloaded(object sender, ConfigReloadEventArgs e)
        {
            lock (_settingsLock)
            {
                _settings = new LogSettings(ConfigurationManager.GetInstance().GetConfiguration(_configPath));
            }
        }

        internal void WriteLog(LogLevel logLevel, string message, params object[] args)
        {
            var settings = _settings ?? LoadSettings();
            if (settings == null || !settings.Enabled || (logLevel > settings.Level && logLevel != LogLevel.SYSTEM))
                return;

            if (string.IsNullOrWhiteSpace(message))
                return;

            string messageTag = GetCallerInformation();
            if (string.IsNullOrWhiteSpace(messageTag))
                return;

            string formatedMessage;
            if (args == null || args.Length == 0)
                formatedMessage = message;
            else
                formatedMessage = string.Format(message, args);

            string datetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            string machineName = PlatformHelper.GetFullMachineName();
            string finalMessage = string.Format("[{0}][{1}][{2}][{3}]: {4}", machineName, datetime, logLevel.ToString(), messageTag, formatedMessage);

            if (_lastMessage != null && _lastMessage.DayOfYear != DateTime.Now.DayOfYear)
            {
                CloseLogFile();
                OpenLogFile();
            }

            WriteFile(finalMessage);
        }

        public static void Critical(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.CRITICAL, message, args);
        }

        public static void Error(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.ERROR, message, args);
        }

        public static void Warn(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.WARNING, message, args);
        }

        public static void Info(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.INFO, message, args);
        }

        public static void Verbose(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.VERBOSE, message, args);
        }

        public static void Debug(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.DEBUG, message, args);
        }

        public static void Trace(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.TRACE, message, args);
        }

        public static void TraceLoop(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.TRACE_LOOP, message, args);
        }

        public static void DataDump(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.DATA_DUMP, message, args);
        }

        public static void System(string message, params object[] args)
        {
            GetInstance().WriteLog(LogLevel.SYSTEM, message, args);
        }

        public static void EmptyLine()
        {
            GetInstance().WriteFile();
        }

        private static string GetCallerInformation()
        {
            try
            {
                string fullName;
                Type declaringType;
                int skipFrames = 1;
                do
                {
                    StackFrame stackFrame = new StackFrame(skipFrames, false);
                    MethodBase method = stackFrame?.GetMethod();
                    declaringType = method?.DeclaringType;
                    if (declaringType == null)
                    {
                        return method?.Name;
                    }
                    skipFrames++;
                    fullName = declaringType?.FullName;
                }
                while (declaringType == typeof(LoggerHelper) || declaringType.Module.Name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase));

                return Regex.Replace(fullName, "(.*)\\+([A-Za-z0-9_<>]+)(.*)", "$1$3");
            }
            catch (Exception)
            {
                return "GetCallerInformationError";
            }
        }

        public static LoggerHelper GetInstance()
        {
            return Instance.Value;
        }
    }
}
