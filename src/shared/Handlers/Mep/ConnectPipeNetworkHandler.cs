using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Joins pipes that meet without a structure: open pipe End connectors within a tolerance are
    /// grouped and get an elbow, union, transition, tee or cross. Ends inside a structure's extent
    /// (manhole, chamber, gully, tank...) stay open because the structure is the junction.
    /// Pipes are never moved; a fitting Revit refuses is reported, not forced.
    /// </summary>
    public class ConnectPipeNetworkHandler : IRevitCommand
    {
        private const double Mm = 304.8;
        private const int MaxPipes = 5000;

        // Plumbing Equipment exists from Revit 2024; earlier versions model it as Mechanical Equipment.
        private static readonly BuiltInCategory[] StructureCategories =
        {
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_SpecialityEquipment,
#if REVIT2024_OR_GREATER
            BuiltInCategory.OST_PlumbingEquipment,
#endif
        };

        public string Name => "connect_pipe_network";

        public string Description =>
            "Insert fittings where pipes meet outside structures (elbow, union, transition, tee, cross; a tee after splitting a pipe whose side another pipe ends on). " +
            "Ends inside generic models or plumbing/mechanical equipment stay open. dry_run (default true) returns the plan only.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""system_name"":{""type"":""string"",""description"":""Prefix of the piping system or system type name""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""tolerance_mm"":{""type"":""number"",""default"":5,""maximum"":50},""structure_margin_mm"":{""type"":""number"",""default"":100,""maximum"":1000},""copy_comments"":{""type"":""boolean"",""default"":true},""dry_run"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":200,""maximum"":500}}}";

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
            double tolerance = request.Value<double?>("tolerance_mm") ?? 5;
            double margin = request.Value<double?>("structure_margin_mm") ?? 100;
            bool copyComments = request.Value<bool?>("copy_comments") ?? true;
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            int limit = request.Value<int?>("limit") ?? 200;
            if (string.IsNullOrEmpty(systemName) && ids.Count == 0)
                return CommandResult.Fail("Provide system_name or element_ids; the whole model is never processed by default.");
            if (tolerance <= 0 || tolerance > 50) return CommandResult.Fail("tolerance_mm must be in (0, 50].");
            if (margin < 0 || margin > 1000) return CommandResult.Fail("structure_margin_mm must be in [0, 1000].");
            if (limit < 1 || limit > 500) return CommandResult.Fail("limit must be in [1, 500].");
            foreach (var id in ids)
                if (!RevitCompat.CanRepresentElementId(id)) return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

            var pipes = SelectPipes(doc, systemName, ids);
            if (pipes.Count == 0)
                return CommandResult.Fail("No pipes match " + (ids.Count > 0 ? "element_ids" : "system_name '" + systemName + "'") + ".");
            if (pipes.Count > MaxPipes)
                return CommandResult.Fail("Scope has " + pipes.Count + " pipes; narrow system_name or element_ids to at most " + MaxPipes + ".");

            var ends = OpenEnds(pipes);
            var plan = PipeJunctionPlanner.Plan(ends, Structures(doc), tolerance, margin, Axes(pipes));
            var todo = plan.Junctions.Take(limit).ToList();

            var created = new List<object>();
            var failed = new List<object>();
            var pieces = new Dictionary<long, List<long>>();   // pipe split by a tee_on_pipe -> pieces created from it
            if (!dryRun && todo.Count > 0)
            {
                using (var tx = new Transaction(doc, "MCP: connect pipe network"))
                {
                    tx.Start();
                    foreach (var j in todo)
                    {
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            try
                            {
                                var fitting = Create(doc, j, pieces, tolerance);
                                if (fitting == null) throw new InvalidOperationException("Revit returned no fitting.");
                                var comment = copyComments ? SharedComment(doc, j) : null;
                                if (comment != null) fitting.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);
                                sub.Commit();
                                created.Add(new { fitting_id = RevitCompat.GetId(fitting.Id), kind = j.Kind, pipes = PipeIds(j),
                                    point_mm = Point(j.X, j.Y, j.Z), deflection_deg = Math.Round(j.DeflectionDeg, 1), comments = comment });
                            }
                            catch (Exception ex)
                            {
                                if (sub.HasStarted()) sub.RollBack();
                                failed.Add(new { kind = j.Kind, pipes = PipeIds(j), point_mm = Point(j.X, j.Y, j.Z),
                                    deflection_deg = Math.Round(j.DeflectionDeg, 1), error = ex.Message });
                            }
                        }
                    }
                    if (created.Count == 0)
                    {
                        tx.RollBack();
                    }
                    else if (tx.Commit() != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail("Revit did not commit the transaction; no fittings were confirmed.");
                    }
                }
            }

            var openAfter = dryRun ? (int?)null : OpenEnds(SelectPipes(doc, systemName, ids)).Count;
            return CommandResult.Ok(new
            {
                dry_run = dryRun,
                pipes = pipes.Count,
                open_ends_before = ends.Count,
                open_ends_after = openAfter,
                planned = plan.Junctions.Count,
                processed = dryRun ? 0 : todo.Count,
                truncated = plan.Junctions.Count > todo.Count,
                junctions = dryRun ? todo.Select(j => (object)new { kind = j.Kind, pipes = PipeIds(j),
                    diameters_mm = j.Ends.Select(e => Math.Round(e.DiameterMm, 1)).ToArray(), point_mm = Point(j.X, j.Y, j.Z),
                    deflection_deg = Math.Round(j.DeflectionDeg, 1) }).ToList() : null,
                created,
                failed,
                in_structure = plan.InStructure.Take(limit).Select(s => new { pipe = s.End.ElementId, structure_id = s.Structure.Id, structure = s.Structure.Name }).ToList(),
                in_structure_count = plan.InStructure.Count,
                unresolved = plan.Unresolved.Take(limit).Select(u => new { reason = u.Reason, pipes = u.Ends.Select(e => e.ElementId).ToArray(), point_mm = Point(u.X, u.Y, u.Z) }).ToList(),
                unresolved_count = plan.Unresolved.Count,
                note = dryRun ? "Plan only. Re-run with dry_run=false to insert the fittings." : null
            });
        }

        private static List<Pipe> SelectPipes(Document doc, string systemName, List<long> ids)
        {
            IEnumerable<Pipe> pipes = ids.Count > 0
                ? ids.Select(id => doc.GetElement(RevitCompat.ToElementId(id)) as Pipe).Where(p => p != null)
                : new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>();
            if (string.IsNullOrEmpty(systemName)) return pipes.ToList();
            return pipes.Where(p =>
            {
                var system = p.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                var type = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "";
                return system.StartsWith(systemName, StringComparison.OrdinalIgnoreCase) || type.StartsWith(systemName, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        private static List<PipeEnd> OpenEnds(IEnumerable<Pipe> pipes)
        {
            var ends = new List<PipeEnd>();
            foreach (var pipe in pipes)
            {
                var systemType = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                var comment = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                foreach (Connector c in pipe.ConnectorManager.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.End || c.Domain != Domain.DomainPiping || c.IsConnected) continue;
                    var o = c.Origin; var d = c.CoordinateSystem.BasisZ;
                    ends.Add(new PipeEnd
                    {
                        ElementId = RevitCompat.GetId(pipe.Id), ConnectorId = c.Id,
                        X = o.X * Mm, Y = o.Y * Mm, Z = o.Z * Mm, Dx = d.X, Dy = d.Y, Dz = d.Z,
                        DiameterMm = pipe.Diameter * Mm,
                        SystemTypeId = systemType == null ? -1 : RevitCompat.GetId(systemType), Comment = comment
                    });
                }
            }
            return ends;
        }

        private static List<PipeAxis> Axes(IEnumerable<Pipe> pipes)
        {
            var axes = new List<PipeAxis>();
            foreach (var pipe in pipes)
            {
                if (!((pipe.Location as LocationCurve)?.Curve is Line line)) continue;
                XYZ s = line.GetEndPoint(0), e = line.GetEndPoint(1);
                var systemType = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                axes.Add(new PipeAxis
                {
                    ElementId = RevitCompat.GetId(pipe.Id), Sx = s.X * Mm, Sy = s.Y * Mm, Sz = s.Z * Mm, Ex = e.X * Mm, Ey = e.Y * Mm, Ez = e.Z * Mm,
                    DiameterMm = pipe.Diameter * Mm, SystemTypeId = systemType == null ? -1 : RevitCompat.GetId(systemType)
                });
            }
            return axes;
        }

        private static long[] PipeIds(PlannedJunction j)
            => j.TargetPipeId.HasValue ? j.Ends.Select(e => e.ElementId).Concat(new[] { j.TargetPipeId.Value }).ToArray() : j.Ends.Select(e => e.ElementId).ToArray();

        private static List<StructureBox> Structures(Document doc)
        {
            var boxes = new List<StructureBox>();
            foreach (var category in StructureCategories)
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

        private static FamilyInstance Create(Document doc, PlannedJunction j, Dictionary<long, List<long>> pieces, double toleranceMm)
        {
            if (j.Kind == "tee_on_pipe") return TeeOnPipe(doc, j, pieces, toleranceMm);
            var c = j.Ends.Select(e => Find(doc, e)).ToArray();
            switch (j.Kind)
            {
                case "elbow": return doc.Create.NewElbowFitting(c[0], c[1]);
                case "union": return doc.Create.NewUnionFitting(c[0], c[1]);
                case "transition": return doc.Create.NewTransitionFitting(c[0], c[1]);
                case "tee": return doc.Create.NewTeeFitting(c[0], c[1], c[2]);
                case "cross": return doc.Create.NewCrossFitting(c[0], c[1], c[2], c[3]);
                default: throw new InvalidOperationException("Unknown fitting kind " + j.Kind + ".");
            }
        }

        // Split the target pipe at the branch point and join the two new ends and the branch with a tee.
        // An earlier tee in the same call may already have split the target, so the piece holding the point is used.
        private static FamilyInstance TeeOnPipe(Document doc, PlannedJunction j, Dictionary<long, List<long>> pieces, double toleranceMm)
        {
            var target = j.TargetPipeId.Value;
            var point = new XYZ(j.X / Mm, j.Y / Mm, j.Z / Mm);
            var tol = toleranceMm / Mm;
            List<long> known;
            var candidates = new[] { target }.Concat(pieces.TryGetValue(target, out known) ? known : new List<long>());
            Pipe host = null; XYZ at = null;
            foreach (var id in candidates)
            {
                var pipe = doc.GetElement(RevitCompat.ToElementId(id)) as Pipe;
                if (!((pipe?.Location as LocationCurve)?.Curve is Line line)) continue;
                var r = line.Project(point);
                if (r == null || r.Distance > tol) continue;
                if (r.XYZPoint.DistanceTo(line.GetEndPoint(0)) <= tol || r.XYZPoint.DistanceTo(line.GetEndPoint(1)) <= tol) continue;
                host = pipe; at = r.XYZPoint; break;
            }
            if (host == null) throw new InvalidOperationException("Pipe " + target + " no longer passes through the branch point.");
            var branch = Find(doc, j.Ends[0]);
            var newId = PlumbingUtils.BreakCurve(doc, host.Id, at);
            var other = doc.GetElement(newId) as Pipe;
            Func<Pipe, Connector> endAt = p => p.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End && !c.IsConnected).OrderBy(c => c.Origin.DistanceTo(at)).FirstOrDefault(c => c.Origin.DistanceTo(at) <= tol);
            var a = endAt(host); var b = other == null ? null : endAt(other);
            if (a == null || b == null) throw new InvalidOperationException("Splitting pipe " + target + " did not leave two open ends at the branch point.");
            var tee = doc.Create.NewTeeFitting(a, b, branch);
            if (!pieces.ContainsKey(target)) pieces[target] = new List<long>();
            pieces[target].Add(RevitCompat.GetId(newId));
            return tee;
        }

        // Connectors are re-read for each fitting: an earlier fitting in the same call may have changed the pipes.
        private static Connector Find(Document doc, PipeEnd end)
        {
            var pipe = doc.GetElement(RevitCompat.ToElementId(end.ElementId)) as Pipe;
            var connector = pipe?.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(c => c.Id == end.ConnectorId);
            if (connector == null) throw new InvalidOperationException("Connector " + end.ConnectorId + " of pipe " + end.ElementId + " is gone.");
            if (connector.IsConnected) throw new InvalidOperationException("Connector " + end.ConnectorId + " of pipe " + end.ElementId + " is already connected.");
            return connector;
        }

        // View filters often select a network by Comments; a fitting takes it only when all its pipes agree.
        private static string SharedComment(Document doc, PlannedJunction j)
        {
            var values = PipeIds(j).Select(id => doc.GetElement(RevitCompat.ToElementId(id))?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                .Distinct().ToList();
            return values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
        }

        private static double[] Point(double x, double y, double z) => new[] { Math.Round(x, 1), Math.Round(y, 1), Math.Round(z, 1) };
    }
}
