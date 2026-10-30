using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Maps points given in a linked/imported CAD drawing's own coordinates (as AutoCAD reports them) into
    /// Revit internal and shared coordinates. Without calibration the drawing is assumed to be in the host's
    /// shared coordinates; with calibration curves read from the DWG, the placement is measured against the
    /// link's geometry and the fitted transform, its error and its difference from shared coordinates are returned.
    /// </summary>
    public class MapCadLinkPointsHandler : IRevitCommand
    {
        private const double FeetPerMetre = 1 / 0.3048;
        private const double MmPerFoot = 304.8;
        private const int MaxPoints = 2000;
        private const int MaxCalibrationCurves = 1000;

        private static readonly Dictionary<string, double> MetresPerUnit = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048, ["in"] = 0.0254
        };

        public string Name => "map_cad_link_points";

        public string Description =>
            "Map DWG coordinates of a CAD link into Revit internal and shared coordinates, optionally calibrated " +
            "against the link geometry with curves read from the DWG.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""link_instance_id"", ""points""],
  ""properties"": {
    ""link_instance_id"": { ""type"": ""integer"", ""description"": ""Linked or imported CAD ImportInstance ElementId."" },
    ""points"": { ""type"": ""array"", ""description"": ""1-2000 points in DWG coordinates: [x, y], [x, y, z] or {x, y, z?}. z is a shared elevation."" },
    ""dwg_units"": { ""type"": ""string"", ""enum"": [""m"", ""cm"", ""mm"", ""ft"", ""in""], ""description"": ""Length of one DWG drawing unit. Default m."" },
    ""calibration"": { ""type"": ""array"", ""description"": ""Up to 1000 DWG curves: {layer, start: [x, y], end: [x, y], length?} in DWG units."" },
    ""tolerance_mm"": { ""type"": ""number"", ""description"": ""Largest accepted calibration error, 0.1-1000 mm. Default 5."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("CAD link mapping is only available in project documents.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var idToken = request["link_instance_id"];
            if (idToken == null || !long.TryParse(idToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var linkId))
                return CommandResult.Fail("link_instance_id is required and must be an integer.");
            if (!RevitCompat.CanRepresentElementId(linkId))
                return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkId));
            var instance = doc.GetElement(RevitCompat.ToElementId(linkId)) as ImportInstance;
            if (instance == null)
                return CommandResult.Fail("Element " + linkId.ToString(CultureInfo.InvariantCulture) + " is not a linked or imported CAD instance. Use revit_list_linked_cad.");

            var units = request.Value<string>("dwg_units") ?? "m";
            if (!MetresPerUnit.TryGetValue(units, out var unitM))
                return CommandResult.Fail("dwg_units must be one of m, cm, mm, ft, in.");
            var toleranceMm = request["tolerance_mm"] == null ? 5.0 : request.Value<double>("tolerance_mm");
            if (!(toleranceMm >= 0.1 && toleranceMm <= 1000))
                return CommandResult.Fail("tolerance_mm must be between 0.1 and 1000.");

            List<double[]> points;
            List<CadSegment> calibration;
            try
            {
                points = ReadPoints(request["points"]);
                calibration = ReadCalibration(request["calibration"], unitM);
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return CommandResult.Fail("points and calibration coordinates must be numbers: " + ex.Message);
            }

            var warnings = new List<string>();
            var sharedToInternal = doc.ActiveProjectLocation.GetTotalTransform();
            var internalToShared = sharedToInternal.Inverse;

            CadLinkFit fit = null;
            object calibrationResult = null;
            var method = "shared_coordinates_assumed";
            if (calibration.Count > 0)
            {
                var linkCurves = ReadLinkCurves(doc, instance);
                fit = CadLinkCalibration.Fit(calibration, linkCurves, toleranceMm / 1000);
                var accepted = fit.MatchedCurves >= 2 && fit.ResidualMax * 1000 <= toleranceMm;
                double? deltaMm = null;
                if (fit.MatchedCurves > 0)
                {
                    // Where shared coordinates would put the calibration centroid, against the fitted transform.
                    var cx = calibration.Average(c => (c.Sx + c.Ex) / 2);
                    var cy = calibration.Average(c => (c.Sy + c.Ey) / 2);
                    fit.Apply(cx, cy, out var fx, out var fy);
                    var viaShared = sharedToInternal.OfPoint(new XYZ(cx * FeetPerMetre, cy * FeetPerMetre, 0));
                    deltaMm = Math.Round(Math.Sqrt(Math.Pow(fx * FeetPerMetre - viaShared.X, 2) + Math.Pow(fy * FeetPerMetre - viaShared.Y, 2)) * MmPerFoot, 1);
                }
                calibrationResult = new
                {
                    accepted,
                    calibration_curves = fit.CalibrationCurves,
                    link_curves = linkCurves.Count,
                    candidates = fit.Candidates,
                    matched_curves = fit.MatchedCurves,
                    residual_max_mm = fit.MatchedCurves > 0 ? Math.Round(fit.ResidualMax * 1000, 2) : (double?)null,
                    scale = fit.MatchedCurves > 0 ? Math.Round(fit.Scale, 6) : (double?)null,
                    rotation_deg = fit.MatchedCurves > 0 ? Math.Round(fit.RotationRad * 180 / Math.PI, 6) : (double?)null,
                    difference_from_shared_mm = deltaMm,
                    dwg_in_shared_coordinates = deltaMm.HasValue && accepted ? deltaMm.Value <= toleranceMm : (bool?)null
                };
                if (accepted)
                {
                    method = "calibrated";
                    if (Math.Abs(fit.Scale - 1) > 0.001)
                        warnings.Add("Fitted scale " + fit.Scale.ToString("0.######", CultureInfo.InvariantCulture) + " is not 1: check dwg_units (" + units + ").");
                }
                else
                {
                    fit = null;
                    warnings.Add("Calibration was not accepted (needs at least 2 matching curves within tolerance); points use shared coordinates and are unverified. " +
                        "Pass longer, distinctive curves (pipes, kerbs) with their exact layer names, and check dwg_units.");
                }
            }
            else
            {
                warnings.Add("No calibration given: the DWG is assumed to be in the host's shared coordinates. Pass calibration curves read from the DWG to verify.");
            }

            var mapped = new List<object>(points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                double xM = p[0] * unitM, yM = p[1] * unitM;
                var zM = p.Length > 2 ? p[2] * unitM : 0;
                XYZ internalPoint;
                if (fit != null)
                {
                    fit.Apply(xM, yM, out var ix, out var iy);
                    // The fit is planar: take the shared elevation for z.
                    var shared = internalToShared.OfPoint(new XYZ(ix * FeetPerMetre, iy * FeetPerMetre, 0));
                    internalPoint = sharedToInternal.OfPoint(new XYZ(shared.X, shared.Y, zM * FeetPerMetre));
                }
                else
                {
                    internalPoint = sharedToInternal.OfPoint(new XYZ(xM * FeetPerMetre, yM * FeetPerMetre, zM * FeetPerMetre));
                }
                var sharedPoint = internalToShared.OfPoint(internalPoint);
                mapped.Add(new
                {
                    index = i,
                    dwg = p,
                    internal_mm = Mm(internalPoint),
                    shared_mm = Mm(sharedPoint),
                    z_given = p.Length > 2
                });
            }

            return CommandResult.Ok(new
            {
                link = new
                {
                    id = RevitCompat.GetId(instance.Id),
                    name = doc.GetElement(instance.GetTypeId())?.Name,
                    is_linked = instance.IsLinked
                },
                method,
                dwg_units = units,
                calibration = calibrationResult,
                points = mapped,
                warnings
            });
        }

        private static double[] Mm(XYZ p) => new[] { Math.Round(p.X * MmPerFoot, 1), Math.Round(p.Y * MmPerFoot, 1), Math.Round(p.Z * MmPerFoot, 1) };

        private static List<double[]> ReadPoints(JToken token)
        {
            if (!(token is JArray array) || array.Count == 0 || array.Count > MaxPoints)
                throw new ArgumentException("points must be an array of 1-" + MaxPoints + " points.");
            var result = new List<double[]>(array.Count);
            foreach (var item in array) result.Add(ReadPoint(item, "points"));
            return result;
        }

        private static double[] ReadPoint(JToken item, string field)
        {
            double[] values;
            if (item is JArray coords && (coords.Count == 2 || coords.Count == 3))
                values = coords.Select(c => c.Type == JTokenType.Float || c.Type == JTokenType.Integer ? c.Value<double>() : double.NaN).ToArray();
            else if (item is JObject obj && obj["x"] != null && obj["y"] != null)
                values = obj["z"] == null
                    ? new[] { obj.Value<double>("x"), obj.Value<double>("y") }
                    : new[] { obj.Value<double>("x"), obj.Value<double>("y"), obj.Value<double>("z") };
            else
                throw new ArgumentException(field + " entries must be [x, y], [x, y, z] or {x, y, z?}.");
            if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException(field + " coordinates must be finite numbers.");
            return values;
        }

        private static List<CadSegment> ReadCalibration(JToken token, double unitM)
        {
            var result = new List<CadSegment>();
            if (token == null || token.Type == JTokenType.Null) return result;
            if (!(token is JArray array) || array.Count > MaxCalibrationCurves)
                throw new ArgumentException("calibration must be an array of up to " + MaxCalibrationCurves + " curves.");
            foreach (var item in array)
            {
                if (!(item is JObject curve) || string.IsNullOrWhiteSpace(curve.Value<string>("layer")))
                    throw new ArgumentException("calibration entries need layer, start and end.");
                var start = ReadPoint(curve["start"], "calibration start");
                var end = ReadPoint(curve["end"], "calibration end");
                var chord = Math.Sqrt(Math.Pow(end[0] - start[0], 2) + Math.Pow(end[1] - start[1], 2));
                var length = curve["length"] == null ? chord : curve.Value<double>("length");
                result.Add(new CadSegment
                {
                    Layer = curve.Value<string>("layer"),
                    Sx = start[0] * unitM, Sy = start[1] * unitM,
                    Ex = end[0] * unitM, Ey = end[1] * unitM,
                    Length = length * unitM
                });
            }
            return result;
        }

        // Curves of the link as Revit draws them, in host internal coordinates converted to metres.
        private static List<CadSegment> ReadLinkCurves(Document doc, ImportInstance instance)
        {
            var result = new List<CadSegment>();
            var styles = new Dictionary<long, string>();
            void Walk(GeometryObject go)
            {
                if (go is GeometryInstance gi)
                {
                    foreach (var g in gi.GetInstanceGeometry()) Walk(g);
                    return;
                }
                XYZ s, e;
                double length;
                if (go is Curve c && c.IsBound)
                {
                    s = c.GetEndPoint(0); e = c.GetEndPoint(1); length = c.Length;
                }
                else if (go is PolyLine pl)
                {
                    var pts = pl.GetCoordinates();
                    if (pts.Count < 2) return;
                    length = 0;
                    for (var i = 1; i < pts.Count; i++) length += pts[i].DistanceTo(pts[i - 1]);
                    s = pts[0]; e = pts[pts.Count - 1];
                }
                else return;
                var styleId = RevitCompat.GetId(go.GraphicsStyleId);
                if (!styles.TryGetValue(styleId, out var layer))
                    styles[styleId] = layer = (doc.GetElement(go.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                result.Add(new CadSegment
                {
                    Layer = layer,
                    Sx = s.X / FeetPerMetre, Sy = s.Y / FeetPerMetre,
                    Ex = e.X / FeetPerMetre, Ey = e.Y / FeetPerMetre,
                    Length = length / FeetPerMetre
                });
            }
            var geometry = instance.get_Geometry(new Options());
            if (geometry != null)
                foreach (var g in geometry) Walk(g);
            return result;
        }
    }
}
