using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using IOTLinkAPI.Platform.Windows;
using IOTLinkService.Service.Engine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class AgentManagerTests
    {
        [TestMethod]
        public void HealthyChecksReuseTheHandleAndRediscoverOnExitOrSessionChange()
        {
            using (var original = StartChild())
            using (var replacement = StartChild())
            {
                var session = new WindowsSessionInfo { IsActive = true, SessionID = original.SessionId };
                Process available = original;
                Process discovered = null;
                int scans = 0, sessionQueries = 0, launches = 0;
                var manager = new AgentManager(() =>
                {
                    sessionQueries++;
                    return new List<WindowsSessionInfo> { session };
                }, () =>
                {
                    scans++;
                    discovered = Process.GetProcessById(available.Id);
                    return new[] { discovered };
                }, id => launches++);

                try
                {
                    manager.StartAgents();
                    var originalHandle = discovered.SafeHandle;
                    Parallel.For(0, 180, i => manager.StartAgents());
                    Assert.AreEqual(1, scans);
                    Assert.AreEqual(1, sessionQueries);
                    Assert.IsFalse(originalHandle.IsClosed);

                    original.StandardInput.Close();
                    Assert.IsTrue(original.WaitForExit(5000));
                    available = replacement;
                    manager.StartAgents();
                    Assert.AreEqual(2, scans);
                    Assert.IsTrue(originalHandle.IsClosed);
                    var replacementHandle = discovered.SafeHandle;

                    session.IsActive = false;
                    manager.OnSessionChanged();
                    manager.StartAgents();
                    Assert.IsTrue(replacementHandle.IsClosed);
                    Assert.IsFalse(replacement.HasExited);
                    session.IsActive = true;
                    manager.OnSessionChanged();
                    manager.StartAgents();
                    Assert.AreEqual(3, scans);
                    Assert.AreEqual(0, launches, "An existing agent was launched again");
                    var adoptedHandle = discovered.SafeHandle;
                    manager.StopAgents();
                    Assert.IsTrue(replacement.WaitForExit(5000));
                    Assert.IsTrue(adoptedHandle.IsClosed);
                }
                finally
                {
                    EndChild(original);
                    EndChild(replacement);
                }
            }
        }

        [TestMethod]
        public void MissingAgentIsRetriedAndLaunchedOnceForTheSession()
        {
            using (var current = Process.GetCurrentProcess())
            {
                var session = new WindowsSessionInfo { IsActive = true, SessionID = current.SessionId };
                Process child = null;
                int attempts = 0;
                var manager = new AgentManager(() => new List<WindowsSessionInfo> { session },
                    () => child == null ? Array.Empty<Process>() : new[] { Process.GetProcessById(child.Id) }, id =>
                    {
                        Assert.AreEqual(session.SessionID, id);
                        if (++attempts > 1)
                            child = StartChild();
                    });
                try
                {
                    manager.StartAgents();
                    Assert.IsNull(child);
                    Parallel.For(0, 20, i => manager.StartAgents());
                    Assert.AreEqual(2, attempts);
                    Assert.IsFalse(child.HasExited);
                    manager.StopAgents();
                    Assert.IsTrue(child.WaitForExit(5000));
                }
                finally
                {
                    if (child != null)
                    {
                        EndChild(child);
                        child.Dispose();
                    }
                }
            }
        }

        private static Process StartChild()
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sort.exe"))
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        private static void EndChild(Process child)
        {
            if (!child.HasExited)
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(5000))
                    child.Kill();
            }
        }
    }
}
