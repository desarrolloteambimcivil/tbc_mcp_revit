using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using static RvtMcp.Plugin.Handlers.TypeStructureSupport;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Edits one railing type's baluster/post placement and non-continuous rails. Every patch is
    /// validated before the transaction starts; the change applies to all railings of the type.
    /// </summary>
    public class SetRailingStructureHandler : IRevitCommand
    {
        public string Name => "set_railing_structure";
        public string Description => "Edit a railing type's balusters/posts (references, offsets, family, spacing) and non-continuous rails (height, offset, name, profile, material).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""typeId"":{""type"":""integer""},""elementId"":{""type"":""integer""},""balusters"":{""type"":""array"",""items"":{""type"":""object""}},""rails"":{""type"":""array"",""items"":{""type"":""object""}}}}";

        private sealed class BalusterTarget
        {
            public string Label;
            public BalusterInfo Info;
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (!TryParse(paramsJson, out var request, out var parseError)) return CommandResult.Fail(parseError);

            var type = ResolveType(doc, request, out var error);
            if (type == null) return CommandResult.Fail(error);

            var balusterPatches = ReadPatchArray(request, "balusters", out error);
            if (balusterPatches == null) return CommandResult.Fail(error);
            var railPatches = ReadPatchArray(request, "rails", out error);
            if (railPatches == null) return CommandResult.Fail(error);
            if (balusterPatches.Count == 0 && railPatches.Count == 0)
                return CommandResult.Fail("Provide at least one patch in balusters or rails.");

            var before = GetRailingStructureHandler.Describe(doc, type, includeInstanceCount: false);
            var applied = new JArray();
            using (var tx = new Transaction(doc, "MCP: Set railing structure"))
            {
                try
                {
                    tx.Start();
                    // Rails first, so renamed rails can be used as baluster references in the same call.
                    foreach (var patch in railPatches)
                    {
                        error = ApplyRailPatch(doc, type, patch, applied);
                        if (error != null) { tx.RollBack(); return CommandResult.Fail(error); }
                    }
                    foreach (var patch in balusterPatches)
                    {
                        error = ApplyBalusterPatch(doc, type, patch, applied);
                        if (error != null) { tx.RollBack(); return CommandResult.Fail(error); }
                    }
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the railing structure change. Transaction status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    return CommandResult.Fail("Failed to set railing structure: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                typeId = RevitCompat.GetId(type.Id),
                typeName = type.Name,
                affectedRailings = CountInstancesOfType(doc, type),
                applied,
                before,
                after = GetRailingStructureHandler.Describe(doc, type, includeInstanceCount: false)
            });
        }

        private static RailingType ResolveType(Document doc, JObject request, out string error)
        {
            error = null;
            var typeId = ReadId(request, "typeId", out error);
            if (error != null) return null;
            var elementId = ReadId(request, "elementId", out error);
            if (error != null) return null;
            if (typeId.HasValue == elementId.HasValue)
            {
                error = "Provide exactly one of typeId (railing type) or elementId (railing instance).";
                return null;
            }
            var problems = new JArray();
            var types = typeId.HasValue
                ? ResolveTypes<RailingType>(doc, new List<long>(), new List<long> { typeId.Value }, problems)
                : ResolveTypes<RailingType>(doc, new List<long> { elementId.Value }, new List<long>(), problems);
            if (types.Count == 1) return types[0];
            error = problems.Count > 0 ? problems[0].Value<string>("error") : "Railing type not found.";
            return null;
        }

        private static string ApplyRailPatch(Document doc, RailingType type, JObject patch, JArray applied)
        {
            var structure = type.RailStructure;
            var count = structure.GetNonContinuousRailCount();
            var index = patch["index"]?.Type == JTokenType.Integer ? patch.Value<int>("index") : -1;
            var name = patch.Value<string>("name");
            if (index < 0 && !string.IsNullOrEmpty(name))
                for (var i = 0; i < count; i++)
                    if (string.Equals(structure.GetNonContinuousRail(i).Name, name, StringComparison.OrdinalIgnoreCase)) index = i;
            if (index < 0 || index >= count)
                return "Rail patch needs a valid index (0-" + (count - 1) + ") or the name of an existing rail.";

            var rail = structure.GetNonContinuousRail(index);
            var label = "rail[" + index + "] " + rail.Name;
            var height = ReadMm(patch, "height_mm", out var error);
            if (error != null) return label + ": " + error;
            var offset = ReadMm(patch, "offset_mm", out error);
            if (error != null) return label + ": " + error;
            var profileId = ReadId(patch, "profile_id", out error);
            if (error != null) return label + ": " + error;
            var materialId = ReadId(patch, "material_id", out error);
            if (error != null) return label + ": " + error;
            var newName = patch.Value<string>("new_name");

            if (height.HasValue)
            {
                if (!rail.IsValidNonContinuousRailHeight(FromMm(height.Value))) return label + ": height_mm " + height + " is not valid for this rail.";
                rail.Height = FromMm(height.Value);
            }
            if (offset.HasValue) rail.Offset = FromMm(offset.Value);
            if (profileId.HasValue)
            {
                var id = RevitCompat.ToElementId(profileId.Value);
                if (!rail.IsValidNonContinuousRailProfile(id)) return label + ": profile_id " + profileId + " is not a valid rail profile.";
                rail.ProfileId = id;
            }
            if (materialId.HasValue)
            {
                var id = RevitCompat.ToElementId(materialId.Value);
                if (!(doc.GetElement(id) is Material) || !rail.IsValidNonContinuousRailMaterial(id))
                    return label + ": material_id " + materialId + " is not a valid material.";
                rail.MaterialId = id;
            }
            if (!string.IsNullOrEmpty(newName))
            {
                if (!rail.IsValidNonContinuousRailName(newName)) return label + ": new_name '" + newName + "' is not valid (names must be unique).";
                rail.Name = newName;
            }
            applied.Add(new JObject { ["target"] = label, ["patch"] = patch });
            return null;
        }

        private static string ApplyBalusterPatch(Document doc, RailingType type, JObject patch, JArray applied)
        {
            var targetSpec = (patch.Value<string>("target") ?? "").Trim().ToLowerInvariant();
            var targets = ResolveBalusterTargets(type, targetSpec);
            if (targets == null)
                return "Baluster patch target must be one of: all, pattern, pattern:<index>, posts, start_post, corner_post, end_post.";

            var baseReference = ResolveReference(type, patch.Value<string>("base_reference"), out var error);
            if (error != null) return error;
            var topReference = ResolveReference(type, patch.Value<string>("top_reference"), out error);
            if (error != null) return error;
            var baseOffset = ReadMm(patch, "base_offset_mm", out error);
            if (error != null) return "base_offset_mm: " + error;
            var topOffset = ReadMm(patch, "top_offset_mm", out error);
            if (error != null) return "top_offset_mm: " + error;
            var spacing = ReadMm(patch, "distance_from_previous_mm", out error);
            if (error != null) return "distance_from_previous_mm: " + error;
            var offset = ReadMm(patch, "offset_mm", out error);
            if (error != null) return "offset_mm: " + error;
            var familyId = ReadId(patch, "baluster_family_id", out error);
            if (error != null) return error;
            ElementId family = null;
            if (familyId.HasValue)
            {
                family = RevitCompat.ToElementId(familyId.Value);
                if (familyId.Value != -1 && !(doc.GetElement(family) is FamilySymbol symbol
                    && symbol.Category != null && symbol.Category.Id == new ElementId(BuiltInCategory.OST_StairsRailingBaluster)))
                    return "baluster_family_id " + familyId + " is not a baluster family type (use -1 for none).";
            }

            foreach (var target in targets)
            {
                if (baseReference != null) target.Info.BaseReferenceName = baseReference;
                if (topReference != null) target.Info.TopReferenceName = topReference;
                if (baseOffset.HasValue) target.Info.BaseOffset = FromMm(baseOffset.Value);
                if (topOffset.HasValue) target.Info.TopOffset = FromMm(topOffset.Value);
                if (spacing.HasValue) target.Info.DistanceFromPreviousOrSpace = FromMm(spacing.Value);
                if (offset.HasValue) target.Info.Offset = FromMm(offset.Value);
                if (family != null) target.Info.BalusterFamilyId = family;
            }
            applied.Add(new JObject { ["target"] = new JArray(targets.Select(t => t.Label)), ["patch"] = patch });
            return null;
        }

        private static List<BalusterTarget> ResolveBalusterTargets(RailingType type, string spec)
        {
            var placement = type.BalusterPlacement;
            var pattern = placement.BalusterPattern;
            var posts = placement.PostPattern;
            var patternTargets = Enumerable.Range(0, pattern.GetBalusterCount())
                .Select(i => new BalusterTarget { Label = "pattern:" + i, Info = pattern.GetBaluster(i) }).ToList();
            var postTargets = new List<BalusterTarget>
            {
                new BalusterTarget { Label = "start_post", Info = posts.StartPost },
                new BalusterTarget { Label = "corner_post", Info = posts.CornerPost },
                new BalusterTarget { Label = "end_post", Info = posts.EndPost }
            };
            switch (spec)
            {
                case "all": return patternTargets.Concat(postTargets).ToList();
                case "pattern": return patternTargets;
                case "posts": return postTargets;
                case "start_post": case "corner_post": case "end_post": return postTargets.Where(t => t.Label == spec).ToList();
            }
            if (spec.StartsWith("pattern:", StringComparison.Ordinal)
                && int.TryParse(spec.Substring("pattern:".Length), out var index) && index >= 0 && index < patternTargets.Count)
                return new List<BalusterTarget> { patternTargets[index] };
            return null;
        }

        /// <summary>Maps host/top_rail tokens to Revit's localized names and checks rail names exist.</summary>
        private static string ResolveReference(RailingType type, string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value)) return null;
            var host = BalusterInfo.GetReferenceNameForHost();
            var topRail = BalusterInfo.GetReferenceNameForTopRail();
            var token = value.Trim();
            if (token.Equals("host", StringComparison.OrdinalIgnoreCase) || token.Equals(host, StringComparison.OrdinalIgnoreCase)) return host;
            if (token.Equals("top_rail", StringComparison.OrdinalIgnoreCase) || token.Equals(topRail, StringComparison.OrdinalIgnoreCase)) return topRail;
            var structure = type.RailStructure;
            var rails = Enumerable.Range(0, structure.GetNonContinuousRailCount()).Select(i => structure.GetNonContinuousRail(i).Name).ToList();
            var match = rails.FirstOrDefault(r => string.Equals(r, token, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            error = "Unknown reference '" + value + "'. Use host, top_rail, or a rail name: " + string.Join(", ", rails) + ".";
            return null;
        }
    }
}
