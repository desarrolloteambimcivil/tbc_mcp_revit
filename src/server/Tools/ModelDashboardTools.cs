using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;

namespace RvtMcp.Server
{
    /// <summary>
    /// Dated model snapshots and the local dashboard drawn from them. Revit is only read; the gateway writes the
    /// snapshot and dashboard files under the product data folder, and the dashboard needs no Revit at all.
    /// </summary>
    [McpServerToolType, Toolset("lint")]
    public class ModelDashboardTools
    {
        private const int SnapshotTimeoutSeconds = 180;

        /// <summary>Tests point the snapshot and dashboard folders at a temporary root.</summary>
        internal static string DataRootOverride;

        private static SnapshotStore Store() => new SnapshotStore(DataRootOverride == null ? null : Path.Combine(DataRootOverride, "snapshots"));

        private static string DashboardFolder()
            => DataRootOverride == null ? Path.Combine(Plugin.ProductIdentity.DataRoot(), "dashboards") : Path.Combine(DataRootOverride, "dashboards");

        [McpServerTool(Name = "revit_get_model_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Read the active model's snapshot figures without saving them: the same sections and parameters as revit_create_model_snapshot "
            + "(composition, health, networks grouped by networkParameter, data_quality). Use it to check figures; use revit_create_model_snapshot to keep a dated copy for the dashboard.")]
        public static async Task<string> GetModelSnapshot(string[] sections = null, string networkParameter = "Comments", double clearanceMm = 0, int sampleLimit = 50)
            => await ToolGateway.CallAsync("get_model_snapshot",
                new { sections, network_parameter = networkParameter, clearance_mm = clearanceMm, sample_limit = sampleLimit }, SnapshotTimeoutSeconds);

        [McpServerTool(Name = "revit_create_model_snapshot", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
            "Save a dated snapshot of the active model's figures as a new local JSON file, to draw a dashboard and track progress over time. The model is only read. "
            + "Sections (default all): composition (elements by category, level and workset); health (warnings by type, in-place families, imported and linked CAD, views not on sheets, sheets, schedules, file size); "
            + "networks (grouped by the value of networkParameter, default Comments: pipes and conduits by diameter with length, fittings, accessories, structures, open ends, clashes per pair of networks at clearanceMm); "
            + "data_quality (networkParameter filled on pipes, conduits, fittings and accessories). label is a free note, e.g. 'end of stormwater session'. "
            + "Returns the key figures, their change since the previous snapshot of the same model, and the file path. Draw the history with revit_create_model_dashboard.")]
        public static async Task<string> CreateModelSnapshot(string label = "", string[] sections = null, string networkParameter = "Comments",
            double clearanceMm = 0, int sampleLimit = 50)
        {
            try
            {
                var data = await ToolGateway.SendToRevit("get_model_snapshot",
                    new { sections, network_parameter = networkParameter, clearance_mm = clearanceMm, sample_limit = sampleLimit }, SnapshotTimeoutSeconds);
                if (data.Value<bool?>("success") == false || ToolGateway.IsPendingOrUnknown(data))
                    return data.ToString(Formatting.Indented);

                // Call metadata (_target, _operation…) goes back to the agent, not into the stored snapshot.
                var metadata = data.Properties().Where(p => p.Name.StartsWith("_", StringComparison.Ordinal)).ToList();
                foreach (var p in metadata) p.Remove();

                var captured = DateTime.UtcNow;
                data["captured_at_utc"] = captured.ToString("o");
                data["label"] = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
                data["producer"] = new JObject { ["gateway"] = GatewayRuntime.Identity.Value<string>("assembly_informational_version") };

                var store = Store();
                var model = data["model"] as JObject ?? new JObject();
                var key = SnapshotStore.ModelKey(model.Value<string>("title"), model.Value<string>("path_hash"));
                var history = store.List(key);
                var previous = history.LastOrDefault();
                var path = store.Save(data, captured);

                var figures = ModelSnapshotSchema.KeyFigures(data);
                var reply = new JObject
                {
                    ["saved"] = path,
                    ["model"] = data["model"],
                    ["captured_at_utc"] = data["captured_at_utc"],
                    ["label"] = data["label"],
                    ["key_figures"] = figures,
                    ["previous_snapshot"] = previous == null ? null : previous.CapturedUtc.ToString("o"),
                    ["change_since_previous"] = previous == null ? new JObject() : ModelSnapshotSchema.Change(figures, ModelSnapshotSchema.KeyFigures(previous.Snapshot)),
                    ["snapshots_of_model"] = history.Count + 1,
                    ["truncated"] = data["truncated"],
                    ["timings_ms"] = data["timings_ms"],
                    ["next"] = "revit_create_model_dashboard with model='" + key + "' draws this model's snapshots.",
                };
                foreach (var p in metadata) reply[p.Name] = p.Value;
                return reply.ToString(Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_model_snapshots", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "List the model snapshots stored on this machine. Without model: every model with snapshots (key, title, count, latest). "
            + "With model (key or exact title): each snapshot's time, label and key figures, oldest first. Needs no Revit.")]
        public static string ListModelSnapshots(string model = "")
        {
            try
            {
                var store = Store();
                if (string.IsNullOrWhiteSpace(model))
                    return new JObject
                    {
                        ["folder"] = store.Root,
                        ["models"] = new JArray(store.ListModels().Select(m => new JObject
                        {
                            ["key"] = m.Key, ["title"] = m.Title, ["snapshots"] = m.Count, ["latest_utc"] = m.LatestUtc.ToString("o"),
                        })),
                    }.ToString(Formatting.Indented);

                var key = store.ResolveModel(model, out var error);
                if (key == null) return "Error: " + error;
                var skipped = new System.Collections.Generic.List<string>();
                var entries = store.List(key, skipped);
                return new JObject
                {
                    ["model"] = key,
                    ["snapshots"] = new JArray(entries.Select(e => new JObject
                    {
                        ["file"] = e.Path, ["captured_at_utc"] = e.CapturedUtc.ToString("o"), ["label"] = e.Label,
                        ["key_figures"] = ModelSnapshotSchema.KeyFigures(e.Snapshot),
                    })),
                    ["skipped_files"] = new JArray(skipped),
                }.ToString(Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_model_dashboard", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
            "Write a self-contained HTML dashboard of one model's snapshots as a new local file: key figures with their change, networks (length by diameter, open ends, clashes between networks), "
            + "health (warnings by type), composition, data quality, progress over time when there are two or more snapshots, and (internal audience) the pending element ids. "
            + "model: key or exact title; default the most recently captured. audience=internal (default) or client, which leaves out internal sections, ids and paths. "
            + "from/to (ISO dates) limit the history. findings is optional commentary shown apart as the agent's notes, never as data. Works offline; needs no Revit. Returns the file path to open.")]
        public static string CreateModelDashboard(string model = "", string audience = "internal", string from = "", string to = "", string findings = "")
        {
            try
            {
                var store = Store();
                var key = store.ResolveModel(model, out var error);
                if (key == null) return "Error: " + error;
                var result = DashboardBuilder.Write(store, key, audience, from, to, findings, DashboardFolder(), DateTime.UtcNow);
                return result.ToString(Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
