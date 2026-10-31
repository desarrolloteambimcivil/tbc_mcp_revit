using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;

namespace RvtMcp.Server
{
    /// <summary>
    /// Writes the self-contained dashboard: the embedded HTML template with one model's snapshots inlined as
    /// JSON. The client audience keeps only client sections and drops element ids and file paths.
    /// </summary>
    internal static class DashboardBuilder
    {
        private const string DataPlaceholder = "__TBC_DATA__";
        private const string LogoPlaceholder = "__TBC_LOGO__";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static JObject Write(SnapshotStore store, string modelKey, string audience, string from, string to, string findings,
            string folder, DateTime nowUtc)
        {
            audience = string.IsNullOrWhiteSpace(audience) ? "internal" : audience.Trim().ToLowerInvariant();
            if (audience != "internal" && audience != "client") throw new ArgumentException("audience must be internal or client.");
            var fromUtc = ParseDate(from, nameof(from));
            var toUtc = ParseDate(to, nameof(to));
            var skipped = new List<string>();
            var entries = store.List(modelKey, skipped)
                .Where(e => (fromUtc == null || e.CapturedUtc >= fromUtc) && (toUtc == null || e.CapturedUtc <= toUtc.Value.AddDays(toUtc.Value.TimeOfDay == TimeSpan.Zero ? 1 : 0)))
                .ToList();
            if (entries.Count == 0) throw new InvalidOperationException("No snapshots of " + modelKey + " in that date range.");

            var data = Data(entries, audience, findings, nowUtc);
            var html = Fill(Template(), data, LogoDataUri());
            Directory.CreateDirectory(folder);
            var stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(folder, modelKey + "-" + (audience == "client" ? "client-" : "") + stamp + ".html");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, modelKey + "-" + (audience == "client" ? "client-" : "") + stamp + "-" + n + ".html");
            File.WriteAllText(path + ".tmp", html, Utf8);
            File.Move(path + ".tmp", path);

            return new JObject
            {
                ["saved"] = path,
                ["model"] = modelKey,
                ["audience"] = audience,
                ["snapshots_used"] = entries.Count,
                ["from_utc"] = entries[0].CapturedUtc.ToString("o"),
                ["to_utc"] = entries[entries.Count - 1].CapturedUtc.ToString("o"),
                ["skipped_files"] = new JArray(skipped),
                ["next"] = "Open the file in a browser; it works offline.",
            };
        }

        /// <summary>The JSON the template reads: model, audience, findings and the snapshots oldest first.</summary>
        internal static JObject Data(IList<SnapshotEntry> entries, string audience, string findings, DateTime nowUtc)
        {
            var latest = entries[entries.Count - 1].Snapshot;
            var model = (JObject)(latest["model"] as JObject ?? new JObject()).DeepClone();
            if (audience == "client") { model.Remove("path_hash"); model.Remove("key"); }
            var snapshots = new JArray();
            foreach (var entry in entries)
            {
                var sections = (JObject)(entry.Snapshot["sections"] as JObject ?? new JObject()).DeepClone();
                if (audience == "client") ForClient(sections);
                var view = new JObject { ["sections"] = sections };
                view["captured_at_utc"] = entry.CapturedUtc.ToString("o");
                view["label"] = entry.Label;
                view["key_figures"] = ModelSnapshotSchema.KeyFigures(view);
                view["truncated"] = entry.Snapshot["truncated"]?.DeepClone();
                if (audience == "internal") view["parameters"] = entry.Snapshot["parameters"]?.DeepClone();
                snapshots.Add(view);
            }
            return new JObject
            {
                ["generated_at_utc"] = nowUtc.ToString("o"),
                ["audience"] = audience,
                ["product_version"] = GatewayRuntime.Identity.Value<string>("assembly_informational_version")?.Split('+')[0],
                ["model"] = model,
                ["findings"] = string.IsNullOrWhiteSpace(findings) ? null : findings.Trim(),
                ["snapshots"] = snapshots,
            };
        }

        /// <summary>Client view: internal sections removed, and no element ids anywhere.</summary>
        internal static void ForClient(JObject sections)
        {
            foreach (var p in sections.Properties().ToList())
                if ((p.Value as JObject)?.Value<string>("audience") != "client") p.Remove();
            foreach (var token in sections.Descendants().OfType<JProperty>().ToList())
                if (token.Name == "sample_ids" || token.Name == "sample_pairs" || token.Name == "missing_sample_ids") token.Remove();
        }

        /// <summary>
        /// Puts the data inside the template's JSON script element. "&lt;/" and "&lt;!--" are escaped so no value
        /// (a model title, a label, the findings) can close the element or start markup.
        /// </summary>
        internal static string Fill(string template, JObject data, string logo)
        {
            if (!template.Contains(DataPlaceholder)) throw new InvalidOperationException("Dashboard template has no data placeholder.");
            var json = data.ToString(Formatting.None).Replace("</", "<\\/").Replace("<!--", "<\\u0021--");
            return template.Replace(LogoPlaceholder, logo ?? "").Replace(DataPlaceholder, json);
        }

        internal static string Template()
        {
            using var stream = typeof(DashboardBuilder).Assembly.GetManifestResourceStream("RvtMcp.Dashboard.template.html")
                ?? throw new InvalidOperationException("Dashboard template resource is missing.");
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd();
        }

        private static string LogoDataUri()
        {
            using var stream = typeof(DashboardBuilder).Assembly.GetManifestResourceStream("RvtMcp.Dashboard.logo.png");
            if (stream == null) return "";
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
        }

        private static DateTime? ParseDate(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
            throw new ArgumentException(name + " must be an ISO date such as 2026-11-01.");
        }
    }
}
