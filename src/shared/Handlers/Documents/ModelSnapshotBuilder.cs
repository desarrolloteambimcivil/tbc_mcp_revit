using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>A pipe or conduit of a network: nominal diameter in mm and length in m.</summary>
    internal sealed class SnapshotNetworkCurve
    {
        public long Id;
        public string Network;
        public string Kind;          // pipe | conduit
        public double DiameterMm;
        public double LengthM;
    }

    /// <summary>A fitting, accessory or structure that belongs to a network.</summary>
    internal sealed class SnapshotNetworkItem
    {
        public long Id;
        public string Network;
        public string Kind;          // fitting | accessory | structure
    }

    /// <summary>Counts for the model health section; the handler fills them from Revit.</summary>
    internal sealed class SnapshotHealthCounts
    {
        public int FamiliesTotal, InPlaceFamilies, ImportedCad, LinkedCad, LinkedRvt;
        public int ViewsTotal, ViewsNotOnSheets, Sheets, Schedules;
        public long? FileSizeBytes;
    }

    /// <summary>
    /// Builds the model snapshot (schema v1) from plain records, so the counting is testable without Revit.
    /// Every section carries an audience: "client" sections may go into deliverables, "internal" ones never do.
    /// </summary>
    internal static class ModelSnapshotBuilder
    {
        internal const int MaxWarningTypes = 50;

        internal static readonly IReadOnlyDictionary<string, string> Audience = new Dictionary<string, string>
        {
            ["composition"] = "client",
            ["health"] = "internal",
            ["networks"] = "client",
            ["data_quality"] = "internal",
        };

        internal static JObject Build(JObject model, IDictionary<string, JObject> sections, IEnumerable<string> truncated = null)
        {
            var body = new JObject();
            foreach (var pair in sections)
            {
                pair.Value["audience"] = Audience.TryGetValue(pair.Key, out var audience) ? audience : "internal";
                body[pair.Key] = pair.Value;
            }
            return new JObject
            {
                ["schema"] = ModelSnapshotSchema.Schema,
                ["schema_version"] = ModelSnapshotSchema.SchemaVersion,
                ["model"] = model,
                ["sections"] = body,
                ["truncated"] = new JObject { ["sections"] = new JArray((truncated ?? Enumerable.Empty<string>()).Cast<object>().ToArray()) },
            };
        }

        /// <summary>Element counts by category, level and (in workshared models) workset, largest first.</summary>
        internal static JObject Composition(IEnumerable<(string Category, string Level, string Workset)> elements, bool workshared)
        {
            var list = elements.ToList();
            var section = new JObject
            {
                ["elements_total"] = list.Count,
                ["by_category"] = Counts(list.Select(e => e.Category)),
                ["by_level"] = Counts(list.Select(e => e.Level)),
            };
            if (workshared) section["by_workset"] = Counts(list.Select(e => e.Workset));
            return section;
        }

        /// <summary>Warnings are grouped by <c>Key</c> (the failure type) and shown with the first <c>Label</c> of each group.</summary>
        internal static JObject Health(SnapshotHealthCounts counts, IEnumerable<(string Key, string Label)> warningTypes)
        {
            var warnings = warningTypes.ToList();
            var byType = warnings.GroupBy(w => Key(w.Key))
                .Select(g => new { description = Key(g.First().Label), count = g.Count() })
                .OrderByDescending(x => x.count).ThenBy(x => x.description, StringComparer.Ordinal).ToList();
            return new JObject
            {
                ["warnings_total"] = warnings.Count,
                ["warning_types"] = byType.Count,
                ["warnings_by_type"] = JArray.FromObject(byType.Take(MaxWarningTypes)),
                ["families_total"] = counts.FamiliesTotal,
                ["in_place_families"] = counts.InPlaceFamilies,
                ["imported_cad"] = counts.ImportedCad,
                ["linked_cad"] = counts.LinkedCad,
                ["linked_rvt"] = counts.LinkedRvt,
                ["views_total"] = counts.ViewsTotal,
                ["views_not_on_sheets"] = counts.ViewsNotOnSheets,
                ["sheets"] = counts.Sheets,
                ["schedules"] = counts.Schedules,
                ["file_size_bytes"] = counts.FileSizeBytes.HasValue ? (JToken)counts.FileSizeBytes.Value : JValue.CreateNull(),
            };
        }

        /// <summary>
        /// Networks grouped by the value of the network parameter. Elements without a value form one
        /// group with <c>unassigned=true</c>. Clashes are counted per unordered pair of networks; joints
        /// without a fitting are counted apart, because they are joints to fit rather than clashes.
        /// </summary>
        internal static JObject Networks(string groupBy, IEnumerable<SnapshotNetworkCurve> curves, IEnumerable<SnapshotNetworkItem> items,
            IEnumerable<(long Id, string Network)> openEnds, IEnumerable<PipeClash> clashes, int sampleLimit)
        {
            var curveList = curves.ToList();
            var itemList = items.ToList();
            var openList = openEnds.ToList();
            var clashList = (clashes ?? Enumerable.Empty<PipeClash>()).ToList();
            var names = curveList.Select(c => Key(c.Network)).Concat(itemList.Select(i => Key(i.Network)))
                .Concat(openList.Select(o => Key(o.Network))).Distinct().ToList();

            var pairs = clashList.Where(c => c.Kind != "junction_without_fitting")
                .GroupBy(c => Pair(Key(c.A.Comment), Key(c.B.Comment)))
                .Select(g => new
                {
                    g.Key.Item1, g.Key.Item2,
                    Count = g.Count(),
                    Sample = g.Take(sampleLimit).Select(c => new JArray(c.A.Id, c.B.Id)).ToList(),
                }).ToList();
            var joints = clashList.Where(c => c.Kind == "junction_without_fitting").ToList();

            var networks = new JArray();
            foreach (var name in names)
            {
                var own = curveList.Where(c => Key(c.Network) == name).ToList();
                var ownItems = itemList.Where(i => Key(i.Network) == name).ToList();
                var ownOpen = openList.Where(o => Key(o.Network) == name).Select(o => o.Id).ToList();
                var ownJoints = joints.Where(c => Key(c.A.Comment) == name || Key(c.B.Comment) == name).ToList();
                var ownPairs = pairs.Where(p => p.Item1 == name || p.Item2 == name).ToList();
                networks.Add(new JObject
                {
                    ["name"] = name,
                    ["unassigned"] = name.Length == 0,
                    ["pipes"] = own.Count(c => c.Kind == "pipe"),
                    ["conduits"] = own.Count(c => c.Kind == "conduit"),
                    ["length_m"] = Round(own.Sum(c => c.LengthM), 3),
                    ["by_diameter"] = new JArray(own.GroupBy(c => new { c.Kind, Dn = (int)Math.Round(c.DiameterMm) })
                        .OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Dn)
                        .Select(g => new JObject
                        {
                            ["kind"] = g.Key.Kind, ["dn_mm"] = g.Key.Dn, ["count"] = g.Count(), ["length_m"] = Round(g.Sum(c => c.LengthM), 3),
                        })),
                    ["fittings"] = ownItems.Count(i => i.Kind == "fitting"),
                    ["accessories"] = ownItems.Count(i => i.Kind == "accessory"),
                    ["structures"] = ownItems.Count(i => i.Kind == "structure"),
                    ["open_ends"] = new JObject { ["count"] = ownOpen.Count, ["elements"] = ownOpen.Distinct().Count(), ["sample_ids"] = new JArray(ownOpen.Distinct().Take(sampleLimit).Cast<object>().ToArray()) },
                    ["clashes_total"] = ownPairs.Sum(p => p.Count),
                    ["unjoined_joints"] = new JObject
                    {
                        ["count"] = ownJoints.Count,
                        ["sample_pairs"] = new JArray(ownJoints.Take(sampleLimit).Select(c => new JArray(c.A.Id, c.B.Id))),
                    },
                });
            }
            var ordered = new JArray(networks.OrderBy(n => n.Value<bool>("unassigned")).ThenByDescending(n => n.Value<double>("length_m"))
                .ThenBy(n => n.Value<string>("name"), StringComparer.Ordinal));

            return new JObject
            {
                ["group_by"] = groupBy,
                ["networks_total"] = names.Count(n => n.Length > 0),
                ["length_m"] = Round(curveList.Sum(c => c.LengthM), 3),
                ["items"] = ordered,
                ["clash_pairs"] = new JArray(pairs.OrderByDescending(p => p.Count).Select(p => new JObject
                {
                    ["a"] = p.Item1, ["b"] = p.Item2, ["count"] = p.Count, ["sample_pairs"] = new JArray(p.Sample),
                })),
                ["clashes_total"] = pairs.Sum(p => p.Count),
            };
        }

        /// <summary>How many elements in each check have the parameter filled, with a sample of those that do not.</summary>
        internal static JObject DataQuality(IEnumerable<(string Parameter, string Scope, IEnumerable<(long Id, bool Filled)> Elements)> checks, int sampleLimit)
        {
            var list = new JArray();
            foreach (var check in checks)
            {
                var elements = check.Elements.ToList();
                var filled = elements.Count(e => e.Filled);
                list.Add(new JObject
                {
                    ["parameter"] = check.Parameter,
                    ["scope"] = check.Scope,
                    ["total"] = elements.Count,
                    ["filled"] = filled,
                    ["pct"] = elements.Count == 0 ? (JToken)JValue.CreateNull() : Round(100.0 * filled / elements.Count, 1),
                    ["missing_sample_ids"] = new JArray(elements.Where(e => !e.Filled).Select(e => e.Id).Take(sampleLimit).Cast<object>().ToArray()),
                });
            }
            return new JObject { ["checks"] = list };
        }

        private static JArray Counts(IEnumerable<string> keys)
            => new JArray(keys.GroupBy(Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new JObject { ["name"] = g.Key, ["count"] = g.Count() }));

        private static string Key(string value) =>string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        private static Tuple<string, string> Pair(string a, string b)
            => string.CompareOrdinal(a, b) <= 0 ? Tuple.Create(a, b) : Tuple.Create(b, a);

        private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }
}
