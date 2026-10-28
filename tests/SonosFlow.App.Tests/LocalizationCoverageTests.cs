using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonosFlow.App.Localization;

namespace SonosFlow.App.Tests;

/// <summary>
/// Translation files: every shipped language is complete, consistent with English (the fallback), safe to format,
/// and every key the code uses exists. These run on the embedded resources, exactly what the app loads.
/// </summary>
public sealed class LocalizationCoverageTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.Compiled);
    private static IReadOnlyDictionary<string, string> En => Loc.LoadTable(Languages.Fallback);

    public static IEnumerable<object[]> ShippedLanguages => Languages.All.Select(l => new object[] { l.Code });

    [Fact]
    public void Registry_and_embedded_files_match()
    {
        var embedded = Loc.EmbeddedLanguages().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in Languages.All) Assert.True(embedded.Contains(l.Code), $"no translation files for {l.Code}");
        foreach (var e in embedded) Assert.True(Languages.All.Any(l => l.Code.Equals(e, StringComparison.OrdinalIgnoreCase)), $"files for unregistered language {e}");
    }

    [Fact]
    public void Installer_texts_are_strict_json()
    {
        // The installer (.NET Framework, JavaScriptSerializer) cannot read comments or trailing commas: it would fail to start.
        var asm = typeof(Loc).Assembly;
        var setupFiles = asm.GetManifestResourceNames().Where(n => n.Replace('\\', '/').EndsWith("/setup.json")).ToList();
        Assert.Equal(Languages.All.Count, setupFiles.Count);
        foreach (var name in setupFiles)
        {
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            var ex = Record.Exception(() => JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions()).Dispose());
            Assert.True(ex is null, $"{name}: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(ShippedLanguages))]
    public void Every_language_has_exactly_the_english_keys(string code)
    {
        var table = Loc.LoadTable(code);
        var missing = BaseKeys(En).Except(BaseKeys(table)).OrderBy(k => k).ToList();
        var extra = BaseKeys(table).Except(BaseKeys(En)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} missing keys: {string.Join(", ", missing.Take(20))}");
        Assert.True(extra.Count == 0, $"{code}: keys not in English: {string.Join(", ", extra.Take(20))}");
        Assert.All(table, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{code}: empty value for {kv.Key}"));
    }

    [Theory]
    [MemberData(nameof(ShippedLanguages))]
    public void Placeholders_match_english_and_format_safely(string code)
    {
        var table = Loc.LoadTable(code);
        foreach (var (key, value) in table)
        {
            var enKey = En.ContainsKey(key) ? key : PluralBase(key) is { } b && En.ContainsKey(b + ".other") ? b + ".other" : null;
            if (enKey is null) continue;
            var expected = Indices(En[enKey]);
            // Plural forms may omit the count ({0}) — e.g. Arabic "one" — but must not invent other values.
            var actual = Indices(value);
            if (PluralBase(key) is not null) Assert.True(actual.IsSubsetOf(expected), $"{code}: {key} uses {{{string.Join(",", actual.Except(expected))}}}");
            else Assert.True(expected.SetEquals(actual), $"{code}: {key} has {{{string.Join(",", actual)}}} but English has {{{string.Join(",", expected)}}}");
            var args = Enumerable.Range(0, 6).Select(i => (object)(i + 1.5)).ToArray();
            var ex = Record.Exception(() => string.Format(Languages.CultureFor(code), value, args));
            Assert.True(ex is null, $"{code}: {key} is not a valid format string: {value}");
        }
    }

    [Theory]
    [MemberData(nameof(ShippedLanguages))]
    public void Plural_keys_have_every_category_the_language_uses(string code)
    {
        var table = Loc.LoadTable(code);
        foreach (var plural in En.Keys.Select(PluralBase).OfType<string>().Distinct())
            foreach (var category in PluralRules.CategoriesOf(code).Append("other").Distinct())
                if (category != "other" || code.Split('-')[0] is "ja" or "ko" or "zh" or "ar" || PluralRules.CategoriesOf(code).Contains("other"))
                    Assert.True(table.ContainsKey($"{plural}.{category}"), $"{code}: missing plural form {plural}.{category}");
    }

    [Theory]
    [MemberData(nameof(ShippedLanguages))]
    public void No_key_is_defined_twice_across_area_files(string code)
    {
        var seen = new Dictionary<string, string>();
        var asm = typeof(Loc).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith($"i18n/{code}/", StringComparison.OrdinalIgnoreCase)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(s, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
            foreach (var k in entries.Keys)
            {
                Assert.False(seen.TryGetValue(k, out var other), $"{code}: {k} in {name} and {other}");
                seen[k] = name;
            }
        }
    }

    [Fact]
    public void Every_key_used_in_the_code_exists_in_english()
    {
        var root = RepoRoot();
        var used = new HashSet<string>();
        var xaml = new Regex(@"\{loc:T\s+([\w.]+)\}");
        var code = new Regex(@"\b(?:L|Loc\.Instance|Localization\.Loc\.Instance)\.(?:T|F|Plural)\(\s*""([\w.]+)""");
        var ternary = new Regex(@"\bL\.T\((?:[^()""]|\([^()]*\))*\?\s*""([\w.]+)""\s*:\s*""([\w.]+)""");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "SonosFlow.App"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in xaml.Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in code.Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in ternary.Matches(text)) { used.Add(m.Groups[1].Value); used.Add(m.Groups[2].Value); }
        }
        foreach (var field in typeof(SonosFlow.Core.Streaming.SessionMessages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
            used.Add((string)field.GetRawConstantValue()!);

        Assert.True(used.Count > 100, $"only {used.Count} keys found: the scan patterns are probably wrong");
        var missing = used.Where(k => !En.ContainsKey(k) && !En.ContainsKey(k + ".other")).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, $"keys used in code but missing in en: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Non_latin_scripts_load_as_real_unicode()
    {
        foreach (var (code, sample) in new[] { ("ar", "؀"), ("zh-Hans", "一"), ("ja", "぀"), ("ko", "가"), ("hi", "ऀ"), ("ru", "Ѐ") })
        {
            var table = Loc.LoadTable(code);
            int lo = sample[0], hi = lo + (code == "zh-Hans" ? 0x5200 : code == "ko" ? 0x2BA4 : 0xFF);
            Assert.True(table.Values.Count(v => v.Any(ch => ch >= lo && ch <= hi)) > table.Count / 2,
                $"{code}: most strings should contain its script (encoding problem or untranslated file?)");
        }
    }

    [Fact]
    public void Long_languages_stay_within_a_reasonable_length()
    {
        // German, Russian… are ~30–40 % longer than English. Anything above 2.2× on a short UI label is suspicious.
        foreach (var code in new[] { "de", "ru", "fr", "pt-BR", "it", "es" })
        {
            var table = Loc.LoadTable(code);
            var tooLong = table.Where(kv => En.TryGetValue(kv.Key, out var en) && en.Length >= 6 && en.Length <= 40 && kv.Value.Length > en.Length * 2.2)
                               .Select(kv => kv.Key).ToList();
            Assert.True(tooLong.Count <= 3, $"{code}: labels much longer than English (check layout): {string.Join(", ", tooLong)}");
        }
    }

    private static IEnumerable<string> BaseKeys(IReadOnlyDictionary<string, string> table) =>
        table.Keys.Select(k => PluralBase(k) is { } b ? b + ".#plural" : k).Distinct();

    private static readonly string[] Categories = ["zero", "one", "two", "few", "many", "other"];
    private static string? PluralBase(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot > 0 && Categories.Contains(key[(dot + 1)..]) ? key[..dot] : null;
    }

    private static HashSet<int> Indices(string s) => Placeholder.Matches(s).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SonosFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SonosFlow.sln not found above the test folder");
    }
}
