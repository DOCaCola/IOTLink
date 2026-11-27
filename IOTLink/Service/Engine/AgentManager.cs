using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform;
using IOTLinkAPI.Platform.Windows;
using IOTLinkService.Service.WebSockets.Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace IOTLinkService.Service.Engine
{
    public class AgentManager
    {
        private static readonly AgentManager Instance = new AgentManager(
            WindowsAPI.GetWindowsSessions, () => Process.GetProcessesByName(PathHelper.APP_AGENT_NAME), LaunchAgent);

        private readonly object _sync = new object();
        private readonly Dictionary<int, Process> _agents = new Dictionary<int, Process>();
        private readonly Func<List<WindowsSessionInfo>> _getSessions;
        private readonly Func<Process[]> _findAgents;
        private readonly Action<int> _launchAgent;
        private bool _rediscover = true;

        public static AgentManager GetInstance() => Instance;

        internal AgentManager(Func<List<WindowsSessionInfo>> getSessions, Func<Process[]> findAgents, Action<int> launchAgent)
        {
            _getSessions = getSessions;
            _findAgents = findAgents;
            _launchAgent = launchAgent;
        }

        public List<AgentInfo> GetAgents()
        {
            lock (_sync)
            {
                var processes = _findAgents();
                try
                {
                    return processes.Select(p => new AgentInfo
                    {
                        SessionId = p.SessionId,
                        ProcessId = p.Id,
                        CommandLine = p.StartInfo.Arguments,
                        Username = PlatformHelper.GetUsername(p.SessionId)
                    }).ToList();
                }
                finally
                {
                    foreach (var process in processes)
                        process.Dispose();
                }
            }
        }

        internal void OnSessionChanged()
        {
            lock (_sync)
                _rediscover = true;
        }

        public void StartAgents()
        {
            lock (_sync)
            {
                try
                {
                    if (!_rediscover && !_agents.Values.Any(p => p.HasExited))
                        return;

                    var sessions = new HashSet<int>(_getSessions().Where(s => s.IsActive).Select(s => s.SessionID));
                    foreach (int id in _agents.Keys.ToArray())
                    {
                        if (!sessions.Contains(id) || _agents[id].HasExited)
                        {
                            _agents[id].Dispose();
                            _agents.Remove(id);
                        }
                    }
                    EnsureAgents(sessions);
                    // WTS can briefly report no active sessions during a logon or resume.
                    _rediscover = sessions.Count == 0 || _agents.Count != sessions.Count;
                }
                catch (Exception ex)
                {
                    _rediscover = true;
                    LoggerHelper.Error("Could not check session agents: {0}", ex);
                }
            }
        }

        public void StartAgent(int sessionId, string username = null)
        {
            lock (_sync)
            {
                if (!_getSessions().Any(s => s.SessionID == sessionId && s.IsActive &&
                    (string.IsNullOrWhiteSpace(username) || s.IsUser(username))))
                    return;

                if (_agents.TryGetValue(sessionId, out var process))
                {
                    if (!process.HasExited)
                        return;

                    process.Dispose();
                    _agents.Remove(sessionId);
                }
                _rediscover = true;
                EnsureAgents(new HashSet<int> { sessionId });
            }
        }

        private void EnsureAgents(HashSet<int> sessions)
        {
            if (sessions.Count == 0)
                return;

            AdoptAgents(sessions);
            var missing = sessions.Where(id => !_agents.ContainsKey(id)).ToArray();
            foreach (int id in missing)
                _launchAgent(id);

            if (missing.Length > 0)
                AdoptAgents(sessions);
        }

        private void AdoptAgents(HashSet<int> sessions)
        {
            var processes = _findAgents();
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    var process = processes[i];
                    int sessionId = process.SessionId;
                    if (!sessions.Contains(sessionId) || _agents.ContainsKey(sessionId))
                        continue;

                    try
                    {
                        // Opening the handle binds future checks to this process, even if its PID is reused.
                        _ = process.SafeHandle;
                        if (!process.HasExited)
                        {
                            _agents.Add(sessionId, process);
                            processes[i] = null; // Ownership moved to the session cache.
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process?.Dispose();
            }
        }

        private static void LaunchAgent(int sessionId)
        {
            PlatformHelper.Run(new RunInfo
            {
                Application = Path.Combine(PathHelper.BaseAppPath(), PathHelper.APP_AGENT_NAME + ".exe"),
                CommandLine = "--agent " + WebSocketServerManager.WEBSOCKET_URI,
                WorkingDir = PathHelper.BaseAppPath(),
                SessionId = sessionId
            });
        }

        public void StopAgents()
        {
            lock (_sync)
            {
                try
                {
                    // Include existing agents in disconnected sessions and any duplicates.
                    foreach (var process in _findAgents())
                    {
                        using (process)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch (Exception ex)
                            {
                                LoggerHelper.Error("Failed to stop agent {0}: {1}", process.Id, ex.Message);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var process in _agents.Values)
                        process.Dispose();
                    _agents.Clear();
                    _rediscover = true;
                }
            }
        }
    }
}
