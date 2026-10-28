using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace SonosFlow.App.Localization;

/// <summary>
/// Translated text for the whole app. Tables are flat JSON files embedded from Localization/Strings/&lt;code&gt;/*.json
/// (one file per area, merged). Lookup: current language → <see cref="Languages.Fallback"/> → <see cref="Languages.Source"/>
/// → the key itself in brackets, so a missing translation is visible but never crashes. XAML binds through the indexer
/// (<see cref="T"/> markup extension), so changing the language updates every open window without a restart.
/// Logs and settings never go through here: they stay language-independent.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = [];

    private Loc() => Apply(Languages.Source);

    public string Code { get; private set; } = Languages.Source;
    public CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;
    public bool IsRightToLeft { get; private set; }
    public FlowDirection FlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Raised on the calling thread after the language changed (view models refresh computed text).</summary>
    public event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Keys looked up but missing in the current language (diagnostics and tests).</summary>
    public IReadOnlyCollection<string> MissingKeys { get { lock (_missing) return [.. _missing]; } }

    public string this[string key] => T(key);

    public void SetLanguage(string code)
    {
        var resolved = Languages.Resolve(code) ?? Languages.Fallback;
        if (resolved == Code && _tables.ContainsKey(resolved)) return;
        Apply(resolved);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        LanguageChanged?.Invoke();
    }

    private void Apply(string code)
    {
        Code = code;
        Culture = Languages.CultureFor(code);
        IsRightToLeft = Languages.All.FirstOrDefault(l => l.Code == code)?.IsRightToLeft ?? false;
        Table(code);
    }

    public string T(string key)
    {
        if (TryGet(key, out var value)) return value;
        lock (_missing) _missing.Add(key);
        return $"⟦{key}⟧";
    }

    /// <summary>Formats with the language's culture: "{0}" placeholders, numbers like 1,2 / 1.2 as the language expects.</summary>
    public string F(string key, params object?[] args)
    {
        var pattern = T(key);
        try { return string.Format(Culture, pattern, args); }
        catch (FormatException) { return pattern; } // a broken translation must not crash the UI (tests catch it)
    }

    /// <summary>Plural form by CLDR rules: looks up key.zero/one/two/few/many/other; {0} is the count unless args are given.</summary>
    public string Plural(string key, long count, params object?[] args)
    {
        var category = PluralRules.Category(Code, count);
        var full = TryGet($"{key}.{category}", out _) ? $"{key}.{category}" : $"{key}.other";
        return F(full, args.Length > 0 ? args : [count]);
    }

    /// <summary>
    /// Text for a message coded as "key" or "key|argument" (how Core reports errors without depending on the UI language).
    /// Anything that is not a known key is shown as is.
    /// </summary>
    public string Message(string? coded)
    {
        if (string.IsNullOrEmpty(coded)) return "";
        var bar = coded.IndexOf('|');
        var key = bar < 0 ? coded : coded[..bar];
        if (!TryGet(key, out _)) return coded;
        return bar < 0 ? T(key) : F(key, coded[(bar + 1)..]);
    }

    private bool TryGet(string key, out string value)
    {
        foreach (var code in new[] { Code, Languages.Fallback, Languages.Source })
            if (Table(code).TryGetValue(key, out value!)) return true;
        value = "";
        return false;
    }

    private IReadOnlyDictionary<string, string> Table(string code)
    {
        lock (_tables)
        {
            if (!_tables.TryGetValue(code, out var table)) _tables[code] = table = LoadTable(code);
            return table;
        }
    }

    /// <summary>Merges every embedded Strings/&lt;code&gt;/*.json. Public for the coverage tests.</summary>
    public static IReadOnlyDictionary<string, string> LoadTable(string code)
    {
        var asm = typeof(Loc).Assembly;
        var prefix = $"i18n/{code}/";
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Json) ?? [];
            foreach (var (k, v) in entries) table[k] = v;
        }
        return table;
    }

    /// <summary>Languages whose files are embedded (tests compare this with <see cref="Languages.All"/>).</summary>
    public static IReadOnlyCollection<string> EmbeddedLanguages() =>
        typeof(Loc).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith("i18n/", StringComparison.Ordinal))
            .Select(n => n.Split('/')[1])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
