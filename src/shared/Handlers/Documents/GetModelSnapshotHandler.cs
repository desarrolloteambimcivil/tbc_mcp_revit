using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Read-only: counts and groups the model into the snapshot sections (composition, health, networks,
    /// data quality). Nothing is written; the gateway stores the snapshot with its timestamp.
    /// </summary>
    public class GetModelSnapshotHandler : IRevitCommand
    {
        private const double Mm = FindPipeClashesHandler.Mm;
        private const double FeetToM = 0.3048;
        private const double StructureMarginMm = 100;
        private static readonly string[] AllSections = { "composition", "health", "networks", "data_quality" };

        public string Name => "get_model_snapshot";
        public string Description => "Count and group the model for a dated snapshot: composition, health, networks by a parameter (default Comments) and data quality. Read-only.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
""sections"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""composition"",""health"",""networks"",""data_quality""]},""description"":""Sections to compute. Default: all.""},
""network_parameter"":{""type"":""string"",""description"":""Instance parameter whose value names the network. Default Comments.""},
""clearance_mm"":{""type"":""number"",""description"":""Clearance for network clashes, mm. Default 0.""},
""sample_limit"":{""type"":""integer"",""description"":""Ids kept per list (open ends, clashes, missing values), 0-500. Default 50.""},
""max_clash_curves"":{""type"":""integer"",""description"":""Clash check is skipped above this many pipes and conduits. Default 5000.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var sections = (request["sections"] as JArray)?.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (sections == null || sections.Count == 0) sections = AllSections.ToList();
            var unknown = sections.Where(s => !AllSections.Contains(s)).ToList();
            if (unknown.Count > 0) return CommandResult.Fail("Unknown section(s): " + string.Join(", ", unknown) + ". Valid: " + string.Join(", ", AllSections) + ".");
            var parameter = string.IsNullOrWhiteSpace(request.Value<string>("network_parameter")) ? "Comments" : request.Value<string>("network_parameter").Trim();
            var clearance = request.Value<double?>("clearance_mm") ?? 0;
            var sampleLimit = Math.Max(0, Math.Min(500, request.Value<int?>("sample_limit") ?? 50));
            var maxClashCurves = Math.Max(0, request.Value<int?>("max_clash_curves") ?? 5000);
            if (clearance < 0) return CommandResult.Fail("clearance_mm must be zero or more.");

            Func<Element, string> network = e => NetworkValue(e, parameter);
            var result = new Dictionary<string, JObject>();
            var timings = new JObject();
            var truncated = new List<string>();
            var clock = Stopwatch.StartNew();

            foreach (var section in sections)
            {
                clock.Restart();
                switch (section)
                {
                    case "composition": result[section] = Composition(doc); break;
                    case "health": result[section] = Health(doc); break;
                    case "networks": result[section] = Networks(doc, parameter, network, clearance, sampleLimit, maxClashCurves, truncated); break;
                    case "data_quality": result[section] = DataQuality(doc, parameter, network, sampleLimit); break;
                }
                timings[section] = clock.ElapsedMilliseconds;
            }

            var snapshot = ModelSnapshotBuilder.Build(Model(app, doc), result, truncated);
            snapshot["parameters"] = new JObject
            {
                ["network_parameter"] = parameter, ["clearance_mm"] = clearance, ["sample_limit"] = sampleLimit, ["max_clash_curves"] = maxClashCurves,
            };
            snapshot["timings_ms"] = timings;
            return CommandResult.Ok(snapshot);
        }

        private static JObject Model(UIApplication app, Document doc)
        {
            var path = doc.PathName ?? "";
            string hash = null;
            if (path.Length > 0)
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
            return new JObject
            {
                ["title"] = doc.Title ?? "",
                ["revit_year"] = int.TryParse(app.Application.VersionNumber, out var year) ? (JToken)year : app.Application.VersionNumber,
                ["is_workshared"] = doc.IsWorkshared,
                ["saved"] = path.Length > 0,
                ["path_hash"] = hash,
            };
        }

        private static JObject Composition(Document doc)
        {
            var levels = new Dictionary<long, string>();
            WorksetTable worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;
            var rows = new List<(string, string, string)>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var category = e.Category;
                if (category == null || category.CategoryType != CategoryType.Model || !IsPhysical(e)) continue;
                var level = "";
                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                {
                    var key = RevitCompat.GetId(e.LevelId);
                    if (!levels.TryGetValue(key, out level)) levels[key] = level = doc.GetElement(e.LevelId)?.Name ?? "";
                }
                var workset = "";
                if (worksets != null)
                    try { workset = worksets.GetWorkset(e.WorksetId)?.Name ?? ""; } catch (Exception) { workset = ""; }
                rows.Add((category.Name, level, workset));
            }
            return ModelSnapshotBuilder.Composition(rows, doc.IsWorkshared);
        }

        private static JObject Health(Document doc)
        {
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            var placed = new HashSet<long>();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            foreach (var sheet in sheets)
                foreach (var id in sheet.GetAllPlacedViews()) placed.Add(RevitCompat.GetId(id));
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule) && SafeCanBePrinted(v)).ToList();
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Count(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule);
            long? size = null;
            try { if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName)) size = new FileInfo(doc.PathName).Length; }
            catch (Exception) { size = null; }

            var counts = new SnapshotHealthCounts
            {
                FamiliesTotal = families.Count,
                InPlaceFamilies = families.Count(f => SafeIsInPlace(f)),
                ImportedCad = imports.Count(i => !i.IsLinked),
                LinkedCad = imports.Count(i => i.IsLinked),
                LinkedRvt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(),
                ViewsTotal = views.Count,
                ViewsNotOnSheets = views.Count(v => !placed.Contains(RevitCompat.GetId(v.Id))),
                Sheets = sheets.Count,
                Schedules = schedules,
                FileSizeBytes = size,
            };
            // Group by the failure type, not the text: many messages repeat one type with a different system or element name.
            var registry = Autodesk.Revit.ApplicationServices.Application.GetFailureDefinitionRegistry();
            var labels = new Dictionary<Guid, string>();
            var warnings = doc.GetWarnings().Select(w =>
            {
                Guid key;
                try { key = w.GetFailureDefinitionId().Guid; } catch (Exception) { return (SafeDescription(w), SafeDescription(w)); }
                if (!labels.TryGetValue(key, out var label))
                {
                    try { label = registry?.FindFailureDefinition(w.GetFailureDefinitionId())?.GetDescriptionText(); } catch (Exception) { label = null; }
                    labels[key] = label = string.IsNullOrWhiteSpace(label) ? SafeDescription(w) : label;
                }
                return (key.ToString(), label);
            });
            return ModelSnapshotBuilder.Health(counts, warnings);
        }

        /// <summary>
        /// Elements that occupy the model: categories of model type still include materials, appearance assets, MEP
        /// systems, pipe segments, analytical centrelines (plain <see cref="Element"/>), sketches, links and base points.
        /// </summary>
        private static bool IsPhysical(Element e)
        {
            if (e is ElementType || e.ViewSpecific || e.GetType() == typeof(Element)) return false;
            if (e is Material || e is PropertySetElement || e is MEPSystem || e is View || e is CurveElement || e is SunAndShadowSettings
                || e is ImportInstance || e is RevitLinkInstance || e is BasePoint || e is InternalOrigin || e is ProjectInfo) return false;
            return e.get_BoundingBox(null) != null;
        }

        private static JObject Networks(Document doc, string parameter, Func<Element, string> network, double clearance, int sampleLimit,
            int maxClashCurves, List<string> truncated)
        {
            var curves = new List<SnapshotNetworkCurve>();
            var mepCurves = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<MEPCurve>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<MEPCurve>()).ToList();
            foreach (var m in mepCurves)
                curves.Add(new SnapshotNetworkCurve
                {
                    Id = RevitCompat.GetId(m.Id), Network = network(m), Kind = m is Pipe ? "pipe" : "conduit",
                    DiameterMm = SafeDiameter(m) * Mm, LengthM = (m.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0) * FeetToM,
                });

            var items = new List<SnapshotNetworkItem>();
            var fittings = Instances(doc, BuiltInCategory.OST_PipeFitting).Concat(Instances(doc, BuiltInCategory.OST_ConduitFitting)).ToList();
            var accessories = Instances(doc, BuiltInCategory.OST_PipeAccessory).ToList();
            items.AddRange(fittings.Select(f => new SnapshotNetworkItem { Id = RevitCompat.GetId(f.Id), Network = network(f), Kind = "fitting" }));
            items.AddRange(accessories.Select(a => new SnapshotNetworkItem { Id = RevitCompat.GetId(a.Id), Network = network(a), Kind = "accessory" }));

            // Structures count only when they carry a network value: generic models are also used for unrelated objects.
            var structures = FindPipeClashesHandler.ReadStructures(doc);
            foreach (var s in structures) s.Comment = network(doc.GetElement(RevitCompat.ToElementId(s.Id)));
            items.AddRange(structures.Where(s => !string.IsNullOrWhiteSpace(s.Comment))
                .Select(s => new SnapshotNetworkItem { Id = s.Id, Network = s.Comment, Kind = "structure" }));

            // An end inside a structure of its own network is a connection to that structure, not an open end.
            var openEnds = new List<(long, string)>();
            foreach (var element in mepCurves.Cast<Element>().Concat(fittings).Concat(accessories))
            {
                var manager = element is MEPCurve curve ? curve.ConnectorManager : (element as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (manager == null) continue;
                var value = network(element);
                foreach (Connector connector in manager.UnusedConnectors)
                {
                    if (connector.ConnectorType != ConnectorType.End) continue;
                    var p = connector.Origin;
                    double x = p.X * Mm, y = p.Y * Mm, z = p.Z * Mm;
                    if (structures.Any(s => Inside(s, x, y, z) && SameNetwork(s.Comment, value))) continue;
                    openEnds.Add((RevitCompat.GetId(element.Id), value));
                }
            }

            List<PipeClash> clashes = null;
            var round = FindPipeClashesHandler.ReadRoundCurves(doc);
            if (round.Count <= maxClashCurves)
            {
                foreach (var c in round) c.Comment = network(doc.GetElement(RevitCompat.ToElementId(c.Id)));
                clashes = PipeClashFinder.Find(round, null, structures, clearance, StructureMarginMm);
            }
            else truncated.Add("networks.clashes");

            var section = ModelSnapshotBuilder.Networks(parameter, curves, items, openEnds, clashes, sampleLimit);
            section["clashes_checked"] = clashes != null;
            section["clearance_mm"] = clearance;
            return section;
        }

        private static JObject DataQuality(Document doc, string parameter, Func<Element, string> network, int sampleLimit)
        {
            var scope = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<Element>())
                .Concat(Instances(doc, BuiltInCategory.OST_PipeFitting)).Concat(Instances(doc, BuiltInCategory.OST_ConduitFitting))
                .Concat(Instances(doc, BuiltInCategory.OST_PipeAccessory))
                .Select(e => (RevitCompat.GetId(e.Id), !string.IsNullOrWhiteSpace(network(e))));
            return ModelSnapshotBuilder.DataQuality(new[] { (parameter, "pipes, conduits, fittings, accessories", scope) }, sampleLimit);
        }

        private static IEnumerable<FamilyInstance> Instances(Document doc, BuiltInCategory category)
            => new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>();

        private static string NetworkValue(Element e, string parameter)
        {
            if (e == null) return null;
            var p = string.Equals(parameter, "Comments", StringComparison.OrdinalIgnoreCase)
                ? e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                : e.LookupParameter(parameter);
            if (p == null || !p.HasValue) return null;
            return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        private static bool Inside(StructureBox s, double x, double y, double z)
            => x >= s.MinX - StructureMarginMm && x <= s.MaxX + StructureMarginMm
               && y >= s.MinY - StructureMarginMm && y <= s.MaxY + StructureMarginMm
               && z >= s.MinZ - StructureMarginMm && z <= s.MaxZ + StructureMarginMm;

        private static bool SameNetwork(string a, string b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static double SafeDiameter(MEPCurve m)
        {
            try { return m.Diameter; } catch (Exception) { return 0; }
        }

        private static bool SafeIsInPlace(Family f)
        {
            try { return f.IsInPlace; } catch (Exception) { return false; }
        }

        private static bool SafeCanBePrinted(View v)
        {
            try { return v.CanBePrinted; } catch (Exception) { return false; }
        }

        private static string SafeDescription(FailureMessage warning)
        {
            try { return warning.GetDescriptionText(); } catch (Exception) { return ""; }
        }
    }
}
