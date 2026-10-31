using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Snapshot schema identity and the key figures read from a snapshot. Shared by the add-in, which builds
    /// snapshots, and the gateway, which stores, compares and draws them.
    /// </summary>
    internal static class ModelSnapshotSchema
    {
        internal const string Schema = "tbc-revit-mcp/model-snapshot";
        internal const int SchemaVersion = 1;

        /// <summary>The snapshot's key figures, for the tool reply and for the change since the previous snapshot.</summary>
        internal static JObject KeyFigures(JObject snapshot)
        {
            var sections = snapshot["sections"] as JObject ?? new JObject();
            var figures = new JObject();
            if (sections["composition"] is JObject composition) figures["elements_total"] = composition["elements_total"];
            if (sections["health"] is JObject health) figures["warnings_total"] = health["warnings_total"];
            if (sections["networks"] is JObject networks)
            {
                figures["network_length_m"] = networks["length_m"];
                figures["network_clashes"] = networks["clashes_total"];
                figures["open_ends"] = (networks["items"] as JArray ?? new JArray()).Sum(n => n["open_ends"]?.Value<int>("count") ?? 0);
            }
            if (sections["data_quality"] is JObject quality)
                foreach (var check in quality["checks"] as JArray ?? new JArray())
                    figures[(check.Value<string>("parameter") ?? "parameter") + "_filled_pct"] = check["pct"];
            return figures;
        }

        /// <summary>Difference of each numeric key figure against an earlier snapshot's figures.</summary>
        internal static JObject Change(JObject current, JObject previous)
        {
            var change = new JObject();
            if (previous == null) return change;
            foreach (var property in current.Properties())
            {
                var before = previous[property.Name];
                if (IsNumber(property.Value) && IsNumber(before))
                    change[property.Name] = Math.Round(property.Value.Value<double>() - before.Value<double>(), 3, MidpointRounding.AwayFromZero);
            }
            return change;
        }

        private static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
    }
}
