using IOTLinkAPI.Addons;
using IOTLinkAPI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace IOTLinkAgent.Agent.Loaders
{
    internal class AssemblyLoader
    {
        // The agent AppDomain keeps every loaded assembly until exit, including superseded builds.
        private static readonly Dictionary<string, Assembly> AssembliesByHash = new Dictionary<string, Assembly>();

        internal static bool LoadAssemblyDLL(ref AddonInfo addonInfo)
        {
            try
            {
                string filename = Path.Combine(addonInfo.AddonPath, addonInfo.AddonFile);

                if (!File.Exists(filename))
                    return false;

                Assembly assembly = GetOrLoadAssembly(File.ReadAllBytes(filename));
                Type scriptType;
                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomain_ReflectionOnlyAssemblyResolve;
                try
                {
                    scriptType = assembly.GetExportedTypes().FirstOrDefault(type => type.IsSubclassOf(typeof(AgentAddon)));
                }
                finally
                {
                    AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= CurrentDomain_ReflectionOnlyAssemblyResolve;
                    AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
                }

                if (scriptType == null)
                    return false;

                LoggerHelper.Debug("Found AgentAddon!");
                addonInfo.AgentAddon = (AgentAddon)Activator.CreateInstance(scriptType);
                return true;
            }
            catch (Exception e)
            {
                LoggerHelper.Debug("Unhandled Exception: {0}", e.ToString());
            }

            return false;
        }

        private static Assembly GetOrLoadAssembly(byte[] bytes)
        {
            string hash;
            using (var sha256 = SHA256.Create())
                hash = Convert.ToBase64String(sha256.ComputeHash(bytes));

            lock (AssembliesByHash)
            {
                if (!AssembliesByHash.TryGetValue(hash, out var assembly))
                {
                    assembly = Assembly.Load(bytes);
                    AssembliesByHash.Add(hash, assembly);
                }
                return assembly;
            }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.Name.Contains(typeof(AgentMain).Assembly.GetName().Name))
            {
                return Assembly.GetExecutingAssembly();
            }
            return null;
        }

        private static Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            Assembly asb = AppDomain.CurrentDomain.GetAssemblies().Where(w => w.FullName == args.Name).FirstOrDefault();
            return asb;
        }
    }
}
