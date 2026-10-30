using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>A straight or curved segment by layer, endpoints and length, in metres.</summary>
    internal sealed class CadSegment
    {
        public string Layer;
        public double Sx, Sy, Ex, Ey, Length;

        internal bool HasDirection => Math.Abs(Ex - Sx) + Math.Abs(Ey - Sy) > 1e-9 && Length > 1e-9;
    }

    /// <summary>Similarity transform q = scale · R(rotation) · p + (Tx, Ty), in metres.</summary>
    internal sealed class CadLinkFit
    {
        public double Scale = 1, RotationRad, Tx, Ty;
        public int Candidates, CalibrationCurves, MatchedCurves;
        public double ResidualMax;

        internal void Apply(double x, double y, out double qx, out double qy)
        {
            var c = Math.Cos(RotationRad);
            var s = Math.Sin(RotationRad);
            qx = Scale * (c * x - s * y) + Tx;
            qy = Scale * (s * x + c * y) + Ty;
        }
    }

    /// <summary>
    /// Fits how a CAD link places its drawing in Revit: DWG curves (read in AutoCAD) are matched with the
    /// same curves in the link's Revit geometry by layer, length and endpoints, a consensus picks the
    /// transform most curves agree on, and a least-squares fit over those curves gives the result and its
    /// largest endpoint error. Revit's link transform alone is not enough: Revit re-bases far-away CAD
    /// geometry inside the link symbol.
    /// </summary>
    internal static class CadLinkCalibration
    {
        private const double ChordToLengthTolerance = 0.005;
        private const int MaxHypotheses = 300;

        internal static CadLinkFit Fit(IList<CadSegment> dwg, IList<CadSegment> revit, double tolerance)
        {
            var fit = new CadLinkFit { CalibrationCurves = dwg.Count };
            var candidates = new List<Candidate>();
            var revitByLayer = revit.Where(r => r.HasDirection)
                .GroupBy(r => r.Layer ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < dwg.Count; i++)
            {
                var d = dwg[i];
                if (!d.HasDirection || !revitByLayer.TryGetValue(d.Layer ?? "", out var sameLayer)) continue;
                var dvx = d.Ex - d.Sx;
                var dvy = d.Ey - d.Sy;
                var dChord = Math.Sqrt(dvx * dvx + dvy * dvy);
                foreach (var r in sameLayer)
                {
                    var ratio = r.Length / d.Length;
                    for (var flip = 0; flip < 2; flip++)
                    {
                        double ax = flip == 0 ? r.Sx : r.Ex, ay = flip == 0 ? r.Sy : r.Ey;
                        double bx = flip == 0 ? r.Ex : r.Sx, by = flip == 0 ? r.Ey : r.Sy;
                        var rvx = bx - ax;
                        var rvy = by - ay;
                        var scale = Math.Sqrt(rvx * rvx + rvy * rvy) / dChord;
                        if (Math.Abs(scale / ratio - 1) > ChordToLengthTolerance) continue;
                        var rotation = Math.Atan2(rvy, rvx) - Math.Atan2(dvy, dvx);
                        var cos = Math.Cos(rotation);
                        var sin = Math.Sin(rotation);
                        candidates.Add(new Candidate
                        {
                            DwgIndex = i,
                            Dwg = d,
                            Ax = ax, Ay = ay, Bx = bx, By = by,
                            Scale = scale,
                            Rotation = rotation,
                            Tx = ax - scale * (cos * d.Sx - sin * d.Sy),
                            Ty = ay - scale * (sin * d.Sx + cos * d.Sy)
                        });
                    }
                }
            }

            fit.Candidates = candidates.Count;
            if (candidates.Count == 0) return fit;

            // Consensus: the candidate transform that places the most distinct DWG curves onto a Revit curve.
            // Long curves give the steadiest rotation, so they are the hypotheses tried.
            List<Candidate> best = null;
            foreach (var c in candidates.OrderByDescending(k => k.Dwg.Length).Take(MaxHypotheses))
            {
                var support = candidates
                    .Where(k => EndpointError(c, k) <= tolerance)
                    .GroupBy(k => k.DwgIndex)
                    .Select(g => g.OrderBy(k => EndpointError(c, k)).First())
                    .ToList();
                if (best == null || support.Count > best.Count) best = support;
            }

            var pairs = new List<double[]>();
            foreach (var k in best)
            {
                pairs.Add(new[] { k.Dwg.Sx, k.Dwg.Sy, k.Ax, k.Ay });
                pairs.Add(new[] { k.Dwg.Ex, k.Dwg.Ey, k.Bx, k.By });
            }

            LeastSquares(pairs, fit);
            fit.MatchedCurves = best.Count;
            fit.ResidualMax = pairs.Max(p =>
            {
                fit.Apply(p[0], p[1], out var qx, out var qy);
                return Math.Sqrt((qx - p[2]) * (qx - p[2]) + (qy - p[3]) * (qy - p[3]));
            });
            return fit;
        }

        private static double EndpointError(Candidate transform, Candidate k)
        {
            var cos = Math.Cos(transform.Rotation);
            var sin = Math.Sin(transform.Rotation);
            double Err(double px, double py, double qx, double qy)
            {
                var x = transform.Scale * (cos * px - sin * py) + transform.Tx - qx;
                var y = transform.Scale * (sin * px + cos * py) + transform.Ty - qy;
                return Math.Sqrt(x * x + y * y);
            }
            return Math.Max(Err(k.Dwg.Sx, k.Dwg.Sy, k.Ax, k.Ay), Err(k.Dwg.Ex, k.Dwg.Ey, k.Bx, k.By));
        }

        // Closed-form 2D similarity fit (Umeyama) over point pairs [px, py, qx, qy], in centred coordinates
        // so large survey coordinates keep their precision.
        private static void LeastSquares(List<double[]> pairs, CadLinkFit fit)
        {
            double pcx = pairs.Average(p => p[0]), pcy = pairs.Average(p => p[1]);
            double qcx = pairs.Average(p => p[2]), qcy = pairs.Average(p => p[3]);
            double a = 0, b = 0, norm = 0;
            foreach (var p in pairs)
            {
                double px = p[0] - pcx, py = p[1] - pcy, qx = p[2] - qcx, qy = p[3] - qcy;
                a += px * qx + py * qy;
                b += px * qy - py * qx;
                norm += px * px + py * py;
            }
            var rotation = Math.Atan2(b, a);
            var scale = norm > 0 ? Math.Sqrt(a * a + b * b) / norm : 1;
            var cos = Math.Cos(rotation);
            var sin = Math.Sin(rotation);
            fit.Scale = scale;
            fit.RotationRad = rotation;
            fit.Tx = qcx - scale * (cos * pcx - sin * pcy);
            fit.Ty = qcy - scale * (sin * pcx + cos * pcy);
        }

        private sealed class Candidate
        {
            public int DwgIndex;
            public CadSegment Dwg;
            public double Ax, Ay, Bx, By, Scale, Rotation, Tx, Ty;
        }
    }
}
