using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>An open pipe End connector: position (mm), outward direction (unit), diameter (mm).</summary>
    internal sealed class PipeEnd
    {
        public long ElementId;
        public int ConnectorId;
        public double X, Y, Z;
        public double Dx, Dy, Dz;
        public double DiameterMm;
        public long SystemTypeId;
    }

    /// <summary>Axis-aligned extent (mm) of a structure such as a manhole, chamber, gully or tank.</summary>
    internal sealed class StructureBox
    {
        public long Id;
        public string Name;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    /// <summary>One fitting to insert. Ends are in API order: elbow/union/transition 2;
    /// tee run, run, branch; cross run A, run A, run B, run B.</summary>
    internal sealed class PlannedJunction
    {
        public string Kind;
        public List<PipeEnd> Ends;
        public double X, Y, Z;
        public double DeflectionDeg;
    }

    internal sealed class UnresolvedJunction
    {
        public string Reason;
        public List<PipeEnd> Ends;
        public double X, Y, Z;
    }

    internal sealed class PipeEndInStructure
    {
        public PipeEnd End;
        public StructureBox Structure;
    }

    internal sealed class PipeJunctionPlan
    {
        public List<PlannedJunction> Junctions = new List<PlannedJunction>();
        public List<PipeEndInStructure> InStructure = new List<PipeEndInStructure>();
        public List<UnresolvedJunction> Unresolved = new List<UnresolvedJunction>();
    }

    /// <summary>
    /// Decides which fitting joins open pipe ends that meet. A structure is the junction:
    /// ends inside a structure's extent stay open. Positions are never changed.
    /// </summary>
    internal static class PipeJunctionPlanner
    {
        internal const double StraightDeg = 1.0;      // two ends this close to opposite form a straight joint
        internal const double TeeRunDeg = 5.0;        // the run of a tee or cross may deviate this much
        internal const double SameDiameterMm = 0.5;

        internal static PipeJunctionPlan Plan(IList<PipeEnd> ends, IList<StructureBox> structures,
            double toleranceMm, double structureMarginMm)
        {
            var plan = new PipeJunctionPlan();
            foreach (var group in Cluster(ends, toleranceMm))
            {
                double cx = group.Average(e => e.X), cy = group.Average(e => e.Y), cz = group.Average(e => e.Z);
                var host = structures.FirstOrDefault(s => Inside(s, cx, cy, cz, structureMarginMm));
                if (host != null)
                {
                    plan.InStructure.AddRange(group.Select(e => new PipeEndInStructure { End = e, Structure = host }));
                    continue;
                }
                string reason;
                var junction = Decide(group, out reason);
                if (junction == null)
                {
                    plan.Unresolved.Add(new UnresolvedJunction { Reason = reason, Ends = group, X = cx, Y = cy, Z = cz });
                    continue;
                }
                junction.X = cx; junction.Y = cy; junction.Z = cz;
                plan.Junctions.Add(junction);
            }
            return plan;
        }

        private static PlannedJunction Decide(List<PipeEnd> g, out string reason)
        {
            reason = null;
            if (g.Count == 1) { reason = "open_end"; return null; }
            if (g.Count > 4) { reason = "too_many_ends"; return null; }
            if (g.Select(e => e.SystemTypeId).Distinct().Count() > 1) { reason = "system_type_mismatch"; return null; }

            if (g.Count == 2)
            {
                var bend = Deflection(g[0], g[1]);
                bool sameDn = Math.Abs(g[0].DiameterMm - g[1].DiameterMm) <= SameDiameterMm;
                if (bend <= StraightDeg)
                    return new PlannedJunction { Kind = sameDn ? "union" : "transition", Ends = g, DeflectionDeg = bend };
                if (!sameDn) { reason = "diameter_change_at_bend"; return null; }
                return new PlannedJunction { Kind = "elbow", Ends = g, DeflectionDeg = bend };
            }

            // Tee and cross need straight runs: pairs of ends pointing in opposite directions.
            var pairs = new List<Tuple<int, int, double>>();
            for (int i = 0; i < g.Count; i++)
                for (int j = i + 1; j < g.Count; j++)
                    pairs.Add(Tuple.Create(i, j, Deflection(g[i], g[j])));
            pairs.Sort((a, b) => a.Item3.CompareTo(b.Item3));

            if (g.Count == 3)
            {
                var run = pairs[0];
                if (run.Item3 > TeeRunDeg) { reason = "no_straight_run_for_tee"; return null; }
                var branch = Enumerable.Range(0, 3).First(k => k != run.Item1 && k != run.Item2);
                return new PlannedJunction { Kind = "tee", Ends = new List<PipeEnd> { g[run.Item1], g[run.Item2], g[branch] }, DeflectionDeg = run.Item3 };
            }

            var a = pairs[0];
            var b = pairs.FirstOrDefault(p => p.Item1 != a.Item1 && p.Item1 != a.Item2 && p.Item2 != a.Item1 && p.Item2 != a.Item2);
            if (a.Item3 > TeeRunDeg || b == null || b.Item3 > TeeRunDeg) { reason = "no_straight_runs_for_cross"; return null; }
            return new PlannedJunction
            {
                Kind = "cross",
                Ends = new List<PipeEnd> { g[a.Item1], g[a.Item2], g[b.Item1], g[b.Item2] },
                DeflectionDeg = Math.Max(a.Item3, b.Item3)
            };
        }

        /// <summary>Change of direction through a joint, in degrees: 0 when the two pipes continue straight.</summary>
        internal static double Deflection(PipeEnd a, PipeEnd b)
        {
            double la = Math.Sqrt(a.Dx * a.Dx + a.Dy * a.Dy + a.Dz * a.Dz), lb = Math.Sqrt(b.Dx * b.Dx + b.Dy * b.Dy + b.Dz * b.Dz);
            if (la < 1e-12 || lb < 1e-12) return 180;
            var cos = -(a.Dx * b.Dx + a.Dy * b.Dy + a.Dz * b.Dz) / (la * lb);
            return Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180 / Math.PI;
        }

        private static bool Inside(StructureBox s, double x, double y, double z, double margin)
            => x >= s.MinX - margin && x <= s.MaxX + margin && y >= s.MinY - margin && y <= s.MaxY + margin
               && z >= s.MinZ - margin && z <= s.MaxZ + margin;

        /// <summary>Groups ends whose positions chain within the tolerance (single linkage).</summary>
        private static List<List<PipeEnd>> Cluster(IList<PipeEnd> ends, double toleranceMm)
        {
            var parent = Enumerable.Range(0, ends.Count).ToArray();
            Func<int, int> find = null;
            find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            var t2 = toleranceMm * toleranceMm;
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                {
                    double dx = ends[i].X - ends[j].X, dy = ends[i].Y - ends[j].Y, dz = ends[i].Z - ends[j].Z;
                    if (dx * dx + dy * dy + dz * dz <= t2) parent[find(i)] = find(j);
                }
            return Enumerable.Range(0, ends.Count).GroupBy(find).Select(gr => gr.Select(i => ends[i]).ToList()).ToList();
        }
    }
}
