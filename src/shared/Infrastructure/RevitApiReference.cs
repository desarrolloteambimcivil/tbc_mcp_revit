using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Reflection + XML-doc lookup over the API assemblies loaded in this process. The handler passes
    /// the running Revit's assemblies, so signatures always match the Revit year that will compile
    /// send_code. Revit ships RevitAPI.xml next to RevitAPI.dll; without it only signatures are returned.
    /// </summary>
    public static class RevitApiReference
    {
        public const int DefaultMaxMembers = 80;
        public const int HardMaxMembers = 300;
        private const int MaxCandidates = 25;
        private const int MaxSummaryChars = 400;

        private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> DocCache =
            new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, object> Lookup(IEnumerable<Assembly> assemblies, string query,
            bool includeInherited = false, int maxMembers = DefaultMaxMembers)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
            query = query.Trim();
            maxMembers = Math.Max(1, Math.Min(maxMembers, HardMaxMembers));

            var types = assemblies.Where(a => a != null && !a.IsDynamic)
                .SelectMany(SafeExportedTypes)
                .Where(t => !t.IsGenericParameter)
                .ToList();

            string memberFilter = null;
            var matches = FindTypes(types, query);
            if (matches.Count == 0 && query.IndexOf('.') > 0)
            {
                var split = query.LastIndexOf('.');
                var typePart = query.Substring(0, split);
                memberFilter = query.Substring(split + 1);
                matches = FindTypes(types, typePart);
            }

            var result = new Dictionary<string, object> { ["query"] = query };
            if (matches.Count != 1)
            {
                var needle = memberFilter == null ? LastSegment(query) : LastSegment(query.Substring(0, query.LastIndexOf('.')));
                var candidates = matches.Count > 1
                    ? matches
                    : types.Where(t => t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(t => t.Name.Length).ThenBy(t => DocName(t), StringComparer.Ordinal).ToList();
                if (candidates.Count == 0)
                {
                    // Typos: rank by edit distance, allowing roughly one edit per four characters.
                    var maxDistance = Math.Max(1, needle.Length / 4);
                    candidates = types.Select(t => new { Type = t, Distance = EditDistance(t.Name.ToLowerInvariant(), needle.ToLowerInvariant()) })
                        .Where(c => c.Distance <= maxDistance)
                        .OrderBy(c => c.Distance).ThenBy(c => DocName(c.Type), StringComparer.Ordinal)
                        .Select(c => c.Type).ToList();
                }
                result["found"] = false;
                result["candidates"] = candidates.Take(MaxCandidates).Select(t => DocName(t)).ToList();
                result["hint"] = matches.Count > 1
                    ? "Several types share this name; repeat the query with a full name from candidates."
                    : "No exact type match. Repeat the query with one of candidates, or a 'Type.MemberPrefix' form.";
                return result;
            }

            var type = matches[0];
            var docs = LoadDocs(type.Assembly);
            var members = DescribeMembers(type, includeInherited, docs)
                .Where(m => memberFilter == null
                    || ((string)m["name"]).StartsWith(memberFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            result["found"] = true;
            result["type"] = DescribeType(type, docs);
            result["xml_docs"] = docs.Count > 0 ? "loaded" : "missing";
            if (memberFilter != null) result["member_filter"] = memberFilter;
            result["total_members"] = members.Count;
            result["members"] = members.Take(maxMembers).ToList();
            result["truncated"] = members.Count > maxMembers;
            return result;
        }

        private static List<Type> FindTypes(List<Type> types, string name)
        {
            var exactFull = types.Where(t => string.Equals(DocName(t), name, StringComparison.Ordinal)).ToList();
            if (exactFull.Count > 0) return exactFull;
            return types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(DocName(t), name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        internal static int EditDistance(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            var current = new int[b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }

        private static string LastSegment(string value)
        {
            var dot = value.LastIndexOf('.');
            return dot < 0 ? value : value.Substring(dot + 1);
        }

        private static IEnumerable<Type> SafeExportedTypes(Assembly assembly)
        {
            try { return assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null && t.IsPublic); }
            catch { return Enumerable.Empty<Type>(); }
        }

        private static Dictionary<string, object> DescribeType(Type type, IReadOnlyDictionary<string, string> docs)
        {
            var info = new Dictionary<string, object>
            {
                ["full_name"] = DocName(type),
                ["kind"] = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                ["assembly"] = type.Assembly.GetName().Name,
            };
            if (type.BaseType != null && type.BaseType != typeof(object) && !type.IsEnum && !type.IsValueType)
                info["base_type"] = DocName(type.BaseType);
            if (type.IsAbstract && type.IsSealed) info["static"] = true;
            else if (type.IsAbstract && !type.IsInterface) info["abstract"] = true;
            AddDocAndObsolete(info, type, "T:" + DocName(type), docs);
            return info;
        }

        internal static IEnumerable<Dictionary<string, object>> DescribeMembers(Type type, bool includeInherited,
            IReadOnlyDictionary<string, string> docs)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited) flags |= BindingFlags.DeclaredOnly;

            if (type.IsEnum)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var entry = Member("enum_value", field.Name, field.Name + " = " + Convert.ToInt64(field.GetRawConstantValue()));
                    AddDocAndObsolete(entry, field, "F:" + DocName(type) + "." + field.Name, docs);
                    yield return entry;
                }
                yield break;
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var entry = Member("constructor", ".ctor", type.Name + "(" + FormatParameters(ctor.GetParameters()) + ")");
                AddDocAndObsolete(entry, ctor, "M:" + DocName(type) + ".#ctor" + DocParameters(ctor.GetParameters()), docs);
                yield return entry;
            }

            foreach (var property in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var getter = property.GetGetMethod();
                var setter = property.GetSetMethod();
                var isStatic = (getter ?? setter)?.IsStatic == true;
                var indexParameters = property.GetIndexParameters();
                var name = indexParameters.Length > 0 ? "this[" + FormatParameters(indexParameters) + "]" : property.Name;
                var accessors = (getter != null ? "get; " : "") + (setter != null ? "set; " : "");
                var signature = (isStatic ? "static " : "") + FriendlyName(property.PropertyType) + " " + name + " { " + accessors + "}";
                var entry = Member("property", property.Name, signature);
                if (property.DeclaringType != type) entry["declared_in"] = DocName(property.DeclaringType);
                AddDocAndObsolete(entry, property,
                    "P:" + DocName(property.DeclaringType) + "." + property.Name + DocParameters(indexParameters), docs);
                yield return entry;
            }

            foreach (var method in type.GetMethods(flags)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GetParameters().Length))
            {
                var parameters = method.GetParameters();
                var generic = method.IsGenericMethodDefinition
                    ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                var signature = (method.IsStatic ? "static " : "") + FriendlyName(method.ReturnType) + " "
                    + method.Name + generic + "(" + FormatParameters(parameters) + ")";
                var entry = Member("method", method.Name, signature);
                if (method.DeclaringType != type) entry["declared_in"] = DocName(method.DeclaringType);
                var genericArity = method.IsGenericMethodDefinition ? "``" + method.GetGenericArguments().Length : "";
                AddDocAndObsolete(entry, method,
                    "M:" + DocName(method.DeclaringType) + "." + method.Name + genericArity + DocParameters(parameters), docs);
                yield return entry;
            }

            foreach (var evt in type.GetEvents(flags).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var entry = Member("event", evt.Name, "event " + FriendlyName(evt.EventHandlerType) + " " + evt.Name);
                AddDocAndObsolete(entry, evt, "E:" + DocName(evt.DeclaringType) + "." + evt.Name, docs);
                yield return entry;
            }

            foreach (var field in type.GetFields(flags).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var entry = Member("field", field.Name,
                    (field.IsStatic ? "static " : "") + FriendlyName(field.FieldType) + " " + field.Name);
                AddDocAndObsolete(entry, field, "F:" + DocName(field.DeclaringType) + "." + field.Name, docs);
                yield return entry;
            }
        }

        private static Dictionary<string, object> Member(string kind, string name, string signature)
            => new Dictionary<string, object> { ["kind"] = kind, ["name"] = name, ["signature"] = signature };

        private static void AddDocAndObsolete(Dictionary<string, object> entry, MemberInfo member, string docId,
            IReadOnlyDictionary<string, string> docs)
        {
            if (docs.TryGetValue(docId, out var summary) && !string.IsNullOrEmpty(summary))
                entry["summary"] = summary;
            ObsoleteAttribute obsolete = null;
            try { obsolete = member.GetCustomAttribute<ObsoleteAttribute>(false); } catch { }
            if (obsolete != null)
                entry["obsolete"] = string.IsNullOrWhiteSpace(obsolete.Message) ? "Obsolete." : obsolete.Message.Trim();
        }

        private static string FormatParameters(ParameterInfo[] parameters)
        {
            return string.Join(", ", parameters.Select(p =>
            {
                var type = p.ParameterType;
                var prefix = "";
                if (type.IsByRef)
                {
                    type = type.GetElementType();
                    prefix = p.IsOut ? "out " : "ref ";
                }
                return prefix + FriendlyName(type) + " " + p.Name;
            }));
        }

        /// <summary>Readable C#-style type name: keywords for primitives, short generic forms.</summary>
        internal static string FriendlyName(Type type)
        {
            if (type == null) return "void";
            if (type.IsByRef) return FriendlyName(type.GetElementType());
            if (type.IsArray) return FriendlyName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter) return type.Name;
            if (Nullable.GetUnderlyingType(type) is Type underlying) return FriendlyName(underlying) + "?";
            if (type.IsEnum) return type.IsNested ? FriendlyName(type.DeclaringType) + "." + type.Name : type.Name;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: return "bool";
                case TypeCode.Byte: return "byte";
                case TypeCode.Char: return "char";
                case TypeCode.Decimal: return "decimal";
                case TypeCode.Double: return "double";
                case TypeCode.Int16: return "short";
                case TypeCode.Int32: return "int";
                case TypeCode.Int64: return "long";
                case TypeCode.Single: return "float";
                case TypeCode.String: return "string";
                case TypeCode.UInt32: return "uint";
            }
            if (type == typeof(void)) return "void";
            if (type == typeof(object)) return "object";
            if (!type.IsGenericType) return type.IsNested ? FriendlyName(type.DeclaringType) + "." + type.Name : type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        /// <summary>Type name in XML documentation-ID form (nested types joined by '.').</summary>
        internal static string DocName(Type type)
        {
            if (type.IsGenericParameter) return (type.DeclaringMethod != null ? "``" : "`") + type.GenericParameterPosition;
            if (type.IsByRef) return DocName(type.GetElementType()) + "@";
            if (type.IsArray)
                return DocName(type.GetElementType()) + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var definition = DocName(type.GetGenericTypeDefinition());
                var tick = definition.LastIndexOf('`');
                if (tick >= 0) definition = definition.Substring(0, tick);
                return definition + "{" + string.Join(",", type.GetGenericArguments().Select(DocName)) + "}";
            }
            if (type.IsNested) return DocName(type.DeclaringType) + "." + type.Name;
            return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
        }

        internal static string DocParameters(ParameterInfo[] parameters)
            => parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(p => DocName(p.ParameterType))) + ")";

        internal static IReadOnlyDictionary<string, string> LoadDocs(Assembly assembly)
        {
            string path;
            try { path = Path.ChangeExtension(assembly.Location, ".xml"); }
            catch { return new Dictionary<string, string>(); }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Dictionary<string, string>();
            return DocCache.GetOrAdd(path, p =>
            {
                try { using (var stream = File.OpenRead(p)) return ParseDocs(stream); }
                catch { return new Dictionary<string, string>(); }
            });
        }

        internal static IReadOnlyDictionary<string, string> ParseDocs(Stream xml)
        {
            var docs = new Dictionary<string, string>(StringComparer.Ordinal);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, XmlResolver = null };
            using (var reader = XmlReader.Create(xml, settings))
            {
                string current = null;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == "member") current = reader.GetAttribute("name");
                    else if (reader.Name == "summary" && current != null && !docs.ContainsKey(current))
                        docs[current] = CleanSummary(reader.ReadInnerXml());
                }
            }
            return docs;
        }

        private static readonly Regex SeeCref = new Regex("<see(?:also)?\\s+cref=\"([A-Z]):([^\"]+)\"\\s*/>", RegexOptions.Compiled);
        private static readonly Regex SeeLangword = new Regex("<see\\s+langword=\"([^\"]+)\"\\s*/>", RegexOptions.Compiled);
        private static readonly Regex AnyTag = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex("\\s+", RegexOptions.Compiled);

        internal static string CleanSummary(string innerXml)
        {
            if (string.IsNullOrEmpty(innerXml)) return null;
            var text = SeeCref.Replace(innerXml, m => ShortCref(m.Groups[1].Value, m.Groups[2].Value));
            text = SeeLangword.Replace(text, m => m.Groups[1].Value);
            text = AnyTag.Replace(text, " ");
            text = Spaces.Replace(WebUtility.HtmlDecode(text), " ").Trim();
            if (text.Length > MaxSummaryChars) text = text.Substring(0, MaxSummaryChars - 3).TrimEnd() + "...";
            return text.Length == 0 ? null : text;
        }

        // Keep "Type" for type references and "Type.Member" otherwise, so the text can be fed back into a lookup.
        internal static string ShortCref(string kind, string cref)
        {
            var paren = cref.IndexOf('(');
            var parts = (paren >= 0 ? cref.Substring(0, paren) : cref).Split('.');
            var keep = kind == "T" || parts.Length < 2
                ? parts[parts.Length - 1]
                : parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            return keep.Replace("#ctor", "constructor");
        }
    }
}
