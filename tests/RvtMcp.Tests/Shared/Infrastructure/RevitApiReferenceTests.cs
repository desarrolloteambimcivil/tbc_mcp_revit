using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ApiLookupFixture
    {
        public ApiLookupFixture() { }
        public ApiLookupFixture(int seed) { }
        public double Length { get; set; }
        public static string Version => "1";
        [Obsolete("Use Length.")] public double Size => Length;
        public IList<long> GetIds(out int count, ref double scale) { count = 0; return new List<long>(); }
        public T Echo<T>(T value) => value;
        public enum Mode { Off = 0, On = 2 }
    }

    public class RevitApiReferenceTests
    {
        private static Dictionary<string, object> Lookup(string query, int maxMembers = RevitApiReference.DefaultMaxMembers)
            => RevitApiReference.Lookup(new[] { typeof(ApiLookupFixture).Assembly }, query, maxMembers: maxMembers);

        private static List<Dictionary<string, object>> Members(Dictionary<string, object> result)
            => ((IEnumerable<Dictionary<string, object>>)result["members"]).ToList();

        [Fact]
        public void Finds_a_type_by_simple_name_with_readable_signatures()
        {
            var result = Lookup("ApiLookupFixture");

            Assert.True((bool)result["found"]);
            var type = (Dictionary<string, object>)result["type"];
            Assert.Equal("RvtMcp.Tests.ApiLookupFixture", type["full_name"]);
            var signatures = Members(result).Select(m => (string)m["signature"]).ToList();
            Assert.Contains("ApiLookupFixture(int seed)", signatures);
            Assert.Contains("double Length { get; set; }", signatures);
            Assert.Contains("static string Version { get; }", signatures);
            Assert.Contains("IList<long> GetIds(out int count, ref double scale)", signatures);
            Assert.Contains("T Echo<T>(T value)", signatures);
            Assert.DoesNotContain(signatures, s => s.Contains("get_Length"));
        }

        [Fact]
        public void Type_dot_member_prefix_filters_members()
        {
            var result = Lookup("ApiLookupFixture.Get");

            Assert.True((bool)result["found"]);
            Assert.Equal("Get", result["member_filter"]);
            Assert.Equal(new[] { "GetIds" }, Members(result).Select(m => (string)m["name"]).ToArray());
        }

        [Fact]
        public void Full_name_resolves_nested_enum_with_values()
        {
            var result = Lookup("RvtMcp.Tests.ApiLookupFixture.Mode");

            Assert.True((bool)result["found"]);
            Assert.Equal("enum", ((Dictionary<string, object>)result["type"])["kind"]);
            Assert.Equal(new[] { "Off = 0", "On = 2" }, Members(result).Select(m => (string)m["signature"]).ToArray());
        }

        [Fact]
        public void Unknown_name_returns_candidates_instead_of_guessing()
        {
            var result = Lookup("LookupFix");

            Assert.False((bool)result["found"]);
            Assert.Contains("RvtMcp.Tests.ApiLookupFixture", (IEnumerable<string>)result["candidates"]);
        }

        [Fact]
        public void Misspelled_name_returns_close_candidates()
        {
            var result = Lookup("ApiLokupFixture");

            Assert.False((bool)result["found"]);
            Assert.Equal("RvtMcp.Tests.ApiLookupFixture", ((IEnumerable<string>)result["candidates"]).First());
        }

        [Fact]
        public void Obsolete_members_are_flagged_with_their_message()
        {
            var size = Members(Lookup("ApiLookupFixture.Size")).Single();

            Assert.Equal("Use Length.", size["obsolete"]);
        }

        [Fact]
        public void Member_cap_reports_truncation()
        {
            var result = Lookup("ApiLookupFixture", maxMembers: 2);

            Assert.Equal(2, Members(result).Count);
            Assert.True((bool)result["truncated"]);
        }

        [Fact]
        public void Documentation_ids_match_the_compiler_xml_format()
        {
            var docs = new Dictionary<string, string>
            {
                ["M:RvtMcp.Tests.ApiLookupFixture.#ctor(System.Int32)"] = "seeded",
                ["P:RvtMcp.Tests.ApiLookupFixture.Length"] = "length",
                ["M:RvtMcp.Tests.ApiLookupFixture.GetIds(System.Int32@,System.Double@)"] = "ids",
                ["M:RvtMcp.Tests.ApiLookupFixture.Echo``1(``0)"] = "echo",
            };

            var summaries = RevitApiReference.DescribeMembers(typeof(ApiLookupFixture), false, docs)
                .Where(m => m.ContainsKey("summary"))
                .ToDictionary(m => (string)m["signature"], m => (string)m["summary"]);

            Assert.Equal("seeded", summaries["ApiLookupFixture(int seed)"]);
            Assert.Equal("length", summaries["double Length { get; set; }"]);
            Assert.Equal("ids", summaries["IList<long> GetIds(out int count, ref double scale)"]);
            Assert.Equal("echo", summaries["T Echo<T>(T value)"]);
            Assert.Equal("System.Collections.Generic.IList{System.Int64}", RevitApiReference.DocName(typeof(IList<long>)));
        }

        [Fact]
        public void Parses_summaries_and_shortens_references()
        {
            const string xml = @"<?xml version=""1.0""?><doc><members>
<member name=""P:Autodesk.Revit.DB.Architecture.BalusterPattern.Length""><summary>
   The length of the pattern, see <see cref=""M:Autodesk.Revit.DB.Architecture.BalusterPattern.GetBaluster(System.Int32)"" />
   and <see cref=""T:Autodesk.Revit.DB.ElementId"" />; null is <see langword=""false"" /> &amp; fine.
</summary><remarks>ignored</remarks></member>
</members></doc>";

            var docs = RevitApiReference.ParseDocs(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

            Assert.Equal("The length of the pattern, see BalusterPattern.GetBaluster and ElementId; null is false & fine.",
                docs["P:Autodesk.Revit.DB.Architecture.BalusterPattern.Length"]);
        }

        [Fact]
        public void Long_summaries_are_truncated()
        {
            var text = RevitApiReference.CleanSummary(new string('a', 1000));

            Assert.Equal(400, text.Length);
            Assert.EndsWith("...", text);
        }
    }
}
