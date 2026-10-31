using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    /// <summary>One stored snapshot: its file and the fields the list and the dashboard need.</summary>
    internal sealed class SnapshotEntry
    {
        public string Path = "";
        public DateTime CapturedUtc;
        public string Label;
        public JObject Snapshot = new JObject();
    }

    /// <summary>
    /// Model snapshots on this machine: snapshots\&lt;model key&gt;\&lt;yyyyMMdd-HHmmss&gt;.json under the product
    /// data folder. They are the user's history, so nothing here ever deletes a file.
    /// </summary>
    internal sealed class SnapshotStore
    {
        private const string FileTimeFormat = "yyyyMMdd-HHmmss";
        private static readonly Regex FileName = new Regex(@"^(?<time>\d{8}-\d{6})(-(?<n>\d+))?\.json$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public SnapshotStore(string root = null)
        {
            Root = System.IO.Path.GetFullPath(root ?? System.IO.Path.Combine(ProductIdentity.DataRoot(), "snapshots"));
        }

        public string Root { get; }

        /// <summary>Folder-safe model title plus 8 characters of the path hash, so two copies with one title stay apart.</summary>
        public static string ModelKey(string title, string pathHash)
        {
            var name = (title ?? "").Trim();
            if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            name = Regex.Replace(name, @"[^\p{L}\p{N}._-]+", "-").Trim('-', '.');
            if (name.Length == 0) name = "model";
            if (name.Length > 60) name = name.Substring(0, 60).TrimEnd('-', '.');
            var hash = string.IsNullOrWhiteSpace(pathHash) ? "unsaved" : pathHash.Trim().ToLowerInvariant();
            return name + "-" + (hash.Length > 8 ? hash.Substring(0, 8) : hash);
        }

        /// <summary>Writes the snapshot as a new file (never over an existing one) and returns its path.</summary>
        public string Save(JObject snapshot, DateTime capturedUtc)
        {
            var model = snapshot["model"] as JObject ?? new JObject();
            var key = ModelKey(model.Value<string>("title"), model.Value<string>("path_hash"));
            model["key"] = key;
            snapshot["model"] = model;
            var folder = System.IO.Path.Combine(Root, key);
            Directory.CreateDirectory(folder);
            var stamp = capturedUtc.ToUniversalTime().ToString(FileTimeFormat, CultureInfo.InvariantCulture);
            var path = System.IO.Path.Combine(folder, stamp + ".json");
            for (var n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, stamp + "-" + n + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, snapshot.ToString(Formatting.Indented), Utf8);
            File.Move(temporary, path);
            return path;
        }

        /// <summary>Snapshots of one model, oldest first. Unreadable files are skipped and named in <paramref name="skipped"/>.</summary>
        public List<SnapshotEntry> List(string modelKey, List<string> skipped = null)
        {
            var folder = System.IO.Path.Combine(Root, modelKey ?? "");
            var entries = new List<SnapshotEntry>();
            if (string.IsNullOrWhiteSpace(modelKey) || !Directory.Exists(folder)) return entries;
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                if (!FileName.IsMatch(System.IO.Path.GetFileName(file))) continue;
                try
                {
                    var snapshot = JObject.Parse(File.ReadAllText(file, Utf8));
                    if (snapshot.Value<string>("schema") != Plugin.Handlers.ModelSnapshotSchema.Schema) throw new InvalidDataException("not a model snapshot");
                    entries.Add(new SnapshotEntry
                    {
                        Path = file,
                        CapturedUtc = CapturedUtc(snapshot, file),
                        Label = snapshot.Value<string>("label"),
                        Snapshot = snapshot,
                    });
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    skipped?.Add(System.IO.Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            // Same second: the -2, -3 … suffix keeps the order in which they were saved.
            return entries.OrderBy(e => e.CapturedUtc).ThenBy(e => Sequence(e.Path)).ToList();
        }

        /// <summary>Models with at least one snapshot, most recently captured first.</summary>
        public List<(string Key, string Title, int Count, DateTime LatestUtc)> ListModels()
        {
            var models = new List<(string, string, int, DateTime)>();
            if (!Directory.Exists(Root)) return models;
            foreach (var folder in Directory.GetDirectories(Root))
            {
                var entries = List(System.IO.Path.GetFileName(folder));
                if (entries.Count == 0) continue;
                var latest = entries[entries.Count - 1];
                models.Add((System.IO.Path.GetFileName(folder), latest.Snapshot["model"]?.Value<string>("title") ?? "", entries.Count, latest.CapturedUtc));
            }
            return models.OrderByDescending(m => m.Item4).ToList();
        }

        /// <summary>
        /// The model a request means: an exact key, else a unique title match (ignoring case), else the most
        /// recently captured model when <paramref name="model"/> is empty. Returns null with a reason otherwise.
        /// </summary>
        public string ResolveModel(string model, out string error)
        {
            error = null;
            var models = ListModels();
            if (models.Count == 0) { error = "No model snapshots yet. Take one with revit_create_model_snapshot."; return null; }
            if (string.IsNullOrWhiteSpace(model)) return models[0].Key;
            var wanted = model.Trim();
            var exact = models.Where(m => string.Equals(m.Key, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0].Key;
            var byTitle = models.Where(m => string.Equals(m.Title, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byTitle.Count == 1) return byTitle[0].Key;
            error = byTitle.Count > 1
                ? "Several models are titled '" + wanted + "': " + string.Join(", ", byTitle.Select(m => m.Key)) + ". Pass the key."
                : "No snapshots for '" + wanted + "'. Known models: " + string.Join(", ", models.Select(m => m.Key)) + ".";
            return null;
        }

        private static int Sequence(string file)
        {
            var n = FileName.Match(System.IO.Path.GetFileName(file)).Groups["n"];
            return n.Success ? int.Parse(n.Value, CultureInfo.InvariantCulture) : 1;
        }

        private static DateTime CapturedUtc(JObject snapshot, string file)
        {
            var token = snapshot["captured_at_utc"];
            if (token != null && token.Type == JTokenType.Date) return token.Value<DateTime>().ToUniversalTime();
            if (token != null && DateTime.TryParse(token.Value<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
            var match = FileName.Match(System.IO.Path.GetFileName(file));
            return DateTime.ParseExact(match.Groups["time"].Value, FileTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
    }
}
