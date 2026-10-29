using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using static RvtMcp.Plugin.Handlers.TypeStructureSupport;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Reads the layer structure of wall, floor, roof and ceiling types (HostObjAttributes).</summary>
    public class GetCompoundStructureHandler : IRevitCommand
    {
        public string Name => "get_compound_structure";
        public string Description => "Read compound layer structure (function, width mm, material, core/shell, wrapping) of wall/floor/roof/ceiling types.";
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
                return CommandResult.Fail("Provide wall/floor/roof/ceiling element IDs in elementIds or their type IDs in typeIds.");

            var problems = new JArray();
            var types = ResolveTypes<HostObjAttributes>(doc, elementIds, typeIds, problems);
            return CommandResult.Ok(new
            {
                count = types.Count,
                types = types.Select(t => Describe(doc, t, includeInstanceCount: true)).ToList(),
                problems
            });
        }

        internal static JObject Describe(Document doc, HostObjAttributes type, bool includeInstanceCount)
        {
            var result = new JObject
            {
                ["typeId"] = RevitCompat.GetId(type.Id),
                ["typeName"] = type.Name,
                ["kind"] = type.GetType().Name,
                ["category"] = type.Category?.Name
            };
            if (includeInstanceCount) result["instanceCount"] = CountInstancesOfType(doc, type);

            var structure = type.GetCompoundStructure();
            if (structure == null)
            {
                result["hasCompoundStructure"] = false;
                result["notes"] = new JArray("This type has no compound structure (for example stacked or curtain walls).");
                return result;
            }

            var layers = new JArray();
            var layerList = structure.GetLayers();
            for (var i = 0; i < layerList.Count; i++)
            {
                var layer = layerList[i];
                layers.Add(new JObject
                {
                    ["index"] = i,
                    ["function"] = layer.Function.ToString(),
                    ["widthMm"] = ToMm(layer.Width),
                    ["material"] = Ref(doc, layer.MaterialId),
                    ["isCore"] = structure.IsCoreLayer(i),
                    ["participatesInWrapping"] = structure.ParticipatesInWrapping(i),
                    ["isStructuralDeck"] = structure.IsStructuralDeck(i)
                });
            }

            result["hasCompoundStructure"] = true;
            result["totalWidthMm"] = ToMm(structure.GetWidth());
            result["layerCount"] = structure.LayerCount;
            result["isVerticallyCompound"] = structure.IsVerticallyCompound;
            result["exteriorShellLayers"] = structure.GetNumberOfShellLayers(ShellLayerType.Exterior);
            result["interiorShellLayers"] = structure.GetNumberOfShellLayers(ShellLayerType.Interior);
            result["firstCoreLayerIndex"] = structure.GetFirstCoreLayerIndex();
            result["lastCoreLayerIndex"] = structure.GetLastCoreLayerIndex();
            result["structuralMaterialIndex"] = structure.StructuralMaterialIndex;
            result["variableLayerIndex"] = structure.VariableLayerIndex;
            result["openingWrapping"] = structure.OpeningWrapping.ToString();
            result["endCap"] = structure.EndCap.ToString();
            result["layerOrder"] = "Index 0 is the exterior face of walls and the top of floors, roofs and ceilings.";
            result["layers"] = layers;
            return result;
        }
    }
}
