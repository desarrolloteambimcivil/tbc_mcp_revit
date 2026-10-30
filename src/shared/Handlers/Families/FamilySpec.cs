using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Parsed and validated description of a loadable family for create_family. Parsing has no Revit
    /// dependency, so a bad spec is rejected before any family document is opened. Lengths are mm.
    /// </summary>
    public sealed class FamilySpec
    {
        public const int MaxSolids = 50;
        public const int MaxParameters = 100;
        public const int MaxTypes = 50;
        public const int MaxProfilePoints = 200;

        public static readonly string[] ParameterTypes = { "length", "number", "integer", "yesno", "text", "material", "angle", "area", "volume" };
        public static readonly string[] ParameterGroups = { "dimensions", "constraints", "materials", "data", "text", "identity", "graphics" };
        public static readonly string[] Planes = { "XY", "XZ", "YZ" };

        public string Name;
        public string Template;
        public string Category;
        public string SavePath;
        public bool Overwrite;
        public bool LoadIntoProject = true;
        public List<ParameterSpec> Parameters = new List<ParameterSpec>();
        public List<TypeSpec> Types = new List<TypeSpec>();
        public List<SolidSpec> Solids = new List<SolidSpec>();

        public sealed class ParameterSpec
        {
            public string Name;
            public string Type;
            public string Group;
            public bool Instance;
            public string Formula;
        }

        public sealed class TypeSpec
        {
            public string Name;
            public Dictionary<string, JToken> Values = new Dictionary<string, JToken>(StringComparer.Ordinal);
        }

        public sealed class SolidSpec
        {
            public int Index;
            public string Shape;                 // extrusion | revolution
            public bool Void;
            public string Plane = "XY";          // extrusion sketch plane
            public double OffsetMm;              // sketch plane offset along its normal
            public List<double[]> Profile;       // closed polygon in plane coordinates (mm)
            public double[] Circle;              // { u, v, radius } in plane coordinates (mm)
            public double StartMm;
            public double EndMm;
            public double StartAngleDeg;
            public double EndAngleDeg = 360;
            public string EndParam;              // length parameter driving the extrusion end
            public string Material;              // material name
            public string MaterialParam;         // material parameter driving the solid's material
        }

        public static FamilySpec Parse(JObject json, out List<string> errors)
        {
            errors = new List<string>();
            var spec = new FamilySpec();
            if (json == null) { errors.Add("The family spec must be a JSON object."); return spec; }

            spec.Name = Str(json, "name");
            if (string.IsNullOrWhiteSpace(spec.Name)) errors.Add("name is required.");
            else if (spec.Name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '{', '}', '[', ']', ';' }) >= 0)
                errors.Add("name contains characters Revit or Windows do not allow in family names.");

            spec.Template = Str(json, "template");
            if (string.IsNullOrWhiteSpace(spec.Template)) errors.Add("template is required: a template name such as \"Metric Generic Model\", or an absolute .rft path.");
            spec.Category = Str(json, "category");
            spec.SavePath = Str(json, "save_path");
            if (!string.IsNullOrEmpty(spec.SavePath) && !spec.SavePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                errors.Add("save_path must end with .rfa.");
            spec.Overwrite = json.Value<bool?>("overwrite") ?? false;
            spec.LoadIntoProject = json.Value<bool?>("load_into_project") ?? true;

            ParseParameters(json["parameters"], spec, errors);
            ParseTypes(json["types"], spec, errors);
            ParseSolids(json["solids"], spec, errors);
            return spec;
        }

        private static void ParseParameters(JToken token, FamilySpec spec, List<string> errors)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (!(token is JArray array)) { errors.Add("parameters must be an array."); return; }
            if (array.Count > MaxParameters) errors.Add("parameters is limited to " + MaxParameters + " entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < array.Count; i++)
            {
                var label = "parameters[" + i + "]";
                if (!(array[i] is JObject item)) { errors.Add(label + " must be an object."); continue; }
                var p = new ParameterSpec
                {
                    Name = Str(item, "name"),
                    Type = (Str(item, "type") ?? "length").Trim().ToLowerInvariant(),
                    Instance = item.Value<bool?>("instance") ?? false,
                    Formula = Str(item, "formula")
                };
                p.Group = (Str(item, "group") ?? DefaultGroup(p.Type)).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(p.Name)) errors.Add(label + ".name is required.");
                else if (!names.Add(p.Name)) errors.Add(label + ": duplicate parameter name '" + p.Name + "'.");
                if (!ParameterTypes.Contains(p.Type)) errors.Add(label + ".type must be one of " + string.Join(", ", ParameterTypes) + ".");
                if (!ParameterGroups.Contains(p.Group)) errors.Add(label + ".group must be one of " + string.Join(", ", ParameterGroups) + ".");
                spec.Parameters.Add(p);
            }
        }

        internal static string DefaultGroup(string type)
        {
            switch (type)
            {
                case "length": case "angle": case "area": case "volume": return "dimensions";
                case "material": return "materials";
                case "text": return "text";
                default: return "data";
            }
        }

        private static void ParseTypes(JToken token, FamilySpec spec, List<string> errors)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (!(token is JArray array)) { errors.Add("types must be an array."); return; }
            if (array.Count > MaxTypes) errors.Add("types is limited to " + MaxTypes + " entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < array.Count; i++)
            {
                var label = "types[" + i + "]";
                if (!(array[i] is JObject item)) { errors.Add(label + " must be an object."); continue; }
                var t = new TypeSpec { Name = Str(item, "name") };
                if (string.IsNullOrWhiteSpace(t.Name)) errors.Add(label + ".name is required.");
                else if (!names.Add(t.Name)) errors.Add(label + ": duplicate type name '" + t.Name + "'.");
                var values = item["values"];
                if (values != null && values.Type != JTokenType.Null)
                {
                    if (!(values is JObject map)) errors.Add(label + ".values must be an object of parameter name to value.");
                    else foreach (var property in map.Properties()) t.Values[property.Name] = property.Value;
                }
                spec.Types.Add(t);
            }
        }

        private static void ParseSolids(JToken token, FamilySpec spec, List<string> errors)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (!(token is JArray array)) { errors.Add("solids must be an array."); return; }
            if (array.Count > MaxSolids) errors.Add("solids is limited to " + MaxSolids + " entries.");
            var lengthParams = new HashSet<string>(spec.Parameters.Where(p => p.Type == "length").Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var materialParams = new HashSet<string>(spec.Parameters.Where(p => p.Type == "material").Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < array.Count; i++)
            {
                var label = "solids[" + i + "]";
                if (!(array[i] is JObject item)) { errors.Add(label + " must be an object."); continue; }
                var s = new SolidSpec
                {
                    Index = i,
                    Shape = (Str(item, "shape") ?? "extrusion").Trim().ToLowerInvariant(),
                    Void = item.Value<bool?>("void") ?? false,
                    Plane = (Str(item, "plane") ?? "XY").Trim().ToUpperInvariant(),
                    OffsetMm = Num(item, "offset_mm") ?? 0,
                    StartMm = Num(item, "start_mm") ?? 0,
                    EndMm = Num(item, "end_mm") ?? 0,
                    StartAngleDeg = Num(item, "start_angle_deg") ?? 0,
                    EndAngleDeg = Num(item, "end_angle_deg") ?? 360,
                    EndParam = Str(item, "end_param"),
                    Material = Str(item, "material"),
                    MaterialParam = Str(item, "material_param")
                };

                if (s.Shape != "extrusion" && s.Shape != "revolution") errors.Add(label + ".shape must be extrusion or revolution.");
                if (!Planes.Contains(s.Plane)) errors.Add(label + ".plane must be XY, XZ or YZ.");
                if (s.Material != null && s.MaterialParam != null) errors.Add(label + ": give material or material_param, not both.");
                if (s.MaterialParam != null && !materialParams.Contains(s.MaterialParam))
                    errors.Add(label + ".material_param '" + s.MaterialParam + "' must be a parameter of type material.");

                ParseProfile(item, s, label, errors);

                if (s.Shape == "extrusion")
                {
                    if (Math.Abs(s.EndMm - s.StartMm) < 0.01) errors.Add(label + ": end_mm must differ from start_mm (the extrusion depth).");
                    if (s.EndParam != null && !lengthParams.Contains(s.EndParam))
                        errors.Add(label + ".end_param '" + s.EndParam + "' must be a parameter of type length.");
                }
                else if (s.Shape == "revolution")
                {
                    if (s.Circle != null) errors.Add(label + ": a revolution needs a profile polygon, not a circle.");
                    if (s.EndParam != null) errors.Add(label + ".end_param applies to extrusions only.");
                    var sweep = s.EndAngleDeg - s.StartAngleDeg;
                    if (sweep <= 0 || sweep > 360) errors.Add(label + ": end_angle_deg must be greater than start_angle_deg, at most 360 degrees apart.");
                    if (s.Profile != null && s.Profile.Any(p => p[0] < 0))
                        errors.Add(label + ": revolution profiles are drawn in the XZ plane as [radius, z] with radius >= 0 (the axis is Z).");
                }
                spec.Solids.Add(s);
            }
            if (spec.Solids.Count > 0 && spec.Solids.All(s => s.Void))
                errors.Add("solids needs at least one solid that is not a void.");
        }

        private static void ParseProfile(JObject item, SolidSpec s, string label, List<string> errors)
        {
            var given = new[] { "profile", "rect", "circle" }.Where(k => item[k] != null && item[k].Type != JTokenType.Null).ToList();
            if (given.Count != 1) { errors.Add(label + ": give exactly one of profile, rect or circle."); return; }

            if (given[0] == "profile")
            {
                if (!(item["profile"] is JArray points)) { errors.Add(label + ".profile must be an array of [u, v] points in mm."); return; }
                var list = new List<double[]>();
                foreach (var point in points)
                {
                    if (!(point is JArray pair) || pair.Count != 2 || pair.Any(c => c.Type != JTokenType.Integer && c.Type != JTokenType.Float))
                    { errors.Add(label + ".profile points must be [u, v] number pairs."); return; }
                    var p = new[] { pair[0].Value<double>(), pair[1].Value<double>() };
                    if (list.Count == 0 || Distance(list[list.Count - 1], p) >= 0.1) list.Add(p);
                }
                if (list.Count > 1 && Distance(list[0], list[list.Count - 1]) < 0.1) list.RemoveAt(list.Count - 1);
                if (list.Count < 3) errors.Add(label + ".profile needs at least 3 distinct points.");
                else if (list.Count > MaxProfilePoints) errors.Add(label + ".profile is limited to " + MaxProfilePoints + " points.");
                else if (Math.Abs(SignedArea(list)) < 1) errors.Add(label + ".profile encloses no area (points are collinear).");
                s.Profile = list;
            }
            else if (given[0] == "rect")
            {
                var rect = item["rect"] as JObject;
                var x = rect == null ? null : Num(rect, "x"); var y = rect == null ? null : Num(rect, "y");
                var w = rect == null ? null : Num(rect, "width"); var d = rect == null ? null : Num(rect, "depth");
                if (w == null || d == null || w <= 0 || d <= 0) { errors.Add(label + ".rect needs width and depth > 0 (and optional x, y of the lower-left corner)."); return; }
                double x0 = x ?? 0, y0 = y ?? 0;
                s.Profile = new List<double[]> { new[] { x0, y0 }, new[] { x0 + w.Value, y0 }, new[] { x0 + w.Value, y0 + d.Value }, new[] { x0, y0 + d.Value } };
            }
            else
            {
                var circle = item["circle"] as JObject;
                var r = circle == null ? null : Num(circle, "radius");
                if (r == null || r <= 0) { errors.Add(label + ".circle needs radius > 0 (and optional x, y of the centre)."); return; }
                s.Circle = new[] { Num(circle, "x") ?? 0, Num(circle, "y") ?? 0, r.Value };
            }
        }

        internal static double SignedArea(IList<double[]> points)
        {
            double area = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                area += a[0] * b[1] - b[0] * a[1];
            }
            return area / 2;
        }

        private static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

        private static string Str(JObject o, string key)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            var value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static double? Num(JObject o, string key)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<double>();
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }
    }
}
