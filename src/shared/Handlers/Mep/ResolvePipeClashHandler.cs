using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Clears one clash by moving one end of a pipe or conduit vertically while its other end stays fixed,
    /// so it passes above or below the other curve with a clearance. The moved end must be free (not joined
    /// to a fitting). dry_run (default true) reports the new levels, slope and any clash the move would create.
    /// </summary>
    public class ResolvePipeClashHandler : IRevitCommand
    {
        private const double Mm = FindPipeClashesHandler.Mm;

        public string Name => "resolve_pipe_clash";

        public string Description =>
            "Clear a pipe clash by moving one free end of a pipe or conduit vertically (the other end stays) so it passes above or below the other curve with clearance.";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""element_id"",""other_id"",""fixed_end"",""side""],""properties"":{""element_id"":{""type"":""integer""},""other_id"":{""type"":""integer""},""fixed_end"":{""type"":""string"",""enum"":[""start"",""end""]},""side"":{""type"":""string"",""enum"":[""above"",""below""]},""clearance_mm"":{""type"":""number"",""default"":50},""max_end_move_mm"":{""type"":""number"",""default"":1000},""keep_fall_direction"":{""type"":""boolean"",""default"":true},""dry_run"":{""type"":""boolean"",""default"":true}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var movedId = request.Value<long?>("element_id");
            var otherId = request.Value<long?>("other_id");
            var fixedEnd = (request.Value<string>("fixed_end") ?? "").Trim().ToLowerInvariant();
            var side = (request.Value<string>("side") ?? "").Trim().ToLowerInvariant();
            double clearance = request.Value<double?>("clearance_mm") ?? 50;
            double maxMove = request.Value<double?>("max_end_move_mm") ?? 1000;
            bool keepFall = request.Value<bool?>("keep_fall_direction") ?? true;
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (movedId == null || otherId == null) return CommandResult.Fail("element_id and other_id are required.");
            if (fixedEnd != "start" && fixedEnd != "end") return CommandResult.Fail("fixed_end must be start or end.");
            if (side != "above" && side != "below") return CommandResult.Fail("side must be above or below.");
            if (clearance < 0 || clearance > 1000) return CommandResult.Fail("clearance_mm must be in [0, 1000].");
            if (maxMove <= 0 || maxMove > 5000) return CommandResult.Fail("max_end_move_mm must be in (0, 5000].");
            foreach (var id in new[] { movedId.Value, otherId.Value })
                if (!RevitCompat.CanRepresentElementId(id)) return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

            var all = FindPipeClashesHandler.ReadRoundCurves(doc);
            var moved = all.FirstOrDefault(c => c.Id == movedId.Value);
            var other = all.FirstOrDefault(c => c.Id == otherId.Value);
            if (moved == null || other == null) return CommandResult.Fail("element_id and other_id must be straight pipes or conduits.");
            var element = doc.GetElement(RevitCompat.ToElementId(movedId.Value)) as MEPCurve;
            var line = (Line)((LocationCurve)element.Location).Curve;

            var plan = PipeRegrade.Plan(moved, other, fixedEnd == "start" ? 0 : 1, side == "above", clearance, maxMove, keepFall);
            var movedPoint = plan.MovedEnd == 0 ? line.GetEndPoint(0) : line.GetEndPoint(1);
            var joined = element.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(k => k.ConnectorType == ConnectorType.End && k.Origin.DistanceTo(movedPoint) < 1e-6 && k.IsConnected);
            if (joined != null && plan.Feasible && plan.Reason != "already_clear")
            {
                plan.Feasible = false;
                plan.Reason = "moved_end_is_joined";
            }

            // Clashes the moved curve would have in its new position (excluding the curves it is joined to).
            var after = new RoundCurve
            {
                Id = moved.Id, Network = moved.Network, SystemName = moved.SystemName, Comment = moved.Comment, OuterRadiusMm = moved.OuterRadiusMm,
                Sx = plan.NewSx, Sy = plan.NewSy, Sz = plan.NewSz, Ex = plan.NewEx, Ey = plan.NewEy, Ez = plan.NewEz, Adjacent = moved.Adjacent
            };
            var remaining = plan.Feasible
                ? PipeClashFinder.Find(new[] { after }, all.Where(c => c.Id != moved.Id).ToList(), FindPipeClashesHandler.ReadStructures(doc), clearance, 100)
                : new List<PipeClash>();

            bool applied = false;
            if (!dryRun && plan.Feasible && plan.Reason != "already_clear")
            {
                using (var tx = new Transaction(doc, "MCP: resolve pipe clash"))
                {
                    tx.Start();
                    ((LocationCurve)element.Location).Curve = Line.CreateBound(
                        new XYZ(plan.NewSx / Mm, plan.NewSy / Mm, plan.NewSz / Mm), new XYZ(plan.NewEx / Mm, plan.NewEy / Mm, plan.NewEz / Mm));
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; the pipe was not moved.");
                    applied = true;
                }
            }

            double inner = element is Autodesk.Revit.DB.Plumbing.Pipe p
                ? (p.get_Parameter(BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM)?.AsDouble() ?? p.Diameter) / 2 * Mm : element.Diameter / 2 * Mm;
            return CommandResult.Ok(new
            {
                dry_run = dryRun,
                applied,
                feasible = plan.Feasible,
                reason = plan.Reason,
                element_id = moved.Id,
                other_id = other.Id,
                moved_end = plan.MovedEnd == 0 ? "start" : "end",
                end_move_mm = Math.Round(plan.EndDeltaMm, 1),
                invert_before_mm = new[] { Math.Round(moved.Sz - inner, 1), Math.Round(moved.Ez - inner, 1) },
                invert_after_mm = new[] { Math.Round(plan.NewSz - inner, 1), Math.Round(plan.NewEz - inner, 1) },
                slope_before_pct = Math.Round(plan.SlopeBeforePct, 2),
                slope_after_pct = Math.Round(plan.SlopeAfterPct, 2),
                clashes_after = remaining.Select(c => new { other = c.B.Id, network = c.B.Network, overlap_mm = Math.Round(c.OverlapMm, 1),
                    point_mm = new[] { Math.Round(c.X, 1), Math.Round(c.Y, 1), Math.Round(c.Z, 1) } }).ToList(),
                note = dryRun && plan.Feasible ? "Plan only. Re-run with dry_run=false to move the pipe." : null
            });
        }
    }
}
