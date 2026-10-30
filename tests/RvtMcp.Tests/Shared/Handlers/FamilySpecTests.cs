using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class FamilySpecTests
    {
        private static FamilySpec Parse(string json, out List<string> errors) => FamilySpec.Parse(JObject.Parse(json), out errors);

        private const string Pedestal = @"{
            ""name"": ""Pedestal"", ""template"": ""Metric Generic Model"", ""category"": ""OST_GenericModel"",
            ""parameters"": [ { ""name"": ""Alto"", ""type"": ""length"" }, { ""name"": ""Acabado"", ""type"": ""material"" },
                              { ""name"": ""Ancho"", ""type"": ""length"", ""instance"": true, ""formula"": ""Alto / 2"" } ],
            ""types"": [ { ""name"": ""900"", ""values"": { ""Alto"": 900, ""Acabado"": ""Hormigón"" } } ],
            ""solids"": [
              { ""shape"": ""extrusion"", ""rect"": { ""x"": -200, ""y"": -200, ""width"": 400, ""depth"": 400 }, ""end_mm"": 900, ""end_param"": ""Alto"", ""material_param"": ""Acabado"" },
              { ""shape"": ""extrusion"", ""circle"": { ""radius"": 50 }, ""start_mm"": 800, ""end_mm"": 900, ""void"": true },
              { ""shape"": ""revolution"", ""profile"": [[0,900],[150,900],[150,950],[0,950]], ""end_angle_deg"": 360 } ] }";

        [Fact]
        public void Valid_spec_parses_with_defaults()
        {
            var spec = Parse(Pedestal, out var errors);

            Assert.Empty(errors);
            Assert.Equal("Pedestal", spec.Name);
            Assert.True(spec.LoadIntoProject);
            Assert.False(spec.Overwrite);
            Assert.Equal("dimensions", spec.Parameters[0].Group);
            Assert.Equal("materials", spec.Parameters[1].Group);
            Assert.True(spec.Parameters[2].Instance);
            Assert.Equal(4, spec.Solids[0].Profile.Count);
            Assert.Equal(new double[] { 0, 0, 50 }, spec.Solids[1].Circle);
            Assert.True(spec.Solids[1].Void);
            Assert.Equal(900, spec.Types[0].Values["Alto"].Value<double>());
        }

        [Fact]
        public void Rect_becomes_a_closed_counter_clockwise_profile()
        {
            var spec = Parse(Pedestal, out _);
            var profile = spec.Solids[0].Profile;

            Assert.Equal(new[] { -200.0, -200.0 }, profile[0]);
            Assert.Equal(new[] { 200.0, 200.0 }, profile[2]);
            Assert.True(FamilySpec.SignedArea(profile) > 0);
        }

        [Fact]
        public void Repeated_closing_point_and_duplicates_are_dropped()
        {
            var spec = Parse(@"{""name"":""A"",""template"":""T"",""solids"":[{""profile"":[[0,0],[0,0],[100,0],[100,100],[0,0]],""end_mm"":10}]}", out var errors);

            Assert.Empty(errors);
            Assert.Equal(3, spec.Solids[0].Profile.Count);
        }

        [Theory]
        [InlineData(@"{""template"":""T"",""solids"":[{""rect"":{""width"":1,""depth"":1},""end_mm"":1}]}", "name is required")]
        [InlineData(@"{""name"":""A/B"",""template"":""T""}", "characters")]
        [InlineData(@"{""name"":""A""}", "template is required")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""save_path"":""C:/x/a.txt""}", "save_path must end with .rfa")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""parameters"":[{""name"":""P"",""type"":""colour""}]}", "parameters[0].type")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""parameters"":[{""name"":""P""},{""name"":""p""}]}", "duplicate parameter")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""types"":[{""name"":""X""},{""name"":""x""}]}", "duplicate type")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""end_mm"":10}]}", "exactly one of profile, rect or circle")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""profile"":[[0,0],[1,0]],""end_mm"":10}]}", "at least 3 distinct points")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""profile"":[[0,0],[10,0],[20,0]],""end_mm"":10}]}", "no area")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""rect"":{""width"":0,""depth"":5},""end_mm"":10}]}", "width and depth > 0")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""circle"":{""radius"":5},""start_mm"":3,""end_mm"":3}]}", "end_mm must differ")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""circle"":{""radius"":5},""end_mm"":3,""end_param"":""H""}]}", "end_param 'H' must be a parameter of type length")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""circle"":{""radius"":5},""end_mm"":3,""plane"":""XW""}]}", "plane must be XY, XZ or YZ")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""shape"":""revolution"",""profile"":[[-5,0],[5,0],[5,5]]}]}", "radius >= 0")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""shape"":""revolution"",""profile"":[[0,0],[5,0],[5,5]],""end_angle_deg"":400}]}", "at most 360")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""circle"":{""radius"":5},""end_mm"":3,""void"":true}]}", "at least one solid that is not a void")]
        [InlineData(@"{""name"":""A"",""template"":""T"",""solids"":[{""circle"":{""radius"":5},""end_mm"":3,""material"":""M"",""material_param"":""P""}]}", "not both")]
        public void Invalid_specs_are_rejected_with_a_specific_message(string json, string expected)
        {
            Parse(json, out var errors);

            Assert.Contains(errors, e => e.Contains(expected));
        }

        [Fact]
        public void Limits_are_enforced()
        {
            var solids = string.Join(",", Enumerable.Repeat(@"{""circle"":{""radius"":5},""end_mm"":3}", FamilySpec.MaxSolids + 1));
            Parse(@"{""name"":""A"",""template"":""T"",""solids"":[" + solids + "]}", out var errors);

            Assert.Contains(errors, e => e.Contains("limited to " + FamilySpec.MaxSolids));
        }
    }
}
