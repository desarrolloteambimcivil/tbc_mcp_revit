using System.Collections.Generic;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ProductIdentityTests
    {
        private static System.Func<string, string> Env(Dictionary<string, string> values)
            => name => values.TryGetValue(name, out var value) ? value : null;

        [Fact]
        public void Current_name_wins_over_the_legacy_name()
        {
            var lookup = Env(new Dictionary<string, string>
            {
                ["TBC_REVIT_MCP_TOOLSETS"] = "query",
                ["BIMWRIGHT_TOOLSETS"] = "all"
            });

            Assert.Equal("query", ProductIdentity.GetEnv(RvtMcpConfig.EnvToolsets, lookup));
        }

        [Fact]
        public void Legacy_bimwright_name_is_read_when_the_current_name_is_unset()
        {
            var lookup = Env(new Dictionary<string, string> { ["BIMWRIGHT_READ_ONLY"] = "1" });

            Assert.Equal("1", ProductIdentity.GetEnv(RvtMcpConfig.EnvReadOnly, lookup));
        }

        [Fact]
        public void Names_without_the_product_prefix_have_no_fallback()
        {
            var lookup = Env(new Dictionary<string, string> { ["BIMWRIGHT_ANTHROPIC_API_KEY"] = "x" });

            Assert.Null(ProductIdentity.GetEnv("ANTHROPIC_API_KEY", lookup));
        }

        [Fact]
        public void Config_overlay_accepts_legacy_environment_names()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyEnvVars(config, Env(new Dictionary<string, string>
            {
                ["BIMWRIGHT_TARGET"] = "2025",
                ["BIMWRIGHT_TOOLSETS"] = "query,meta"
            }), configFilePath: null, allowPersistenceWriteBack: false);

            Assert.Equal("2025", config.Target);
            Assert.Equal(new List<string> { "query", "meta" }, config.Toolsets);
        }

        [Fact]
        public void Data_root_is_under_the_new_vendor_folder()
        {
            Assert.Equal(Path.Combine("X:", "NourRouas", "tbc-revit-mcp"), ProductIdentity.DataRoot("X:"));
            Assert.Equal(Path.Combine("X:", "Bimwright", "rvt-mcp"), ProductIdentity.LegacyDataRoot("X:"));
        }
    }
}
