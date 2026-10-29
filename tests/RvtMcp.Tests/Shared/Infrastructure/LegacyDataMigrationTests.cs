using System;
using System.IO;
using Xunit;
using RvtMcp.Plugin;

public class LegacyDataMigrationTests
{
    [Fact]
    public void MigrateOnce_FamilyRootWithSiblingDoesNotCreateProductRoot()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            var sibling = Path.Combine(tempLocal, "Bimwright", "ipt-mcp", "baked");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "tool.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);

            Assert.False(Directory.Exists(Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(sibling, "tool.json")));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_CopiesBakedFolderAndCreatesMarker()
    {
        // Arrange: redirect LOCALAPPDATA to temp using parameterized helper.
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            var legacyBaked = Path.Combine(tempLocal, "Bimwright", "baked");
            Directory.CreateDirectory(legacyBaked);
            File.WriteAllText(Path.Combine(legacyBaked, "tool1.json"), "{}");

            // Act
            LegacyDataMigration.MigrateOnce(tempLocal);

            // Assert
            var newBaked = Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp", "baked", "tool1.json");
            Assert.True(File.Exists(newBaked));
            var marker = Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp", ".migrated-from-bimwright-v2");
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_IsIdempotent()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            Directory.CreateDirectory(Path.Combine(tempLocal, "Bimwright", "baked"));
            File.WriteAllText(Path.Combine(tempLocal, "Bimwright", "baked", "tool1.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);
            var firstMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp", "baked", "tool1.json"));

            LegacyDataMigration.MigrateOnce(tempLocal);   // second call — should be no-op
            var secondMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp", "baked", "tool1.json"));

            Assert.Equal(firstMtime, secondMtime);
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_CopiesUpstreamRootWithoutLogsUpdateStateOrServer()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        var upstream = Path.Combine(tempLocal, "Bimwright", "rvt-mcp");
        var current = Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp");
        Directory.CreateDirectory(Path.Combine(upstream, "failures", "pending"));
        Directory.CreateDirectory(Path.Combine(upstream, "server", "current"));
        try
        {
            File.WriteAllText(Path.Combine(upstream, "rvtmcp.config.json"), "{\"toolsets\":[\"all\"]}");
            File.WriteAllText(Path.Combine(upstream, "failures", "pending", "capture.json"), "{}");
            File.WriteAllText(Path.Combine(upstream, "rvt-mcp.log"), "log");
            File.WriteAllText(Path.Combine(upstream, "update-check.json"), "{}");
            File.WriteAllText(Path.Combine(upstream, "server", "current", "rvt-mcp.exe"), "exe");

            LegacyDataMigration.MigrateOnce(tempLocal);

            Assert.Equal("{\"toolsets\":[\"all\"]}", File.ReadAllText(Path.Combine(current, "rvtmcp.config.json")));
            Assert.True(File.Exists(Path.Combine(current, "failures", "pending", "capture.json")));
            Assert.True(File.Exists(Path.Combine(current, ".migrated-from-bimwright-rvt-mcp")));
            Assert.False(File.Exists(Path.Combine(current, "rvt-mcp.log")));
            Assert.False(File.Exists(Path.Combine(current, "update-check.json")));
            Assert.False(Directory.Exists(Path.Combine(current, "server")));
            Assert.True(File.Exists(Path.Combine(upstream, "rvtmcp.config.json")));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_PrefersUpstreamRootOverOlderRecoveryFolders()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        var upstream = Path.Combine(tempLocal, "Bimwright", "rvt-mcp");
        var older = Path.Combine(tempLocal, "RvtMcp");
        Directory.CreateDirectory(upstream);
        Directory.CreateDirectory(older);
        try
        {
            // The older folder is recovery data that upstream already migrated and later diverged from.
            File.WriteAllText(Path.Combine(upstream, "rvtmcp.config.json"), "{\"toolsets\":[\"all\"]}");
            File.WriteAllText(Path.Combine(older, "rvtmcp.config.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);

            var current = Path.Combine(tempLocal, "NourRouas", "tbc-revit-mcp");
            Assert.Equal("{\"toolsets\":[\"all\"]}", File.ReadAllText(Path.Combine(current, "rvtmcp.config.json")));
            Assert.False(File.Exists(Path.Combine(current, ".migrated-from-rvtmcp-v2")));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }
}
