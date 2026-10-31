using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Straight round MEP curve (pipe or conduit) in mm: centreline, outer radius and identity.</summary>
    internal sealed class RoundCurve
    {
        public long Id;
        public string Network;     // piping/conduit system type name
        public string SystemName;  // system instance name
        public string Comment;
        public double Sx, Sy, Sz, Ex, Ey, Ez;
        public double OuterRadiusMm;
        public HashSet<long> Adjacent = new HashSet<long>();   // joined directly or through one fitting
    }

    internal sealed class PipeClash
    {
        public RoundCurve A, B;
        /// <summary>crossing, parallel (axes within 5°), duplicate (parallel and on the same axis), or
        /// junction_without_fitting (their nearest ends meet: a joint to fit, not a clash).</summary>
        public string Kind;
        public double AngleDeg;
        public double DistanceMm;          // between centrelines
        public double OverlapMm;           // radii + clearance - distance (> 0 = clash)
        public double X, Y, Z;             // midpoint of the closest points
        public double ZaMm, ZbMm;          // centreline levels at the crossing
        public double Ta, Tb;              // parameters 0..1 of the closest points along A and B
        /// <summary>Raise A so its bottom clears B's top by the clearance (mm, &lt;= 0 when it already does).</summary>
        public double RaiseAMm, LowerAMm, RaiseBMm, LowerBMm;
    }

    /// <summary>
    /// Finds clashes between straight round MEP curves from their centrelines and outer radii, which is exact
    /// for cylinders. Pairs joined by a fitting and crossings inside a structure of their own network are junctions.
    /// </summary>
    internal static class PipeClashFinder
    {
        internal const double ParallelDeg = 5.0;

        /// <summary>Clashes and, separately, joints without a fitting (nearest ends within reach), most severe first.</summary>
        internal static List<PipeClash> Find(IList<RoundCurve> scope, IList<RoundCurve> others, IList<StructureBox> structures,
            double clearanceMm, double structureMarginMm)
        {
            var clashes = new List<PipeClash>();
            var seen = new HashSet<Tuple<long, long>>();
            var pool = others ?? scope;
            foreach (var a in scope)
            {
                foreach (var b in pool)
                {
                    if (a.Id == b.Id || a.Adjacent.Contains(b.Id) || b.Adjacent.Contains(a.Id)) continue;
                    var key = a.Id < b.Id ? Tuple.Create(a.Id, b.Id) : Tuple.Create(b.Id, a.Id);
                    if (seen.Contains(key)) continue;
                    var reach = a.OuterRadiusMm + b.OuterRadiusMm + clearanceMm;
                    if (!BoxesNear(a, b, reach)) continue;
                    seen.Add(key);
                    var c = Closest(a, b);
                    if (c == null || c.DistanceMm >= reach) continue;
                    c.OverlapMm = reach - c.DistanceMm;
                    if (EndToEnd(c, a, b)) continue;
                    if (structures != null && structures.Any(s => Inside(s, c.X, c.Y, c.Z, structureMarginMm) && OwnNetwork(s, a, b))) continue;
                    Classify(c, reach);
                    AtPlanCrossing(c);
                    Clearances(c, clearanceMm);
                    clashes.Add(c);
                }
            }
            return clashes.OrderByDescending(x => x.OverlapMm).ToList();
        }

        /// <summary>Closest points of two segments (Sunday's algorithm), in mm.</summary>
        internal static PipeClash Closest(RoundCurve a, RoundCurve b)
        {
            double ux = a.Ex - a.Sx, uy = a.Ey - a.Sy, uz = a.Ez - a.Sz;
            double vx = b.Ex - b.Sx, vy = b.Ey - b.Sy, vz = b.Ez - b.Sz;
            double wx = a.Sx - b.Sx, wy = a.Sy - b.Sy, wz = a.Sz - b.Sz;
            double A = ux * ux + uy * uy + uz * uz, B = ux * vx + uy * vy + uz * vz, C = vx * vx + vy * vy + vz * vz;
            double D = ux * wx + uy * wy + uz * wz, E = vx * wx + vy * wy + vz * wz;
            if (A < 1e-9 || C < 1e-9) return null;
            double den = A * C - B * B, sN, sD = den, tN, tD = den;
            if (den < 1e-9 * A * C) { sN = 0; sD = 1; tN = E; tD = C; }
            else
            {
                sN = B * E - C * D; tN = A * E - B * D;
                if (sN < 0) { sN = 0; tN = E; tD = C; }
                else if (sN > sD) { sN = sD; tN = E + B; tD = C; }
            }
            if (tN < 0)
            {
                tN = 0;
                if (-D < 0) sN = 0; else if (-D > A) sN = sD; else { sN = -D; sD = A; }
            }
            else if (tN > tD)
            {
                tN = tD;
                if (-D + B < 0) sN = 0; else if (-D + B > A) sN = sD; else { sN = -D + B; sD = A; }
            }
            double s = Math.Abs(sN) < 1e-12 ? 0 : sN / sD, t = Math.Abs(tN) < 1e-12 ? 0 : tN / tD;
            double pax = a.Sx + s * ux, pay = a.Sy + s * uy, paz = a.Sz + s * uz;
            double pbx = b.Sx + t * vx, pby = b.Sy + t * vy, pbz = b.Sz + t * vz;
            double dx = pax - pbx, dy = pay - pby, dz = paz - pbz;
            return new PipeClash
            {
                A = a, B = b, Ta = s, Tb = t, ZaMm = paz, ZbMm = pbz,
                DistanceMm = Math.Sqrt(dx * dx + dy * dy + dz * dz),
                X = (pax + pbx) / 2, Y = (pay + pby) / 2, Z = (paz + pbz) / 2
            };
        }

        private static void Classify(PipeClash c, double reach)
        {
            RoundCurve a = c.A, b = c.B;
            double ux = a.Ex - a.Sx, uy = a.Ey - a.Sy, uz = a.Ez - a.Sz, vx = b.Ex - b.Sx, vy = b.Ey - b.Sy, vz = b.Ez - b.Sz;
            double cos = Math.Abs(ux * vx + uy * vy + uz * vz) / (Math.Sqrt(ux * ux + uy * uy + uz * uz) * Math.Sqrt(vx * vx + vy * vy + vz * vz));
            c.AngleDeg = Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
            if (c.AngleDeg < 90 - ParallelDeg && Continues(a, b, reach)) c.Kind = "junction_without_fitting";
            else if (c.AngleDeg >= ParallelDeg) c.Kind = "crossing";
            else c.Kind = c.DistanceMm < 0.2 * Math.Min(a.OuterRadiusMm, b.OuterRadiusMm) ? "duplicate" : "parallel";
        }

        /// <summary>
        /// One curve ends where the other begins and the second carries on beyond it (a polyline vertex
        /// left without a fitting). Side-by-side ends of parallel tubes do not continue each other.
        /// </summary>
        private static bool Continues(RoundCurve a, RoundCurve b, double reach)
        {
            var aEnds = new[] { new[] { a.Sx, a.Sy, a.Sz, a.Ex, a.Ey, a.Ez }, new[] { a.Ex, a.Ey, a.Ez, a.Sx, a.Sy, a.Sz } };
            var bEnds = new[] { new[] { b.Sx, b.Sy, b.Sz, b.Ex, b.Ey, b.Ez }, new[] { b.Ex, b.Ey, b.Ez, b.Sx, b.Sy, b.Sz } };
            foreach (var pa in aEnds)
                foreach (var pb in bEnds)
                {
                    double dx = pa[0] - pb[0], dy = pa[1] - pb[1], dz = pa[2] - pb[2];
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > reach) continue;
                    // Outward direction of a at this end, and the direction b runs away from its end.
                    double ox = pa[0] - pa[3], oy = pa[1] - pa[4], oz = pa[2] - pa[5];
                    double ix = pb[3] - pb[0], iy = pb[4] - pb[1], iz = pb[5] - pb[2];
                    if (ox * ix + oy * iy + oz * iz > 0) return true;
                }
            return false;
        }

        /// <summary>
        /// When the two curves cross in plan, the levels that matter are the ones on the same vertical:
        /// replace the closest-point levels and parameters by those at the plan intersection.
        /// </summary>
        internal static void AtPlanCrossing(PipeClash c)
        {
            RoundCurve a = c.A, b = c.B;
            double ux = a.Ex - a.Sx, uy = a.Ey - a.Sy, vx = b.Ex - b.Sx, vy = b.Ey - b.Sy;
            double den = ux * vy - uy * vx, la = Math.Sqrt(ux * ux + uy * uy), lb = Math.Sqrt(vx * vx + vy * vy);
            if (la < 1e-9 || lb < 1e-9 || Math.Abs(den) < 0.05 * la * lb) return;   // nearly parallel in plan
            double wx = b.Sx - a.Sx, wy = b.Sy - a.Sy;
            double s = (wx * vy - wy * vx) / den, u = (wx * uy - wy * ux) / den;
            if (s < 0 || s > 1 || u < 0 || u > 1) return;
            c.Ta = s; c.Tb = u;
            c.ZaMm = a.Sz + s * (a.Ez - a.Sz); c.ZbMm = b.Sz + u * (b.Ez - b.Sz);
            c.X = a.Sx + s * ux; c.Y = a.Sy + s * uy; c.Z = (c.ZaMm + c.ZbMm) / 2;
        }

        private static void Clearances(PipeClash c, double clearanceMm)
        {
            double ra = c.A.OuterRadiusMm, rb = c.B.OuterRadiusMm;
            c.RaiseAMm = (c.ZbMm + rb + clearanceMm) - (c.ZaMm - ra);
            c.LowerAMm = (c.ZaMm + ra) - (c.ZbMm - rb - clearanceMm);
            c.RaiseBMm = (c.ZaMm + ra + clearanceMm) - (c.ZbMm - rb);
            c.LowerBMm = (c.ZbMm + rb) - (c.ZaMm - ra - clearanceMm);
        }

        // Two curves that only touch where their ends meet form a junction (handled by the connect tool), not a clash.
        private static bool EndToEnd(PipeClash c, RoundCurve a, RoundCurve b)
        {
            bool aEnd = c.Ta < 1e-6 || c.Ta > 1 - 1e-6, bEnd = c.Tb < 1e-6 || c.Tb > 1 - 1e-6;
            if (!aEnd || !bEnd) return false;
            // Parallel curves have closest points at their ends even when they lie along each other.
            double ux = a.Ex - a.Sx, uy = a.Ey - a.Sy, uz = a.Ez - a.Sz, vx = b.Ex - b.Sx, vy = b.Ey - b.Sy, vz = b.Ez - b.Sz;
            var cos = Math.Abs(ux * vx + uy * vy + uz * vz) / (Math.Sqrt(ux * ux + uy * uy + uz * uz) * Math.Sqrt(vx * vx + vy * vy + vz * vz));
            if (cos > Math.Cos(ParallelDeg * Math.PI / 180)) return false;
            return c.DistanceMm <= Math.Max(a.OuterRadiusMm, b.OuterRadiusMm);
        }

        private static bool OwnNetwork(StructureBox s, RoundCurve a, RoundCurve b)
            => string.IsNullOrEmpty(s.Comment)
               || (string.Equals(s.Comment, a.Comment, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Comment, b.Comment, StringComparison.OrdinalIgnoreCase));

        private static bool Inside(StructureBox s, double x, double y, double z, double m)
            => x >= s.MinX - m && x <= s.MaxX + m && y >= s.MinY - m && y <= s.MaxY + m && z >= s.MinZ - m && z <= s.MaxZ + m;

        private static bool BoxesNear(RoundCurve a, RoundCurve b, double reach)
            => Math.Min(a.Sx, a.Ex) - reach <= Math.Max(b.Sx, b.Ex) && Math.Min(b.Sx, b.Ex) - reach <= Math.Max(a.Sx, a.Ex)
               && Math.Min(a.Sy, a.Ey) - reach <= Math.Max(b.Sy, b.Ey) && Math.Min(b.Sy, b.Ey) - reach <= Math.Max(a.Sy, a.Ey)
               && Math.Min(a.Sz, a.Ez) - reach <= Math.Max(b.Sz, b.Ez) && Math.Min(b.Sz, b.Ez) - reach <= Math.Max(a.Sz, a.Ez);
    }

    internal sealed class RegradePlan
    {
        public bool Feasible;
        public string Reason;
        public int MovedEnd;               // 0 = start, 1 = end of the moved curve
        public double EndDeltaMm;          // vertical move of the free end
        public double NewSx, NewSy, NewSz, NewEx, NewEy, NewEz;
        public double SlopeBeforePct, SlopeAfterPct;
    }

    /// <summary>
    /// Clears a clash by moving one end of a curve vertically while the other end stays fixed: the
    /// level at the crossing changes in proportion to its distance from the fixed end.
    /// </summary>
    internal static class PipeRegrade
    {
        internal static RegradePlan Plan(RoundCurve moved, RoundCurve other, int fixedEnd, bool above, double clearanceMm,
            double maxEndDeltaMm, bool keepFallDirection)
        {
            var c = PipeClashFinder.Closest(moved, other);
            var plan = new RegradePlan { MovedEnd = 1 - fixedEnd };
            if (c == null) { plan.Reason = "degenerate_curve"; return plan; }
            PipeClashFinder.AtPlanCrossing(c);
            double rm = moved.OuterRadiusMm, ro = other.OuterRadiusMm;
            var need = above ? (c.ZbMm + ro + clearanceMm) - (c.ZaMm - rm) : -((c.ZaMm + rm) - (c.ZbMm - ro - clearanceMm));
            if ((above && need <= 0) || (!above && need >= 0)) { plan.Feasible = true; plan.Reason = "already_clear"; Fill(plan, moved, 0); return plan; }
            // Share of the end move felt at the crossing: 0 at the fixed end, 1 at the moved end.
            var share = fixedEnd == 0 ? c.Ta : 1 - c.Ta;
            if (share < 0.05) { plan.Reason = "crossing_too_close_to_fixed_end"; return plan; }
            var delta = need / share;
            Fill(plan, moved, delta);
            // Sloped curves: the shortest distance is slightly less than the vertical gap, so check in 3D and add the residual.
            for (int i = 0; i < 4; i++)
            {
                var probe = new RoundCurve { Sx = plan.NewSx, Sy = plan.NewSy, Sz = plan.NewSz, Ex = plan.NewEx, Ey = plan.NewEy, Ez = plan.NewEz };
                var check = PipeClashFinder.Closest(probe, other);
                var residual = rm + ro + clearanceMm - check.DistanceMm;
                if (residual <= 0.01) break;
                delta += (above ? 1 : -1) * (residual + 0.1) / share;
                Fill(plan, moved, delta);
            }
            if (Math.Abs(delta) > maxEndDeltaMm) { plan.Reason = "end_move_exceeds_limit"; return plan; }
            if (keepFallDirection && Math.Sign(Fall(moved.Sz, moved.Ez)) != 0
                && Math.Sign(Fall(plan.NewSz, plan.NewEz)) != Math.Sign(Fall(moved.Sz, moved.Ez)))
            { plan.Reason = "reverses_fall"; return plan; }
            plan.Feasible = true;
            return plan;
        }

        private static double Fall(double zs, double ze) => Math.Round(zs - ze, 3);

        private static void Fill(RegradePlan p, RoundCurve m, double delta)
        {
            p.EndDeltaMm = delta;
            p.NewSx = m.Sx; p.NewSy = m.Sy; p.NewSz = m.Sz + (p.MovedEnd == 0 ? delta : 0);
            p.NewEx = m.Ex; p.NewEy = m.Ey; p.NewEz = m.Ez + (p.MovedEnd == 1 ? delta : 0);
            var plan = Math.Sqrt((m.Ex - m.Sx) * (m.Ex - m.Sx) + (m.Ey - m.Sy) * (m.Ey - m.Sy));
            p.SlopeBeforePct = plan < 1e-9 ? 0 : (m.Sz - m.Ez) / plan * 100;
            p.SlopeAfterPct = plan < 1e-9 ? 0 : (p.NewSz - p.NewEz) / plan * 100;
        }
    }
}
