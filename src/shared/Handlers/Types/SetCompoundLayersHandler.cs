using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using static RvtMcp.Plugin.Handlers.TypeStructureSupport;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Edits layers of one wall/floor/roof/ceiling type in place. Uses per-layer setters and DeleteLayer,
    /// never SetLayers, because SetLayers resets core/shell boundaries, the variable and the structural layer.
    /// </summary>
    public class SetCompoundLayersHandler : IRevitCommand
    {
        public string Name => "set_compound_layers";
        public string Description => "Edit layers (width mm, material, function) or delete layers of one wall/floor/roof/ceiling type; validated before commit.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""typeId"":{""type"":""integer""},""elementId"":{""type"":""integer""},""layers"":{""type"":""array"",""items"":{""type"":""object""}},""delete_indices"":{""type"":""array"",""items"":{""type"":""integer""}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (!TryParse(paramsJson, out var request, out var parseError)) return CommandResult.Fail(parseError);

            var typeId = ReadId(request, "typeId", out var error);
            if (error != null) return CommandResult.Fail(error);
            var elementId = ReadId(request, "elementId", out error);
            if (error != null) return CommandResult.Fail(error);
            if (typeId.HasValue == elementId.HasValue)
                return CommandResult.Fail("Provide exactly one of typeId or elementId.");
            var problems = new JArray();
            var types = ResolveTypes<HostObjAttributes>(doc,
                elementId.HasValue ? new List<long> { elementId.Value } : new List<long>(),
                typeId.HasValue ? new List<long> { typeId.Value } : new List<long>(), problems);
            if (types.Count != 1)
                return CommandResult.Fail(problems.Count > 0 ? problems[0].Value<string>("error") : "Type not found.");
            var type = types[0];

            var patches = ReadPatchArray(request, "layers", out error);
            if (patches == null) return CommandResult.Fail(error);
            if (!TryReadIdArray(request, "delete_indices", out var deleteIndices, out error)) return CommandResult.Fail(error);
            if (patches.Count == 0 && deleteIndices.Count == 0)
                return CommandResult.Fail("Provide layer patches in layers and/or indices in delete_indices.");

            var structure = type.GetCompoundStructure();
            if (structure == null) return CommandResult.Fail("This type has no compound structure (for example stacked or curtain walls).");

            // Indices in both lists refer to the structure as it was before this call.
            var layerCount = structure.LayerCount;
            foreach (var patch in patches)
            {
                error = ApplyPatch(doc, structure, layerCount, patch);
                if (error != null) return CommandResult.Fail(error);
            }
            foreach (var index in deleteIndices.OrderByDescending(i => i))
            {
                if (index < 0 || index >= layerCount) return CommandResult.Fail("delete_indices: " + index + " is out of range (0-" + (layerCount - 1) + ").");
                if (!structure.DeleteLayer((int)index)) return CommandResult.Fail("Revit refused to delete layer " + index + " (it may own a vertical region).");
            }

            if (!structure.IsValid(doc, out IDictionary<int, CompoundStructureError> layerErrors, out IDictionary<int, int> twoLayerErrors))
            {
                return CommandResult.Fail("The edited structure is not valid; nothing was changed. "
                    + string.Join("; ", layerErrors.Select(e => (e.Key < 0 ? "structure" : "layer " + e.Key) + ": " + e.Value))
                    + (twoLayerErrors.Count > 0 ? "; layer pairs: " + string.Join(", ", twoLayerErrors.Select(p => p.Key + "/" + p.Value)) : ""));
            }

            var before = GetCompoundStructureHandler.Describe(doc, type, includeInstanceCount: false);
            using (var tx = new Transaction(doc, "MCP: Set compound layers"))
            {
                try
                {
                    tx.Start();
                    type.SetCompoundStructure(structure);
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the layer change. Transaction status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    return CommandResult.Fail("Failed to set compound layers: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                typeId = RevitCompat.GetId(type.Id),
                typeName = type.Name,
                affectedInstances = CountInstancesOfType(doc, type),
                before,
                after = GetCompoundStructureHandler.Describe(doc, type, includeInstanceCount: false)
            });
        }

        private static string ApplyPatch(Document doc, CompoundStructure structure, int layerCount, JObject patch)
        {
            if (patch["index"]?.Type != JTokenType.Integer) return "Each layer patch needs an integer index.";
            var index = patch.Value<int>("index");
            if (index < 0 || index >= layerCount) return "Layer index " + index + " is out of range (0-" + (layerCount - 1) + ").";
            var label = "layer " + index;

            var width = ReadMm(patch, "width_mm", out var error);
            if (error != null) return label + ": " + error;
            if (width.HasValue)
            {
                if (width.Value < 0) return label + ": width_mm cannot be negative.";
                // Only growing a zero-width layer can be refused (it would need a new rectangular region).
                if (width.Value > 0 && structure.GetLayerWidth(index) == 0 && !structure.CanLayerWidthBeNonZero(index))
                    return label + ": this zero-width layer cannot get a width in this structure.";
                structure.SetLayerWidth(index, FromMm(width.Value));
            }

            var functionName = patch.Value<string>("function");
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                if (!Enum.TryParse(functionName.Trim(), true, out MaterialFunctionAssignment function) || !Enum.IsDefined(typeof(MaterialFunctionAssignment), function))
                    return label + ": function must be one of " + string.Join(", ", Enum.GetNames(typeof(MaterialFunctionAssignment))) + ".";
                structure.SetLayerFunction(index, function);
            }

            var materialId = ReadId(patch, "material_id", out error);
            if (error != null) return label + ": " + error;
            var materialName = patch.Value<string>("material_name");
            if (materialId.HasValue || !string.IsNullOrWhiteSpace(materialName))
            {
                ElementId id;
                if (materialId.HasValue)
                {
                    id = RevitCompat.ToElementId(materialId.Value);
                    // SetMaterialId does not verify the element; -1 means "by category".
                    if (materialId.Value != -1 && !(doc.GetElement(id) is Material))
                        return label + ": material_id " + materialId + " is not a material (use -1 for by category).";
                }
                else
                {
                    var material = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                        .FirstOrDefault(m => string.Equals(m.Name, materialName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (material == null) return label + ": no material named '" + materialName + "'.";
                    id = material.Id;
                }
                structure.SetMaterialId(index, id);
            }
            return null;
        }
    }
}
