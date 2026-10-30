using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Lists family templates (.rft) under Revit's template folder, for create_family.</summary>
    public class ListFamilyTemplatesHandler : IRevitCommand
    {
        public string Name => "list_family_templates";
        public string Description => "List family templates (.rft) in Revit's family template folder, optionally filtered by name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""query"":{""type"":""string""},""max_results"":{""type"":""integer"",""default"":50}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var query = FamilyTemplateCatalog.Fold(request.Value<string>("query"));
            var max = Math.Max(1, Math.Min(request.Value<int?>("max_results") ?? 50, 500));
            var root = FamilyTemplateCatalog.Root(app.Application);
            var matches = FamilyTemplateCatalog.All(app.Application)
                .Where(p => query.Length == 0 || FamilyTemplateCatalog.Fold(Path.GetFileNameWithoutExtension(p)).Contains(query))
                .ToList();
            return CommandResult.Ok(new
            {
                templateFolder = root,
                total = matches.Count,
                truncated = matches.Count > max,
                templates = matches.Take(max).Select(p => new
                {
                    name = Path.GetFileNameWithoutExtension(p),
                    relativePath = root != null && p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length).TrimStart('\\', '/') : p,
                    path = p
                })
            });
        }
    }
}
