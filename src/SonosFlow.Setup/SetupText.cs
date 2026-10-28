using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SonosFlow.Setup;

/// <summary>
/// Installer text in the Windows display language (the app's language is chosen later, on first launch).
/// Reads the same translation tree as the app (Localization/Strings/&lt;code&gt;/setup.json, embedded at build time),
/// falling back to English, then Spanish, then the key.
/// </summary>
internal static class SetupText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = Load();
    private static readonly string Code = Pick(CultureInfo.CurrentUICulture.Name);

    public static bool IsRightToLeft => Code == "ar";

    public static string T(string key)
    {
        foreach (var code in new[] { Code, "en", "es" })
            if (Tables.TryGetValue(code, out var t) && t.TryGetValue(key, out var v)) return v;
        return key;
    }

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    private static string Pick(string tag)
    {
        if (Tables.ContainsKey(tag)) return tag;
        var lang = tag.Split('-')[0].ToLowerInvariant();
        if (lang == "zh") return tag.Contains("TW") || tag.Contains("HK") || tag.Contains("Hant") ? "en" : "zh-Hans";
        return Tables.Keys.FirstOrDefault(k => k.Split('-')[0].Equals(lang, System.StringComparison.OrdinalIgnoreCase)) ?? "en";
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(System.StringComparer.OrdinalIgnoreCase);
        var asm = typeof(SetupText).Assembly;
        var json = new JavaScriptSerializer();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith("i18n/")))
        {
            var code = name.Replace('\\', '/').Split('/')[1];
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!, System.Text.Encoding.UTF8);
            result[code] = json.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }
        return result;
    }
}
