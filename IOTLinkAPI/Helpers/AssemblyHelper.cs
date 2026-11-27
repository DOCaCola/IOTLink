using System;
using System.Reflection;

namespace IOTLinkAPI.Helpers
{
    public class AssemblyHelper
    {

        public static string GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version.ToString();
        }

        public static bool CheckAssemblyVersion(string MinVersion, string MaxVersion)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            int[] current = { version.Major, version.Minor, version.Build, version.Revision };
            int[] minimum = (int[])current.Clone();
            int[] maximum = (int[])current.Clone();
            StringHelper.ParseVersion(ref minimum, MinVersion);
            StringHelper.ParseVersion(ref maximum, MaxVersion);

            return current.AsSpan().SequenceCompareTo(minimum) >= 0 &&
                   current.AsSpan().SequenceCompareTo(maximum) <= 0;
        }
    }
}
