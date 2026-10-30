using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CadLinkCalibrationTests
    {
        // Shared coordinates of the pilot project's internal origin (E, N), metres.
        private const double E0 = 472881.6, N0 = 4379962.2315;

        private static CadSegment Seg(string layer, double sx, double sy, double ex, double ey, double? length = null) => new CadSegment
        {
            Layer = layer, Sx = sx, Sy = sy, Ex = ex, Ey = ey,
            Length = length ?? Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy))
        };

        private static List<CadSegment> PilotDwg() => new List<CadSegment>
        {
            Seg("200", 472883.8462, 4379942.5175, 472860.2088, 4379975.3831),
            Seg("315", 472890.0793, 4379974.5788, 472918.3208, 4379974.0774),
            Seg("2T 250", 472886.735, 4379938.4840, 472904.401, 4379955.8490),
            Seg("110", 472905.2603, 4379956.2240, 472923.3761, 4379957.2406),
        };

        private static CadSegment Move(CadSegment s, Func<double, double, (double, double)> f, bool reverse = false)
        {
            var (sx, sy) = f(s.Sx, s.Sy);
            var (ex, ey) = f(s.Ex, s.Ey);
            var moved = reverse ? Seg(s.Layer, ex, ey, sx, sy) : Seg(s.Layer, sx, sy, ex, ey);
            moved.Length = Math.Sqrt((moved.Ex - moved.Sx) * (moved.Ex - moved.Sx) + (moved.Ey - moved.Sy) * (moved.Ey - moved.Sy));
            return moved;
        }

        [Fact]
        public void Shared_coordinates_offset_is_recovered_to_a_tenth_of_a_millimetre()
        {
            var dwg = PilotDwg();
            var revit = dwg.Select((s, i) => Move(s, (x, y) => (x - E0, y - N0), reverse: i % 2 == 1)).ToList();

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(4, fit.MatchedCurves);
            Assert.Equal(1, fit.Scale, 6);
            Assert.Equal(0, fit.RotationRad, 6);
            Assert.Equal(-E0, fit.Tx, 4);
            Assert.Equal(-N0, fit.Ty, 4);
            Assert.True(fit.ResidualMax < 0.0001);
            fit.Apply(472900, 4379960, out var qx, out var qy);
            Assert.Equal(472900 - E0, qx, 4);
            Assert.Equal(4379960 - N0, qy, 4);
        }

        [Fact]
        public void Repeated_curves_of_the_same_layer_and_length_elsewhere_are_not_matched()
        {
            var dwg = PilotDwg();
            var revit = dwg.Select(s => Move(s, (x, y) => (x - E0, y - N0))).ToList();
            // Same layer and length, 70 m away: the kind of false pair the pilot's potable-water plan had.
            revit.AddRange(dwg.Select(s => Move(s, (x, y) => (x - E0 + 70, y - N0 - 3))).Take(1));

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(4, fit.MatchedCurves);
            Assert.True(fit.ResidualMax < 0.0001);
            Assert.Equal(-E0, fit.Tx, 4);
        }

        [Fact]
        public void Rotation_and_feet_to_metres_scale_are_fitted()
        {
            var angle = 30 * Math.PI / 180;
            var scale = 0.3048;
            var dwg = PilotDwg().Select(s => Move(s, (x, y) => ((x - 472800) / 0.3048, (y - 4379900) / 0.3048))).ToList();
            var revit = dwg.Select(s => Move(s, (x, y) =>
                (scale * (Math.Cos(angle) * x - Math.Sin(angle) * y) + 12.5, scale * (Math.Sin(angle) * x + Math.Cos(angle) * y) - 7.25))).ToList();

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(4, fit.MatchedCurves);
            Assert.Equal(scale, fit.Scale, 6);
            Assert.Equal(angle, fit.RotationRad, 6);
            Assert.Equal(12.5, fit.Tx, 4);
            Assert.Equal(-7.25, fit.Ty, 4);
        }

        [Fact]
        public void Layers_must_match()
        {
            var dwg = PilotDwg();
            var revit = dwg.Select(s => { var m = Move(s, (x, y) => (x - E0, y - N0)); m.Layer = "other"; return m; }).ToList();

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(0, fit.Candidates);
            Assert.Equal(0, fit.MatchedCurves);
        }

        [Fact]
        public void Layer_names_compare_without_case()
        {
            var dwg = PilotDwg();
            // "2T 250" in the DWG, "2t 250" as Revit names the layer category.
            var revit = dwg.Select(s => { var m = Move(s, (x, y) => (x - E0, y - N0)); m.Layer = m.Layer.ToLowerInvariant(); return m; }).ToList();

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(4, fit.MatchedCurves);
        }

        [Fact]
        public void Closed_and_zero_length_curves_are_ignored()
        {
            var dwg = new List<CadSegment> { Seg("0", 10, 10, 10, 10, 46.0), Seg("0", 5, 5, 5, 5, 0) };
            var revit = new List<CadSegment> { Seg("0", 1, 1, 1, 1, 46.0) };

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(0, fit.Candidates);
            Assert.Equal(0, fit.MatchedCurves);
        }

        [Fact]
        public void A_polyline_uses_its_length_and_endpoints()
        {
            // An L-shaped polyline: chord 50 m, length 70 m.
            var dwg = new List<CadSegment> { Seg("315", 0, 0, 30, 40, 70), Seg("200", 100, 0, 100, 25) };
            var revit = dwg.Select(s => Move(s, (x, y) => (x - 5, y + 3))).ToList();
            revit[0].Length = 70;

            var fit = CadLinkCalibration.Fit(dwg, revit, 0.005);

            Assert.Equal(2, fit.MatchedCurves);
            Assert.Equal(-5, fit.Tx, 6);
            Assert.Equal(3, fit.Ty, 6);
        }
    }
}
