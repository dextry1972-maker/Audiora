namespace SonosFlow.App.Localization;

/// <summary>
/// CLDR cardinal plural categories (integers only — Audiora counts things) for the supported languages.
/// Keys use the category as suffix: "net.stalls.one", "net.stalls.few", "net.stalls.other"…; "other" is required.
/// </summary>
public static class PluralRules
{
    public static string Category(string code, long n)
    {
        n = Math.Abs(n);
        switch (code.Split('-')[0])
        {
            case "ja": case "ko": case "zh":
                return "other";
            case "fr": case "hi": case "pt":
                return n is 0 or 1 ? "one" : "other";   // 0 and 1 are singular (pt-BR follows CLDR "pt")
            case "ru":
                long m10 = n % 10, m100 = n % 100;
                if (m10 == 1 && m100 != 11) return "one";
                if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return "few";
                return "many";
            case "ar":
                if (n == 0) return "zero";
                if (n == 1) return "one";
                if (n == 2) return "two";
                long r = n % 100;
                if (r is >= 3 and <= 10) return "few";
                if (r is >= 11 and <= 99) return "many";
                return "other";
            default: // en, es, de, it
                return n == 1 ? "one" : "other";
        }
    }

    /// <summary>Categories a language can produce: translators must provide each of them (checked by tests).</summary>
    public static IReadOnlyList<string> CategoriesOf(string code) => code.Split('-')[0] switch
    {
        "ja" or "ko" or "zh" => ["other"],
        "ru" => ["one", "few", "many"],
        "ar" => ["zero", "one", "two", "few", "many", "other"],
        _ => ["one", "other"],
    };
}
