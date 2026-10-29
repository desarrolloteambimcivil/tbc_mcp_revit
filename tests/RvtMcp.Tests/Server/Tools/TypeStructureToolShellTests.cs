using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class TypeStructureToolShellTests
    {
        [Fact]
        public async Task Set_railing_structure_sends_parsed_patch_arrays()
        {
            var sent = await Capture.Send(() => ModifyTools.SetRailingStructure(
                typeId: 230748,
                balusters_json: "[{\"target\":\"all\",\"base_reference\":\"host\",\"base_offset_mm\":0}]",
                rails_json: "[{\"name\":\"Plasitor\",\"offset_mm\":-24}]"));

            Assert.Equal("set_railing_structure", sent.Command);
            var json = sent.Json();
            Assert.Equal(230748, json.Value<long>("typeId"));
            Assert.Equal("host", json["balusters"]![0]!.Value<string>("base_reference"));
            Assert.Equal(-24, json["rails"]![0]!.Value<double>("offset_mm"));
        }

        [Fact]
        public async Task Set_railing_structure_rejects_malformed_json_without_calling_revit()
        {
            var called = false;
            await Capture.WithSendOverride(
                (command, parameters, timeout) => { called = true; return Task.FromResult(new JObject()); },
                async () =>
                {
                    var text = await ModifyTools.SetRailingStructure(typeId: 1, balusters_json: "[{not json");
                    Assert.StartsWith("Error:", text);
                });
            Assert.False(called);
        }

        [Fact]
        public async Task Set_compound_layers_sends_layer_patches_and_deletions()
        {
            var sent = await Capture.Send(() => ModifyTools.SetCompoundLayers(
                elementId: 12345,
                layers_json: "[{\"index\":1,\"width_mm\":120,\"material_name\":\"Concrete\"}]",
                delete_indices: new long[] { 3 }));

            Assert.Equal("set_compound_layers", sent.Command);
            var json = sent.Json();
            Assert.Equal(12345, json.Value<long>("elementId"));
            Assert.Equal(120, json["layers"]![0]!.Value<double>("width_mm"));
            Assert.Equal(3, json["delete_indices"]![0]!.Value<long>());
        }

        [Fact]
        public async Task Capture_view_image_forwards_focus_options()
        {
            var sent = await Capture.Send(() => ViewTools.CaptureViewImage(
                element_ids: new long[] { 419359 }, padding_mm: 500, isolate: true));

            Assert.Equal("capture_view_image", sent.Command);
            var json = sent.Json();
            Assert.Equal(419359, json["element_ids"]![0]!.Value<long>());
            Assert.Equal(500, json.Value<double>("padding_mm"));
            Assert.True(json.Value<bool>("isolate"));
        }
    }
}
