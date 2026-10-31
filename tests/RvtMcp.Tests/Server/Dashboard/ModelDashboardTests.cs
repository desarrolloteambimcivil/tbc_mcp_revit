using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ModelDashboardTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tbc-dashboard-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        // A snapshot as the add-in returns it (made-up figures, no project data).
        private static JObject AddinSnapshot(string title, double length, int openEnds) => ModelSnapshotBuilder.Build(
            new JObject { ["title"] = title, ["path_hash"] = "0123456789abcdef", ["revit_year"] = 2024 },
            new Dictionary<string, JObject>
            {
                ["networks"] = ModelSnapshotBuilder.Networks("Comments",
                    new[] { new SnapshotNetworkCurve { Id = 1, Network = "Red A", Kind = "pipe", DiameterMm = 160, LengthM = length } },
                    new SnapshotNetworkItem[0], Enumerable.Range(1, openEnds).Select(i => ((long)i, "Red A")), null, 50),
                ["health"] = ModelSnapshotBuilder.Health(new SnapshotHealthCounts(), new[] { ("w", "W") }),
            });

        private SnapshotStore Store() => new SnapshotStore(Path.Combine(_root, "snapshots"));

        [Fact]
        public void Template_resource_has_the_data_and_logo_placeholders()
        {
            var template = DashboardBuilder.Template();
            Assert.Contains("__TBC_DATA__", template);
            Assert.Contains("__TBC_LOGO__", template);
            Assert.DoesNotContain("cdn", template, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script src", template, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Data_cannot_close_the_script_element()
        {
            var html = DashboardBuilder.Fill("<script type=\"application/json\">__TBC_DATA__</script>__TBC_LOGO__",
                new JObject { ["title"] = "x</script><script>alert(1)</script><!--" }, "data:logo");

            Assert.Equal(1, html.Split("</script>").Length - 1);
            Assert.DoesNotContain("<!--", html);
            Assert.EndsWith("data:logo", html);
            var json = html.Substring(html.IndexOf('>') + 1, html.IndexOf("</script>", StringComparison.Ordinal) - html.IndexOf('>') - 1);
            Assert.Equal("x</script><script>alert(1)</script><!--", JObject.Parse(json).Value<string>("title"));
        }

        [Fact]
        public void Client_view_keeps_client_sections_and_drops_ids()
        {
            var snapshot = AddinSnapshot("M", 10, 3);
            var sections = (JObject)snapshot["sections"].DeepClone();
            DashboardBuilder.ForClient(sections);

            Assert.Null(sections["health"]);
            Assert.NotNull(sections["networks"]);
            Assert.DoesNotContain(sections.Descendants().OfType<JProperty>(), p => p.Name.Contains("sample"));
            Assert.Equal(3, sections["networks"]["items"][0]["open_ends"].Value<int>("count"));
        }

        [Fact]
        public void Writes_a_dashboard_with_the_history_and_filters_by_date()
        {
            var store = Store();
            store.Save(AddinSnapshot("Modelo", 10, 4), new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
            store.Save(AddinSnapshot("Modelo", 25, 1), new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc));
            var key = store.ResolveModel("", out _);
            var folder = Path.Combine(_root, "dashboards");

            var all = DashboardBuilder.Write(store, key, "internal", "", "", "Revisar Red A", folder, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(2, all.Value<int>("snapshots_used"));
            var html = File.ReadAllText(all.Value<string>("saved"));
            Assert.DoesNotContain("__TBC_DATA__", html);
            Assert.Contains("Revisar Red A", html);
            Assert.Contains("data:image/png;base64,", html);

            var client = DashboardBuilder.Write(store, key, "client", "2026-11-02", "", "", folder, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(1, client.Value<int>("snapshots_used"));
            Assert.Contains("-client-", client.Value<string>("saved"));
            Assert.DoesNotContain("0123456789abcdef", File.ReadAllText(client.Value<string>("saved")));

            Assert.Throws<InvalidOperationException>(() => DashboardBuilder.Write(store, key, "internal", "2027-01-01", "", "", folder, DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => DashboardBuilder.Write(store, key, "everyone", "", "", "", folder, DateTime.UtcNow));
        }

        [Fact]
        public async Task Snapshot_tool_reads_revit_saves_and_reports_the_change()
        {
            ModelDashboardTools.DataRootOverride = _root;
            try
            {
                var sends = new List<(string Command, JObject Parameters, int? Timeout)>();
                var length = 10.0;
                string first = null, second = null;
                await Capture.WithSendOverride((command, parameters, timeout) =>
                {
                    sends.Add((command, JObject.FromObject(parameters), timeout));
                    var data = AddinSnapshot("Modelo", length, 2);
                    data["_target"] = new JObject { ["pid"] = 1 };
                    return Task.FromResult(data);
                }, async () =>
                {
                    first = await ModelDashboardTools.CreateModelSnapshot(label: "inicio", networkParameter: "Comments", clearanceMm: 50);
                    length = 12.5;
                    second = await ModelDashboardTools.CreateModelSnapshot();
                });

                Assert.Equal("get_model_snapshot", sends[0].Command);
                Assert.Equal(50, sends[0].Parameters.Value<double>("clearance_mm"));
                Assert.Equal(180, sends[0].Timeout);
                var a = JObject.Parse(first);
                Assert.Equal("inicio", a.Value<string>("label"));
                Assert.Equal(1, a["_target"].Value<int>("pid"));
                Assert.Empty((JObject)a["change_since_previous"]);
                Assert.True(File.Exists(a.Value<string>("saved")));
                Assert.Null(JObject.Parse(File.ReadAllText(a.Value<string>("saved")))["_target"]);

                var b = JObject.Parse(second);
                Assert.Equal(2, b.Value<int>("snapshots_of_model"));
                Assert.Equal(2.5, b["change_since_previous"].Value<double>("network_length_m"));

                var list = JObject.Parse(ModelDashboardTools.ListModelSnapshots("Modelo"));
                Assert.Equal(2, list["snapshots"].Count());
                var dashboard = JObject.Parse(ModelDashboardTools.CreateModelDashboard());
                Assert.True(File.Exists(dashboard.Value<string>("saved")));
            }
            finally { ModelDashboardTools.DataRootOverride = null; }
        }

        [Fact]
        public async Task Snapshot_tool_saves_nothing_when_revit_reports_a_failure()
        {
            ModelDashboardTools.DataRootOverride = _root;
            try
            {
                string reply = null;
                await Capture.WithSendOverride((c, p, t) => Task.FromResult(new JObject { ["success"] = false, ["error"] = "No document is open." }),
                    async () => reply = await ModelDashboardTools.CreateModelSnapshot());

                Assert.Contains("No document is open.", reply);
                Assert.False(Directory.Exists(Path.Combine(_root, "snapshots")));
                Assert.StartsWith("Error:", ModelDashboardTools.CreateModelDashboard());
            }
            finally { ModelDashboardTools.DataRootOverride = null; }
        }
    }
}
