using System.Globalization;

namespace SonosFlow.App.Localization;

/// <summary>A language Audiora ships. <paramref name="Code"/> is a BCP 47 tag and the folder name under Strings/.</summary>
public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, bool IsRightToLeft)
{
    /// <summary>Shown in pickers: the native name, plus the English name when it differs (helps if the script is unfamiliar).</summary>
    public string Display => NativeName == EnglishName ? NativeName : $"{NativeName} — {EnglishName}";
}

/// <summary>Registry of supported languages and the rules to map any locale tag onto one of them.</summary>
public static class Languages
{
    /// <summary>Last resort for missing keys: every key must exist here (checked by tests).</summary>
    public const string Fallback = "en";

    /// <summary>Language the copy was written in; previous Audiora versions were Spanish-only.</summary>
    public const string Source = "es";

    public static IReadOnlyList<LanguageInfo> All { get; } =
    [
        new("es", "Spanish", "Español", false),
        new("en", "English", "English", false),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", false),
        new("fr", "French", "Français", false),
        new("de", "German", "Deutsch", false),
        new("it", "Italian", "Italiano", false),
        new("ru", "Russian", "Русский", false),
        new("zh-Hans", "Chinese (Simplified)", "简体中文", false),
        new("ja", "Japanese", "日本語", false),
        new("ko", "Korean", "한국어", false),
        new("hi", "Hindi", "हिन्दी", false),
        new("ar", "Arabic", "العربية", true),
    ];

    public static LanguageInfo Get(string code) => All.First(l => l.Code == code);

    /// <summary>
    /// Nearest supported language for a tag ("es-MX" → es, "pt-PT" → pt-BR, "zh-CN"/"zh-SG" → zh-Hans), or null.
    /// Traditional Chinese tags (zh-TW, zh-HK, zh-Hant) are not mapped to Simplified on purpose.
    /// </summary>
    public static string? Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim().Replace('_', '-');
        var exact = All.FirstOrDefault(l => string.Equals(l.Code, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Code;

        var parts = tag.Split('-');
        var lang = parts[0].ToLowerInvariant();
        if (lang == "zh")
        {
            bool traditional = parts.Skip(1).Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase)
                || p.Equals("TW", StringComparison.OrdinalIgnoreCase) || p.Equals("HK", StringComparison.OrdinalIgnoreCase)
                || p.Equals("MO", StringComparison.OrdinalIgnoreCase));
            return traditional ? null : "zh-Hans";
        }
        return All.FirstOrDefault(l => l.Code.Split('-')[0].Equals(lang, StringComparison.OrdinalIgnoreCase))?.Code;
    }

    /// <summary>Suggestion for a first launch: the Windows display language when supported, else English.</summary>
    public static string SuggestFromSystem(CultureInfo? ui = null) =>
        Resolve((ui ?? CultureInfo.CurrentUICulture).Name) ?? Fallback;

    /// <summary>Culture used to format numbers and dates for a language.</summary>
    public static CultureInfo CultureFor(string code)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        // Dates stay Gregorian (Arabic data may default to the Umm al-Qura calendar).
        if (culture.Calendar is GregorianCalendar) return culture;
        var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        if (gregorian is null) return culture;
        var copy = (CultureInfo)culture.Clone();
        copy.DateTimeFormat.Calendar = gregorian;
        return CultureInfo.ReadOnly(copy);
    }
}
