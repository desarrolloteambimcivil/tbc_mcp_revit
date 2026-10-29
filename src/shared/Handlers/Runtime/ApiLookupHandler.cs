using System;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Looks up Revit API types and members in the assemblies of the running Revit, so send_code and
    /// baked tools use member names that exist in this Revit year instead of guessed ones.
    /// </summary>
    public class ApiLookupHandler : IRevitCommand
    {
        private static readonly string[] ApiAssemblies = { "RevitAPI", "RevitAPIUI" };

        public string Name => "api_lookup";
        public string Description => "Look up Revit API types/members (signatures, obsolete flags, XML-doc summaries) in the running Revit.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""description"":""Type name, full type name, or Type.MemberPrefix""},""include_inherited"":{""type"":""boolean"",""default"":false},""max_members"":{""type"":""integer"",""default"":80}},""required"":[""query""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var query = request.Value<string>("query");
            if (string.IsNullOrWhiteSpace(query))
                return CommandResult.Fail("query is required, e.g. 'BalusterPattern', 'Autodesk.Revit.DB.View3D' or 'CompoundStructure.GetLayer'.");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && ApiAssemblies.Contains(a.GetName().Name, StringComparer.OrdinalIgnoreCase));
            var result = RevitApiReference.Lookup(assemblies, query,
                request.Value<bool?>("include_inherited") ?? false,
                request.Value<int?>("max_members") ?? RevitApiReference.DefaultMaxMembers);
            result["revit_version"] = app?.Application?.VersionNumber;
            return CommandResult.Ok(result);
        }
    }
}
