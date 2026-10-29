using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using static RvtMcp.Plugin.Handlers.TypeStructureSupport;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Reads the railing-type definition that is only reachable through the API: baluster pattern,
    /// posts, non-continuous rails, top rail and handrails, plus the localized reference names.
    /// </summary>
    public class GetRailingStructureHandler : IRevitCommand
    {
        public string Name => "get_railing_structure";
        public string Description => "Read railing type structure: baluster pattern, posts, non-continuous rails, top rail and handrails (mm).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (!TryParse(paramsJson, out var request, out var parseError)) return CommandResult.Fail(parseError);
            if (!TryReadIdArray(request, "elementIds", out var elementIds, out var error)
                || !TryReadIdArray(request, "typeIds", out var typeIds, out error))
                return CommandResult.Fail(error);
            if (elementIds.Count == 0 && typeIds.Count == 0)
                return CommandResult.Fail("Provide railing element IDs in elementIds or railing type IDs in typeIds.");

            var problems = new JArray();
            var types = ResolveTypes<RailingType>(doc, elementIds, typeIds, problems);
            return CommandResult.Ok(new
            {
                count = types.Count,
                referenceNames = new
                {
                    host = BalusterInfo.GetReferenceNameForHost(),
                    topRail = BalusterInfo.GetReferenceNameForTopRail()
                },
                types = types.Select(t => Describe(doc, t, includeInstanceCount: true)).ToList(),
                problems
            });
        }

        internal static JObject Describe(Document doc, RailingType type, bool includeInstanceCount)
        {
            var result = new JObject
            {
                ["typeId"] = RevitCompat.GetId(type.Id),
                ["typeName"] = type.Name,
                ["topRail"] = type.TopRailType == null || type.TopRailType == ElementId.InvalidElementId ? null : new JObject
                {
                    ["type"] = Ref(doc, type.TopRailType),
                    ["heightMm"] = ToMm(type.TopRailHeight)
                },
                ["primaryHandrail"] = Handrail(doc, type, primary: true),
                ["secondaryHandrail"] = Handrail(doc, type, primary: false)
            };
            if (includeInstanceCount) result["instanceCount"] = CountInstancesOfType(doc, type);

            var rails = new JArray();
            var structure = type.RailStructure;
            for (var i = 0; i < structure.GetNonContinuousRailCount(); i++)
            {
                var rail = structure.GetNonContinuousRail(i);
                rails.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = rail.Name,
                    ["heightMm"] = ToMm(rail.Height),
                    ["offsetMm"] = ToMm(rail.Offset),
                    ["profile"] = Ref(doc, rail.ProfileId),
                    ["material"] = Ref(doc, rail.MaterialId)
                });
            }
            result["rails"] = rails;

            var placement = type.BalusterPlacement;
            var pattern = placement.BalusterPattern;
            var balusters = new JArray();
            for (var i = 0; i < pattern.GetBalusterCount(); i++)
            {
                var baluster = Baluster(doc, pattern.GetBaluster(i));
                baluster.AddFirst(new JProperty("index", i));
                balusters.Add(baluster);
            }
            result["balusterPattern"] = new JObject
            {
                ["lengthMm"] = ToMm(pattern.Length),
                ["breakPattern"] = pattern.BreakPattern.ToString(),
                ["distributionJustification"] = pattern.DistributionJustification.ToString(),
                ["patternAngleDegrees"] = ToDegrees(pattern.PatternAngle),
                ["endSpaceMm"] = ToMm(pattern.EndSpace),
                ["excessLengthFill"] = Ref(doc, pattern.ExcessLengthFillBalusterId),
                ["excessLengthFillSpacingMm"] = ToMm(pattern.ExcessLengthFillSpacing),
                ["balusters"] = balusters
            };

            var posts = placement.PostPattern;
            result["posts"] = new JObject
            {
                ["cornerPostCondition"] = posts.CornerPostCondition.ToString(),
                ["cornerPostAngleDegrees"] = ToDegrees(posts.CornerPostAngle),
                ["start"] = Baluster(doc, posts.StartPost),
                ["corner"] = Baluster(doc, posts.CornerPost),
                ["end"] = Baluster(doc, posts.EndPost)
            };
            result["balusterPerTread"] = new JObject
            {
                ["use"] = placement.UseBalusterPerTreadOnStairs,
                ["number"] = placement.BalusterPerTreadNumber,
                ["family"] = Ref(doc, placement.BalusterPerTreadFamilyId)
            };

            // Revit trims balusters where they pass through a non-continuous rail's profile.
            var centredRails = rails.Where(r => Math.Abs(r.Value<double>("offsetMm")) < 0.5).Select(r => r.Value<string>("name")).ToList();
            if (centredRails.Count > 0 && (balusters.Any(b => b["family"] != null && b["family"].Type != JTokenType.Null)))
                result["notes"] = new JArray(
                    "Rails on the baluster axis (offsetMm 0): " + string.Join(", ", centredRails) + ". Revit trims balusters where they pass through a rail profile; "
                    + "if posts look cut, offset that rail sideways by at least half the post width plus the profile thickness.");
            return result;
        }

        // Height, offset and position throw when the railing type has no such handrail, so read them only after the type check.
        private static JObject Handrail(Document doc, RailingType type, bool primary)
        {
            var typeId = primary ? type.PrimaryHandrailType : type.SecondaryHandrailType;
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return new JObject
            {
                ["type"] = Ref(doc, typeId),
                ["heightMm"] = ToMm(primary ? type.PrimaryHandrailHeight : type.SecondaryHandrailHeight),
                ["lateralOffsetMm"] = ToMm(primary ? type.PrimaryHandrailLateralOffset : type.SecondaryHandrailLateralOffset),
                ["position"] = (primary ? type.PrimaryHandRailPosition : type.SecondaryHandRailPosition).ToString()
            };
        }

        private static JObject Baluster(Document doc, BalusterInfo info)
        {
            if (info == null) return null;
            return new JObject
            {
                ["name"] = info.Name,
                ["family"] = Ref(doc, info.BalusterFamilyId),
                ["baseReference"] = info.BaseReferenceName,
                ["baseOffsetMm"] = ToMm(info.BaseOffset),
                ["topReference"] = info.TopReferenceName,
                ["topOffsetMm"] = ToMm(info.TopOffset),
                ["distanceFromPreviousMm"] = ToMm(info.DistanceFromPreviousOrSpace),
                ["offsetMm"] = ToMm(info.Offset)
            };
        }
    }
}
