using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class ModelSnapshotBuilderTests
    {
        private static SnapshotNetworkCurve Pipe(long id, string network, double dn, double m) => new SnapshotNetworkCurve { Id = id, Network = network, Kind = "pipe", DiameterMm = dn, LengthM = m };

        private static PipeClash Clash(long a, string na, long b, string nb, string kind = "crossing")
            => new PipeClash { A = new RoundCurve { Id = a, Comment = na }, B = new RoundCurve { Id = b, Comment = nb }, Kind = kind };

        private static JObject Networks(IEnumerable<SnapshotNetworkCurve> curves, IEnumerable<SnapshotNetworkItem> items = null,
            IEnumerable<(long, string)> open = null, IEnumerable<PipeClash> clashes = null, int sample = 50)
            => ModelSnapshotBuilder.Networks("Comments", curves, items ?? new SnapshotNetworkItem[0], open ?? new (long, string)[0], clashes, sample);

        private static JObject Item(JObject networks, string name) => (JObject)networks["items"].Single(n => n.Value<string>("name") == name);

        [Fact]
        public void Networks_sum_lengths_by_kind_and_nominal_diameter()
        {
            var n = Networks(new[] { Pipe(1, "Pluviales", 160, 10.25), Pipe(2, "Pluviales", 160.2, 4.5), Pipe(3, "Pluviales", 315, 20),
                new SnapshotNetworkCurve { Id = 4, Network = "Electricidad", Kind = "conduit", DiameterMm = 90, LengthM = 7 } });

            var pluv = Item(n, "Pluviales");
            Assert.Equal(3, pluv.Value<int>("pipes"));
            Assert.Equal(34.75, pluv.Value<double>("length_m"), 3);
            var dn160 = pluv["by_diameter"].Single(d => d.Value<int>("dn_mm") == 160);
            Assert.Equal(2, dn160.Value<int>("count"));
            Assert.Equal(14.75, dn160.Value<double>("length_m"), 3);
            Assert.Equal(1, Item(n, "Electricidad").Value<int>("conduits"));
            Assert.Equal(41.75, n.Value<double>("length_m"), 3);
            Assert.Equal(2, n.Value<int>("networks_total"));
        }

        [Fact]
        public void Elements_without_a_network_value_form_one_unassigned_group_listed_last()
        {
            var n = Networks(new[] { Pipe(1, null, 110, 50), Pipe(2, "  ", 110, 5), Pipe(3, "Saneamiento", 200, 1) });

            Assert.Equal("Saneamiento", n["items"][0].Value<string>("name"));
            var none = (JObject)n["items"][1];
            Assert.True(none.Value<bool>("unassigned"));
            Assert.Equal("", none.Value<string>("name"));
            Assert.Equal(2, none.Value<int>("pipes"));
            Assert.Equal(1, n.Value<int>("networks_total"));
        }

        [Fact]
        public void Clashes_are_counted_per_unordered_pair_and_unjoined_joints_apart()
        {
            var clashes = new[]
            {
                Clash(1, "Pluviales", 9, "Electricidad"), Clash(9, "Electricidad", 2, "Pluviales"),
                Clash(3, "Pluviales", 4, "Pluviales"), Clash(5, "Pluviales", 6, "Pluviales", "junction_without_fitting"),
            };
            var n = Networks(new[] { Pipe(1, "Pluviales", 160, 1), Pipe(9, "Electricidad", 90, 1) }, clashes: clashes);

            var pairs = n["clash_pairs"].ToList();
            Assert.Equal(2, pairs.Count);
            var cross = pairs.Single(p => p.Value<string>("a") != p.Value<string>("b"));
            Assert.Equal(new[] { "Electricidad", "Pluviales" }, new[] { cross.Value<string>("a"), cross.Value<string>("b") });
            Assert.Equal(2, cross.Value<int>("count"));
            Assert.Equal(3, n.Value<int>("clashes_total"));
            Assert.Equal(3, Item(n, "Pluviales").Value<int>("clashes_total"));
            Assert.Equal(2, Item(n, "Electricidad").Value<int>("clashes_total"));
            Assert.Equal(1, Item(n, "Pluviales")["unjoined_joints"].Value<int>("count"));
        }

        [Fact]
        public void Items_open_ends_and_samples_are_bounded()
        {
            var items = new[]
            {
                new SnapshotNetworkItem { Id = 10, Network = "Pluviales", Kind = "fitting" },
                new SnapshotNetworkItem { Id = 11, Network = "Pluviales", Kind = "structure" },
                new SnapshotNetworkItem { Id = 12, Network = "Pluviales", Kind = "structure" },
            };
            // Element 1 has both ends open: 8 ends on 7 elements.
            var open = Enumerable.Range(1, 7).Select(i => ((long)i, "Pluviales")).Concat(new[] { (1L, "Pluviales") });
            var n = Networks(new[] { Pipe(1, "Pluviales", 160, 1) }, items, open, sample: 3);

            var pluv = Item(n, "Pluviales");
            Assert.Equal(1, pluv.Value<int>("fittings"));
            Assert.Equal(2, pluv.Value<int>("structures"));
            Assert.Equal(8, pluv["open_ends"].Value<int>("count"));
            Assert.Equal(7, pluv["open_ends"].Value<int>("elements"));
            Assert.Equal(3, pluv["open_ends"]["sample_ids"].Count());
        }

        [Fact]
        public void Composition_counts_largest_first_and_worksets_only_when_workshared()
        {
            var elements = new[] { ("Tuberías", "Nivel 0", "WS1"), ("Tuberías", "Nivel 0", "WS1"), ("Uniones", "Nivel 1", "WS2") };
            var c = ModelSnapshotBuilder.Composition(elements, workshared: false);

            Assert.Equal(3, c.Value<int>("elements_total"));
            Assert.Equal("Tuberías", c["by_category"][0].Value<string>("name"));
            Assert.Equal(2, c["by_category"][0].Value<int>("count"));
            Assert.Null(c["by_workset"]);
            Assert.NotNull(ModelSnapshotBuilder.Composition(elements, workshared: true)["by_workset"]);
        }

        [Fact]
        public void Health_groups_warnings_by_description()
        {
            // One failure type with system-specific texts counts as one type, labelled by its first text.
            var h = ModelSnapshotBuilder.Health(new SnapshotHealthCounts { InPlaceFamilies = 2 },
                new[] { ("flow", "Flow direction does not match"), ("flow", "System 11: flow direction"), ("mark", "Duplicate mark") });

            Assert.Equal(3, h.Value<int>("warnings_total"));
            Assert.Equal(2, h.Value<int>("warning_types"));
            Assert.Equal("Flow direction does not match", h["warnings_by_type"][0].Value<string>("description"));
            Assert.Equal(2, h["warnings_by_type"][0].Value<int>("count"));
            Assert.Equal(2, h.Value<int>("in_place_families"));
            Assert.Equal(JTokenType.Null, h["file_size_bytes"].Type);
        }

        [Fact]
        public void Data_quality_reports_percentage_and_missing_sample()
        {
            var q = ModelSnapshotBuilder.DataQuality(new[]
            {
                ("Comments", "MEP", (IEnumerable<(long, bool)>)new[] { (1L, true), (2L, false), (3L, true), (4L, true) }),
                ("Mark", "none", Enumerable.Empty<(long, bool)>()),
            }, 50);

            var comments = q["checks"][0];
            Assert.Equal(75.0, comments.Value<double>("pct"));
            Assert.Equal(new long[] { 2 }, comments["missing_sample_ids"].Values<long>().ToArray());
            Assert.Equal(JTokenType.Null, q["checks"][1]["pct"].Type);
        }

        [Fact]
        public void Snapshot_carries_schema_and_an_audience_on_every_section()
        {
            var s = ModelSnapshotBuilder.Build(new JObject { ["title"] = "M" },
                new Dictionary<string, JObject> { ["networks"] = new JObject(), ["health"] = new JObject(), ["other"] = new JObject() },
                new[] { "composition" });

            Assert.Equal(ModelSnapshotSchema.Schema, s.Value<string>("schema"));
            Assert.Equal(1, s.Value<int>("schema_version"));
            Assert.Equal("client", s["sections"]["networks"].Value<string>("audience"));
            Assert.Equal("internal", s["sections"]["health"].Value<string>("audience"));
            Assert.Equal("internal", s["sections"]["other"].Value<string>("audience"));
            Assert.Equal("composition", s["truncated"]["sections"][0].Value<string>());
        }

        [Fact]
        public void Key_figures_and_change_against_the_previous_snapshot()
        {
            var sections = new Dictionary<string, JObject>
            {
                ["networks"] = Networks(new[] { Pipe(1, "Pluviales", 160, 12) }, open: new[] { (1L, "Pluviales") }),
                ["data_quality"] = ModelSnapshotBuilder.DataQuality(new[] { ("Comments", "MEP", (IEnumerable<(long, bool)>)new[] { (1L, true) }) }, 5),
            };
            var now = ModelSnapshotSchema.KeyFigures(ModelSnapshotBuilder.Build(new JObject(), sections));

            Assert.Equal(12, now.Value<double>("network_length_m"));
            Assert.Equal(1, now.Value<int>("open_ends"));
            Assert.Equal(100.0, now.Value<double>("Comments_filled_pct"));

            var before = new JObject { ["network_length_m"] = 10.5, ["open_ends"] = 4 };
            var change = ModelSnapshotSchema.Change(now, before);
            Assert.Equal(1.5, change.Value<double>("network_length_m"));
            Assert.Equal(-3, change.Value<double>("open_ends"));
            Assert.Null(change["Comments_filled_pct"]);
            Assert.Empty(ModelSnapshotSchema.Change(now, null));
        }
    }
}
