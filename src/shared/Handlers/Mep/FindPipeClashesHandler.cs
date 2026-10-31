using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Finds clashes between straight pipes and conduits from their centrelines and outer diameters
    /// (exact for cylinders), with an optional clearance. Pairs joined through a fitting and crossings
    /// inside a structure of their own network are junctions, not clashes. Read-only.
    /// </summary>
    public class FindPipeClashesHandler : IRevitCommand
    {
        internal const double Mm = 304.8;

        public string Name => "find_pipe_clashes";

        public string Description =>
            "Find clashes between pipes and conduits (exact for round sections), with clearance; report the crossing levels and how much each side must rise or drop.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""system_name"":{""type"":""string""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""against"":{""type"":""string"",""enum"":[""all"",""scope""],""default"":""all""},""clearance_mm"":{""type"":""number"",""default"":0,""maximum"":1000},""structure_margin_mm"":{""type"":""number"",""default"":100,""maximum"":1000},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var systemName = request.Value<string>("system_name")?.Trim();
            var ids = (request["element_ids"] as JArray)?.Select(t => t.Value<long>()).ToList() ?? new List<long>();
            var against = (request.Value<string>("against") ?? "all").Trim().ToLowerInvariant();
            double clearance = request.Value<double?>("clearance_mm") ?? 0;
            double margin = request.Value<double?>("structure_margin_mm") ?? 100;
            int limit = request.Value<int?>("limit") ?? 200;
            if (against != "all" && against != "scope") return CommandResult.Fail("against must be all or scope.");
            if (clearance < 0 || clearance > 1000) return CommandResult.Fail("clearance_mm must be in [0, 1000].");
            if (margin < 0 || margin > 1000) return CommandResult.Fail("structure_margin_mm must be in [0, 1000].");
            if (limit < 1 || limit > 1000) return CommandResult.Fail("limit must be in [1, 1000].");
            foreach (var id in ids)
                if (!RevitCompat.CanRepresentElementId(id)) return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

            var all = ReadRoundCurves(doc);
            var scope = all.Where(c => InScope(c, systemName, ids)).ToList();
            if (scope.Count == 0)
                return CommandResult.Fail("No straight pipes or conduits match the scope.");

            var clashes = PipeClashFinder.Find(scope, against == "all" ? all : scope, ReadStructures(doc), clearance, margin);
            var curves = all.ToDictionary(c => c.Id);
            return CommandResult.Ok(new
            {
                scope_curves = scope.Count,
                compared_with = against == "all" ? all.Count : scope.Count,
                clearance_mm = clearance,
                clash_count = clashes.Count,
                truncated = clashes.Count > limit,
                clashes = clashes.Take(limit).Select(c => new
                {
                    point_mm = new[] { Math.Round(c.X, 1), Math.Round(c.Y, 1), Math.Round(c.Z, 1) },
                    overlap_mm = Math.Round(c.OverlapMm, 1),
                    a = Describe(doc, c.A, c.ZaMm),
                    b = Describe(doc, c.B, c.ZbMm),
                    a_is_above = c.ZaMm > c.ZbMm,
                    // Vertical moves at the crossing that would clear the clash with the clearance.
                    raise_a_mm = Math.Round(c.RaiseAMm, 1), lower_a_mm = Math.Round(c.LowerAMm, 1),
                    raise_b_mm = Math.Round(c.RaiseBMm, 1), lower_b_mm = Math.Round(c.LowerBMm, 1)
                }).ToList(),
                note = "Gravity networks usually keep the levels taken from drawings; resolve with revit_resolve_pipe_clash by moving the end whose level is assumed or free."
            });
        }

        private static object Describe(Document doc, RoundCurve c, double zAtCrossing)
        {
            var e = doc.GetElement(RevitCompat.ToElementId(c.Id));
            var mep = e as MEPCurve;
            return new
            {
                id = c.Id, category = e?.Category?.Name, network = c.Network, comments = c.Comment,
                outer_diameter_mm = Math.Round(c.OuterRadiusMm * 2, 1),
                centre_z_at_crossing_mm = Math.Round(zAtCrossing, 1),
                ends = mep == null ? null : mep.ConnectorManager.Connectors.Cast<Connector>().Where(k => k.ConnectorType == ConnectorType.End)
                    .Select(k => new { index = (k.Origin.DistanceTo(((LocationCurve)mep.Location).Curve.GetEndPoint(0)) < 1e-6) ? "start" : "end",
                        z_mm = Math.Round(k.Origin.Z * Mm, 1), connected = k.IsConnected }).OrderBy(k => k.index == "end").ToList()
            };
        }

        internal static bool InScope(RoundCurve c, string systemName, List<long> ids)
        {
            if (ids.Count > 0 && !ids.Contains(c.Id)) return false;
            return string.IsNullOrEmpty(systemName) || (c.Network ?? "").StartsWith(systemName, StringComparison.OrdinalIgnoreCase)
                   || (c.SystemName ?? "").StartsWith(systemName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Straight pipes and conduits with their outer radius and the curves they are joined to.</summary>
        internal static List<RoundCurve> ReadRoundCurves(Document doc)
        {
            var list = new List<RoundCurve>();
            var curves = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<MEPCurve>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<MEPCurve>());
            foreach (var m in curves)
            {
                if (!((m.Location as LocationCurve)?.Curve is Line line)) continue;
                double od = m is Pipe
                    ? m.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() ?? m.Diameter
                    : m.get_Parameter(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM)?.AsDouble() ?? m.Diameter;
                if (od <= 0) continue;
                XYZ s = line.GetEndPoint(0), e = line.GetEndPoint(1);
                var c = new RoundCurve
                {
                    Id = RevitCompat.GetId(m.Id),
                    Network = m is Pipe ? m.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() : m.Document.GetElement(m.GetTypeId())?.Name,
                    SystemName = m.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString(),
                    Comment = m.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                    Sx = s.X * Mm, Sy = s.Y * Mm, Sz = s.Z * Mm, Ex = e.X * Mm, Ey = e.Y * Mm, Ez = e.Z * Mm,
                    OuterRadiusMm = od / 2 * Mm
                };
                foreach (Connector k in m.ConnectorManager.Connectors)
                    foreach (Connector r in k.AllRefs)
                    {
                        var owner = r.Owner;
                        if (owner == null || owner.Id == m.Id || owner is MEPSystem) continue;
                        c.Adjacent.Add(RevitCompat.GetId(owner.Id));
                        // Curves joined through one fitting are neighbours too.
                        if (owner is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
                            foreach (Connector fk in fi.MEPModel.ConnectorManager.Connectors)
                                foreach (Connector fr in fk.AllRefs)
                                    if (fr.Owner != null && !(fr.Owner is MEPSystem) && fr.Owner.Id != fi.Id) c.Adjacent.Add(RevitCompat.GetId(fr.Owner.Id));
                    }
                list.Add(c);
            }
            return list;
        }

        internal static List<StructureBox> ReadStructures(Document doc)
        {
            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_SpecialityEquipment,
#if REVIT2024_OR_GREATER
                BuiltInCategory.OST_PlumbingEquipment,
#endif
            };
            var boxes = new List<StructureBox>();
            foreach (var category in categories)
                foreach (var e in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    boxes.Add(new StructureBox
                    {
                        Id = RevitCompat.GetId(e.Id), Name = e.Symbol?.FamilyName + ": " + e.Symbol?.Name,
                        Comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                        MinX = bb.Min.X * Mm, MinY = bb.Min.Y * Mm, MinZ = bb.Min.Z * Mm, MaxX = bb.Max.X * Mm, MaxY = bb.Max.Y * Mm, MaxZ = bb.Max.Z * Mm
                    });
                }
            return boxes;
        }
    }
}
