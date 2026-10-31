using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class SnapshotStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tbc-snapshots-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static JObject Snapshot(string title, string hash, string captured, string label = null) => new JObject
        {
            ["schema"] = ModelSnapshotSchema.Schema,
            ["schema_version"] = 1,
            ["captured_at_utc"] = captured,
            ["label"] = label,
            ["model"] = new JObject { ["title"] = title, ["path_hash"] = hash },
            ["sections"] = new JObject(),
        };

        [Theory]
        [InlineData("Redes Servicios v01", "ABCDEF0123456789", "Redes-Servicios-v01-abcdef01")]
        [InlineData("Proyecto: A/B*?.rvt", "1234", "Proyecto-A-B-1234")]
        [InlineData("", null, "model-unsaved")]
        public void Model_key_is_folder_safe_and_keeps_copies_apart(string title, string hash, string expected)
            => Assert.Equal(expected, SnapshotStore.ModelKey(title, hash));

        [Fact]
        public void Saves_new_files_without_overwriting_and_lists_them_oldest_first()
        {
            var store = new SnapshotStore(_root);
            var later = store.Save(Snapshot("M", "aaaa1111", "2026-11-02T10:00:00Z", "fin sesión"), new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc));
            var first = store.Save(Snapshot("M", "aaaa1111", "2026-11-01T09:00:00Z"), new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
            var same = store.Save(Snapshot("M", "aaaa1111", "2026-11-01T09:00:00Z"), new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));

            Assert.NotEqual(first, same);
            Assert.EndsWith("20261101-090000-2.json", same);
            var entries = store.List("M-aaaa1111");
            Assert.Equal(new[] { first, same, later }, entries.ConvertAll(e => e.Path));
            Assert.Equal("fin sesión", entries[2].Label);
            Assert.Equal("M-aaaa1111", entries[0].Snapshot["model"].Value<string>("key"));
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "M-aaaa1111"), "*.tmp"));
        }

        [Fact]
        public void Unreadable_or_foreign_files_are_skipped_and_reported()
        {
            var store = new SnapshotStore(_root);
            store.Save(Snapshot("M", "bbbb", "2026-11-01T09:00:00Z"), DateTime.UtcNow);
            var folder = Path.Combine(_root, "M-bbbb");
            File.WriteAllText(Path.Combine(folder, "20260101-000000.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder, "20260102-000000.json"), "{\"schema\":\"other\"}");
            File.WriteAllText(Path.Combine(folder, "notes.json"), "{}");

            var skipped = new List<string>();
            Assert.Single(store.List("M-bbbb", skipped));
            Assert.Equal(2, skipped.Count);
        }

        [Fact]
        public void Resolves_a_model_by_key_title_or_latest()
        {
            var store = new SnapshotStore(_root);
            Assert.Null(store.ResolveModel("", out var none));
            Assert.Contains("No model snapshots", none);

            store.Save(Snapshot("Alpha", "11111111", "2026-11-01T09:00:00Z"), new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
            store.Save(Snapshot("Beta", "22222222", "2026-11-03T09:00:00Z"), new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc));
            store.Save(Snapshot("Beta", "33333333", "2026-11-02T09:00:00Z"), new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc));

            Assert.Equal("Beta-22222222", store.ResolveModel("", out _));
            Assert.Equal("Alpha-11111111", store.ResolveModel("alpha", out _));
            Assert.Equal("Beta-33333333", store.ResolveModel("Beta-33333333", out _));
            Assert.Null(store.ResolveModel("Beta", out var ambiguous));
            Assert.Contains("Several models", ambiguous);
            Assert.Null(store.ResolveModel("Gamma", out var unknown));
            Assert.Contains("Known models", unknown);
            Assert.Equal(3, store.ListModels().Count);
        }
    }
}
