using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Creates a loadable family from a FamilySpec: new family document from a template, parameters,
    /// extrusions/revolutions (voids cut the solids they touch), parameter-driven extrusion ends and
    /// materials, types with values, then saves the .rfa and loads it into the active project.
    /// The family document is always closed without saving its in-memory state.
    /// </summary>
    public class CreateFamilyHandler : IRevitCommand
    {
        public string Name => "create_family";
        public string Description => "Create a loadable family (.rfa) from a JSON spec: template, parameters, extrusions/revolutions, types; save it and load it into the project.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""spec"":{""type"":""object"",""description"":""Family spec: name, template, category, parameters, types, solids, save_path, overwrite, load_into_project""}},""required"":[""spec""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var project = app.ActiveUIDocument?.Document;
            if (project == null) return CommandResult.Fail("No document is open.");
            if (project.IsFamilyDocument) return CommandResult.Fail("The active document is a family. Activate a project to create and load families into it.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var spec = FamilySpec.Parse(request["spec"] as JObject, out var specErrors);
            if (specErrors.Count > 0) return CommandResult.Fail("The family spec is not valid; nothing was created. " + string.Join(" ", specErrors));

            var template = FamilyTemplateCatalog.Resolve(app.Application, spec.Template, out var templateError);
            if (template == null) return CommandResult.Fail(templateError);

            var savePath = string.IsNullOrEmpty(spec.SavePath)
                ? Path.Combine(ProductIdentity.DataRoot(), "families", spec.Name + ".rfa")
                : spec.SavePath;
            if (!Path.IsPathRooted(savePath)) return CommandResult.Fail("save_path must be an absolute path.");
            var familyName = Path.GetFileNameWithoutExtension(savePath);
            if (File.Exists(savePath) && !spec.Overwrite)
                return CommandResult.Fail("A file already exists at " + savePath + ". Set overwrite=true to replace it, or choose another name or save_path.");
            if (spec.LoadIntoProject && !spec.Overwrite && new FilteredElementCollector(project).OfClass(typeof(Family))
                    .Any(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail("The project already has a family named '" + familyName + "'. Set overwrite=true to reload it, or choose another name.");

            BuiltInCategory? category = null;
            if (!string.IsNullOrEmpty(spec.Category))
            {
                var token = spec.Category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? spec.Category : "OST_" + spec.Category;
                if (!Enum.TryParse(token, true, out BuiltInCategory parsed))
                    return CommandResult.Fail("category '" + spec.Category + "' is not a BuiltInCategory name (for example OST_LightingFixtures or OST_GenericModel).");
                category = parsed;
            }

            var warnings = new List<string>();
            var materials = new List<object>();
            var solids = new List<object>();
            var cuts = new List<object>();
            Document familyDoc = null;
            try
            {
                try { familyDoc = app.Application.NewFamilyDocument(template); }
                catch (Exception ex) { return CommandResult.Fail("Could not create a family document from " + template + ": " + ex.Message); }
                if (familyDoc == null) return CommandResult.Fail("Revit returned no family document for " + template + ".");

                using (var tx = new Transaction(familyDoc, "MCP: Create family " + familyName))
                {
                    tx.Start();
                    try
                    {
                        if (category.HasValue)
                        {
                            var cat = Category.GetCategory(familyDoc, category.Value);
                            if (cat == null) throw new InvalidOperationException("category " + category.Value + " is not available in this template.");
                            familyDoc.OwnerFamily.FamilyCategory = cat;
                        }

                        var manager = familyDoc.FamilyManager;
                        if (manager.Types.Size == 0) manager.NewType(spec.Types.Count > 0 ? spec.Types[0].Name : familyName);
                        var parameters = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in spec.Parameters)
                        {
                            var existing = manager.get_Parameter(p.Name);
                            if (existing != null) { parameters[p.Name] = existing; warnings.Add("Parameter '" + p.Name + "' already exists in the template; it was reused."); continue; }
                            parameters[p.Name] = manager.AddParameter(p.Name, GroupFor(p.Group), SpecFor(p.Type), p.Instance);
                        }

                        var materialCache = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                        ElementId MaterialId(string name) => materialCache.TryGetValue(name, out var id)
                            ? id : (materialCache[name] = ResolveMaterial(familyDoc, project, name, materials));

                        var created = new List<GenericForm>();
                        foreach (var s in spec.Solids)
                        {
                            var form = s.Shape == "revolution" ? CreateRevolution(familyDoc, s) : (GenericForm)CreateExtrusion(familyDoc, s);
                            created.Add(form);
                            if (s.EndParam != null)
                                manager.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), parameters[s.EndParam]);
                            if (!s.Void)
                            {
                                if (s.Material != null) form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(MaterialId(s.Material));
                                if (s.MaterialParam != null)
                                    manager.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), parameters[s.MaterialParam]);
                            }
                            solids.Add(new { index = s.Index, shape = s.Shape, @void = s.Void, elementId = RevitCompat.GetId(form.Id) });
                        }

                        var typeSpecs = spec.Types.Count > 0 ? spec.Types : new List<FamilySpec.TypeSpec> { new FamilySpec.TypeSpec { Name = familyName } };
                        var declared = spec.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var t in typeSpecs)
                        {
                            var familyType = manager.Types.Cast<FamilyType>().FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                            if (familyType != null) manager.CurrentType = familyType;
                            else manager.CurrentType = manager.NewType(t.Name);
                            foreach (var value in t.Values)
                            {
                                var fp = parameters.TryGetValue(value.Key, out var known) ? known : manager.get_Parameter(value.Key);
                                if (fp == null) { warnings.Add("Type '" + t.Name + "': parameter '" + value.Key + "' does not exist; value skipped."); continue; }
                                if (declared.TryGetValue(value.Key, out var decl) && !string.IsNullOrEmpty(decl.Formula))
                                { warnings.Add("Type '" + t.Name + "': '" + value.Key + "' is driven by a formula; value skipped."); continue; }
                                var error = SetValue(manager, fp, value.Value, MaterialId);
                                if (error != null) throw new InvalidOperationException("Type '" + t.Name + "', parameter '" + value.Key + "': " + error);
                            }
                        }

                        foreach (var p in spec.Parameters.Where(p => !string.IsNullOrEmpty(p.Formula)))
                        {
                            try { manager.SetFormula(parameters[p.Name], p.Formula); }
                            catch (Exception ex) { throw new InvalidOperationException("Formula of '" + p.Name + "' was rejected (" + p.Formula + "): " + ex.Message); }
                        }

                        // Cut after the types exist: until a type sets its values, parameter-driven solids can have
                        // zero size and no bounds. In a regular (non-conceptual) family, a void cuts through a geometry combination;
                        // SolidSolidCutUtils only works in projects and conceptual/adaptive families. One
                        // combination per solid keeps each solid's own material, and a void that touches several
                        // solids is copied, because an element can belong to only one combination.
                        if (spec.Solids.Any(s => s.Void))
                        {
                            familyDoc.Regenerate();
                            var voidUsed = new HashSet<int>();
                            for (var s = 0; s < created.Count; s++)
                            {
                                if (spec.Solids[s].Void) continue;
                                var box = created[s].get_BoundingBox(null);
                                var members = new CombinableElementArray();
                                members.Append(created[s]);
                                var voids = new List<int>();
                                for (var v = 0; v < created.Count; v++)
                                {
                                    if (!spec.Solids[v].Void) continue;
                                    var voidBox = created[v].get_BoundingBox(null);
                                    if (box == null || voidBox == null || !Overlaps(box, voidBox)) continue;
                                    var cutter = voidUsed.Add(v) ? created[v]
                                        : (GenericForm)familyDoc.GetElement(ElementTransformUtils.CopyElement(familyDoc, created[v].Id, XYZ.Zero).First());
                                    members.Append(cutter);
                                    voids.Add(v);
                                }
                                if (voids.Count == 0) continue;
                                try
                                {
                                    var combination = familyDoc.CombineElements(members);
                                    cuts.Add(new { solidIndex = s, voidIndexes = voids, combinationId = RevitCompat.GetId(combination.Id) });
                                }
                                catch (Exception ex) { warnings.Add("Solid " + s + " could not be cut by voids " + string.Join(", ", voids) + ": " + ex.Message); }
                            }
                            foreach (var v in spec.Solids.Where(x => x.Void).Select(x => x.Index).Where(v => !voidUsed.Contains(v)))
                                warnings.Add("Void " + v + " does not touch any solid, so it cuts nothing.");
                        }

                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("Revit did not commit the family geometry and parameters; nothing was saved or loaded.");
                    }
                    catch (Exception ex)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        return CommandResult.Fail("Family creation failed; nothing was saved or loaded. " + ex.Message);
                    }
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    familyDoc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = spec.Overwrite });
                }
                catch (Exception ex) { return CommandResult.Fail("The family was built but could not be saved to " + savePath + ": " + ex.Message); }

                long? familyId = null;
                var types = new List<object>();
                if (spec.LoadIntoProject)
                {
                    Family family;
                    try { family = familyDoc.LoadFamily(project, new OverwriteLoadOptions(spec.Overwrite)); }
                    catch (Exception ex) { return CommandResult.Fail("The family was saved to " + savePath + " but could not be loaded into the project: " + ex.Message); }
                    if (family == null)
                    {
                        warnings.Add("The .rfa was saved but Revit did not load it into the project.");
                    }
                    else
                    {
                        familyId = RevitCompat.GetId(family.Id);
                        foreach (var id in family.GetFamilySymbolIds())
                            types.Add(new { typeId = RevitCompat.GetId(id), name = project.GetElement(id)?.Name });
                    }
                }

                return CommandResult.Ok(new
                {
                    created = true,
                    familyName,
                    rfaPath = savePath,
                    template,
                    category = familyDoc.OwnerFamily.FamilyCategory?.Name,
                    loadedIntoProject = familyId.HasValue,
                    familyId,
                    types,
                    parameters = spec.Parameters.Select(p => new { p.Name, p.Type, p.Instance, p.Group, p.Formula }),
                    solids,
                    voidCuts = cuts,
                    materials,
                    warnings
                });
            }
            finally
            {
                if (familyDoc != null)
                {
                    try { familyDoc.Close(false); } catch { /* best effort; never mask the result */ }
                }
            }
        }

        private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static ForgeTypeId SpecFor(string type)
        {
            switch (type)
            {
                case "length": return SpecTypeId.Length;
                case "number": return SpecTypeId.Number;
                case "integer": return SpecTypeId.Int.Integer;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "text": return SpecTypeId.String.Text;
                case "material": return SpecTypeId.Reference.Material;
                case "angle": return SpecTypeId.Angle;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                default: throw new ArgumentException("Unsupported parameter type " + type);
            }
        }

        private static ForgeTypeId GroupFor(string group)
        {
            switch (group)
            {
                case "dimensions": return GroupTypeId.Geometry;
                case "constraints": return GroupTypeId.Constraints;
                case "materials": return GroupTypeId.Materials;
                case "text": return GroupTypeId.Text;
                case "identity": return GroupTypeId.IdentityData;
                case "graphics": return GroupTypeId.Graphics;
                default: return GroupTypeId.Data;
            }
        }

        /// <summary>Maps a plane point (u, v) in mm to model coordinates for the solid's sketch plane.</summary>
        private static XYZ ToModel(string plane, double offsetMm, double u, double v)
        {
            switch (plane)
            {
                case "XZ": return new XYZ(Mm(u), Mm(offsetMm), Mm(v));
                case "YZ": return new XYZ(Mm(offsetMm), Mm(u), Mm(v));
                default: return new XYZ(Mm(u), Mm(v), Mm(offsetMm));
            }
        }

        private static SketchPlane Sketch(Document doc, string plane, double offsetMm)
        {
            var normal = plane == "XZ" ? XYZ.BasisY : plane == "YZ" ? XYZ.BasisX : XYZ.BasisZ;
            return SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, ToModel(plane, offsetMm, 0, 0)));
        }

        private static CurveArrArray Loop(string plane, double offsetMm, FamilySpec.SolidSpec s)
        {
            var loop = new CurveArray();
            if (s.Circle != null)
            {
                var center = ToModel(plane, offsetMm, s.Circle[0], s.Circle[1]);
                var xAxis = (ToModel(plane, offsetMm, 1, 0) - ToModel(plane, offsetMm, 0, 0)).Normalize();
                var yAxis = (ToModel(plane, offsetMm, 0, 1) - ToModel(plane, offsetMm, 0, 0)).Normalize();
                loop.Append(Arc.Create(center, Mm(s.Circle[2]), 0, Math.PI, xAxis, yAxis));
                loop.Append(Arc.Create(center, Mm(s.Circle[2]), Math.PI, 2 * Math.PI, xAxis, yAxis));
            }
            else
            {
                for (var i = 0; i < s.Profile.Count; i++)
                {
                    var a = s.Profile[i]; var b = s.Profile[(i + 1) % s.Profile.Count];
                    loop.Append(Line.CreateBound(ToModel(plane, offsetMm, a[0], a[1]), ToModel(plane, offsetMm, b[0], b[1])));
                }
            }
            var profile = new CurveArrArray();
            profile.Append(loop);
            return profile;
        }

        private static Extrusion CreateExtrusion(Document doc, FamilySpec.SolidSpec s)
        {
            var extrusion = doc.FamilyCreate.NewExtrusion(!s.Void, Loop(s.Plane, s.OffsetMm, s), Sketch(doc, s.Plane, s.OffsetMm), Mm(s.EndMm));
            extrusion.StartOffset = Mm(s.StartMm);
            extrusion.EndOffset = Mm(s.EndMm);
            return extrusion;
        }

        private static Revolution CreateRevolution(Document doc, FamilySpec.SolidSpec s)
        {
            // Profiles are [radius, z] in the XZ plane, revolved around the Z axis.
            var axis = Line.CreateBound(XYZ.Zero, XYZ.BasisZ);
            return doc.FamilyCreate.NewRevolution(!s.Void, Loop("XZ", 0, s), Sketch(doc, "XZ", 0), axis,
                s.StartAngleDeg * Math.PI / 180, s.EndAngleDeg * Math.PI / 180);
        }

        private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b)
            => a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

        /// <summary>Family material by name: reuse it, copy it from the project, or create it.</summary>
        private static ElementId ResolveMaterial(Document familyDoc, Document project, string name, List<object> report)
        {
            Material Find(Document d) => new FilteredElementCollector(d).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            var inFamily = Find(familyDoc);
            if (inFamily != null) { report.Add(new { name, source = "template" }); return inFamily.Id; }
            var inProject = Find(project);
            if (inProject != null)
            {
                var copied = ElementTransformUtils.CopyElements(project, new List<ElementId> { inProject.Id }, familyDoc, Transform.Identity, new CopyPasteOptions());
                if (copied.Count > 0) { report.Add(new { name, source = "copied_from_project" }); return copied.First(); }
            }
            report.Add(new { name, source = "created" });
            return Material.Create(familyDoc, name);
        }

        private static string SetValue(FamilyManager manager, FamilyParameter fp, JToken value, Func<string, ElementId> materialId)
        {
            var spec = fp.Definition.GetDataType();
            try
            {
                if (spec == SpecTypeId.String.Text) { manager.Set(fp, value.ToString()); return null; }
                if (spec == SpecTypeId.Reference.Material) { manager.Set(fp, materialId(value.ToString())); return null; }
                if (spec == SpecTypeId.Boolean.YesNo)
                {
                    var on = value.Type == JTokenType.Boolean ? value.Value<bool>() : value.ToString() == "1" || value.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                    manager.Set(fp, on ? 1 : 0); return null;
                }
                if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return "expected a number, got " + value.Type + ".";
                var number = value.Value<double>();
                if (spec == SpecTypeId.Int.Integer) { manager.Set(fp, (int)Math.Round(number)); return null; }
                if (spec == SpecTypeId.Length) { manager.Set(fp, Mm(number)); return null; }
                if (spec == SpecTypeId.Angle) { manager.Set(fp, number * Math.PI / 180); return null; }
                if (spec == SpecTypeId.Area) { manager.Set(fp, UnitUtils.ConvertToInternalUnits(number, UnitTypeId.SquareMeters)); return null; }
                if (spec == SpecTypeId.Volume) { manager.Set(fp, UnitUtils.ConvertToInternalUnits(number, UnitTypeId.CubicMeters)); return null; }
                manager.Set(fp, number);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private sealed class OverwriteLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            public OverwriteLoadOptions(bool overwrite) { _overwrite = overwrite; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }
        }
    }

    /// <summary>Finds family templates (.rft) under Revit's configured template folder.</summary>
    internal static class FamilyTemplateCatalog
    {
        internal static string Root(Application app)
        {
            try { return app.FamilyTemplatePath; } catch { return null; }
        }

        internal static List<string> All(Application app)
        {
            var root = Root(app);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<string>();
            try { return Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>An absolute .rft path, or a name matched without case or accents: exact first, then unique partial match.</summary>
        internal static string Resolve(Application app, string query, out string error)
        {
            error = null;
            if (Path.IsPathRooted(query))
            {
                if (File.Exists(query) && query.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)) return query;
                error = "Template file not found: " + query;
                return null;
            }
            var all = All(app);
            if (all.Count == 0)
            {
                error = "No family templates were found under Revit's template folder (" + (Root(app) ?? "not set") + "). Pass an absolute .rft path as template.";
                return null;
            }
            var wanted = Fold(Path.GetFileNameWithoutExtension(query));
            var exact = all.Where(p => Fold(Path.GetFileNameWithoutExtension(p)) == wanted).ToList();
            if (exact.Count >= 1) return exact[0];
            var partial = all.Where(p => Fold(Path.GetFileNameWithoutExtension(p)).Contains(wanted)).ToList();
            if (partial.Count == 1) return partial[0];
            error = partial.Count == 0
                ? "No family template matches '" + query + "'. Call revit_list_family_templates to see the available names."
                : "Several family templates match '" + query + "': " + string.Join(", ", partial.Take(15).Select(Path.GetFileNameWithoutExtension)) + ". Use the full name.";
            return null;
        }

        internal static string Fold(string value)
        {
            var decomposed = (value ?? "").Normalize(System.Text.NormalizationForm.FormD);
            var chars = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
            return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Trim();
        }
    }
}
