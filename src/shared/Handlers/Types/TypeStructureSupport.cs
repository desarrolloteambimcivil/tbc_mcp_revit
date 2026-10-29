using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Shared input parsing and DTO helpers for the railing and compound-structure type handlers.</summary>
    internal static class TypeStructureSupport
    {
        internal static double ToMm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 3);
        internal static double FromMm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        internal static double ToDegrees(double radians) => Math.Round(radians * 180.0 / Math.PI, 3);

        internal static bool TryParse(string paramsJson, out JObject request, out string error)
        {
            error = null;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                return true;
            }
            catch (JsonException ex)
            {
                request = null;
                error = "Parameters must be a JSON object: " + ex.Message;
                return false;
            }
        }

        internal static bool TryReadIdArray(JObject request, string propertyName, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;
            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Array)
            {
                error = propertyName + " must be an array of integer IDs.";
                return false;
            }
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.Integer)
                {
                    error = propertyName + " must contain only integer IDs.";
                    return false;
                }
                var id = item.Value<long>();
                if (!ids.Contains(id)) ids.Add(id);
            }
            return true;
        }

        /// <summary>
        /// Resolves explicit type IDs plus the types of the given elements, keeping request order.
        /// IDs that are missing or of another kind are reported in <paramref name="problems"/>.
        /// </summary>
        internal static List<T> ResolveTypes<T>(Document doc, List<long> elementIds, List<long> typeIds, JArray problems)
            where T : ElementType
        {
            var resolved = new List<T>();
            var seen = new HashSet<long>();
            void Add(long typeId, string source, long requestedId)
            {
                var type = doc.GetElement(RevitCompat.ToElementId(typeId));
                if (type is T typed)
                {
                    if (seen.Add(typeId)) resolved.Add(typed);
                    return;
                }
                problems.Add(new JObject
                {
                    ["source"] = source,
                    ["id"] = requestedId,
                    ["error"] = type == null ? "Type not found." : "Type is " + type.GetType().Name + ", not " + typeof(T).Name + "."
                });
            }

            foreach (var elementId in elementIds)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                var typeId = element?.GetTypeId();
                if (element == null || typeId == null || typeId == ElementId.InvalidElementId)
                {
                    problems.Add(new JObject
                    {
                        ["source"] = "elementIds", ["id"] = elementId,
                        ["error"] = element == null ? "Element not found." : "Element has no type."
                    });
                    continue;
                }
                Add(RevitCompat.GetId(typeId), "elementIds", elementId);
            }
            foreach (var typeId in typeIds) Add(typeId, "typeIds", typeId);
            return resolved;
        }

        internal static JObject Ref(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            var element = doc.GetElement(id);
            var symbol = element as FamilySymbol;
            return new JObject
            {
                ["id"] = RevitCompat.GetId(id),
                ["name"] = symbol != null ? symbol.FamilyName + " : " + symbol.Name : element?.Name
            };
        }

        internal static int CountInstancesOfType(Document doc, ElementType type)
        {
            if (type.Category == null) return 0;
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategoryId(type.Category.Id)
                .Count(e => e.GetTypeId() == type.Id);
        }

        internal static double? ReadMm(JObject patch, string property, out string error)
        {
            error = null;
            var token = patch[property];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = property + " must be a number in millimetres.";
                return null;
            }
            return token.Value<double>();
        }

        internal static long? ReadId(JObject patch, string property, out string error)
        {
            error = null;
            var token = patch[property];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer)
            {
                error = property + " must be an integer element ID.";
                return null;
            }
            return token.Value<long>();
        }

        internal static List<JObject> ReadPatchArray(JObject request, string property, out string error)
        {
            error = null;
            var token = request[property];
            if (token == null || token.Type == JTokenType.Null) return new List<JObject>();
            if (token.Type != JTokenType.Array || ((JArray)token).Any(item => item.Type != JTokenType.Object))
            {
                error = property + " must be an array of objects.";
                return null;
            }
            return ((JArray)token).Cast<JObject>().ToList();
        }
    }
}
