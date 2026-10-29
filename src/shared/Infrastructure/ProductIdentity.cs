using System;
using System.IO;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Product name, author, per-user data folder and environment-variable prefix in one place.
    /// tbc-revit-mcp is a fork of bimwright/rvt-mcp: the legacy folder and BIMWRIGHT_* names are
    /// kept here only so existing installs migrate and existing environment settings keep working.
    /// </summary>
    public static class ProductIdentity
    {
        public const string Name = "tbc-revit-mcp";
        public const string Author = "Nour Rouas";
        public const string AuthorLine = "Nour Rouas — BIM developer/modeller at TeamBIMCivil";
        public const string GitHubRepository = "desarrolloteambimcivil/tbc_mcp_revit";
        public const string RepositoryUrl = "https://github.com/" + GitHubRepository;

        public const string VendorFolder = "NourRouas";
        public const string ProductFolder = "tbc-revit-mcp";
        public const string EnvPrefix = "TBC_REVIT_MCP_";

        public const string LegacyVendorFolder = "Bimwright";
        public const string LegacyProductFolder = "rvt-mcp";
        public const string LegacyEnvPrefix = "BIMWRIGHT_";

        /// <summary>Per-user data root: %LOCALAPPDATA%\NourRouas\tbc-revit-mcp.</summary>
        public static string DataRoot(string localAppData = null)
            => Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                VendorFolder, ProductFolder);

        /// <summary>Data root used by bimwright/rvt-mcp 1.x, the migration source.</summary>
        public static string LegacyDataRoot(string localAppData = null)
            => Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyVendorFolder, LegacyProductFolder);

        /// <summary>
        /// Reads TBC_REVIT_MCP_X; when it is not set, falls back to the legacy BIMWRIGHT_X spelling.
        /// Names without the current prefix are read as given.
        /// </summary>
        public static string GetEnv(string name, Func<string, string> lookup = null)
        {
            lookup = lookup ?? Environment.GetEnvironmentVariable;
            var value = lookup(name);
            if (value != null || name == null || !name.StartsWith(EnvPrefix, StringComparison.Ordinal)) return value;
            return lookup(LegacyEnvPrefix + name.Substring(EnvPrefix.Length));
        }

        /// <summary>Wraps a lookup so every read gets the legacy fallback of <see cref="GetEnv"/>.</summary>
        public static Func<string, string> WithLegacyFallback(Func<string, string> lookup)
        {
            lookup = lookup ?? Environment.GetEnvironmentVariable;
            return name => GetEnv(name, lookup);
        }
    }
}
