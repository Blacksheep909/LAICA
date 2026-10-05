using System;
using System.IO;

namespace Laica
{
    /// <summary>Shared location for LAICA workspace data used by the GUI and native bridge.</summary>
    public static class DataPaths
    {
        public static string DataDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("LAICA_HOME");
            if (!String.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LAICA", "workspace"));
        }
    }
}
