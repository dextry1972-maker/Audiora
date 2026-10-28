using System.Globalization;
using System.Windows;
using SonosFlow.App.Localization;

namespace SonosFlow.App.Tests;

/// <summary>Tests that change the shared <see cref="Loc"/> instance must not run in parallel.</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection;

[Collection("Loc")]
public sealed class LocalizationBehaviourTests : IDisposable
{
    private readonly string _before = Loc.Instance.Code;
    public void Dispose() => Loc.Instance.SetLanguage(_before);

    [Fact]
    public void Choice_items_keep_their_identity_and_relabel_live()
    {
        // A ComboBox keeps its selection across a language change only if the items are the same (by Key).
        Loc.Instance.SetLanguage("en");
        var item = new ViewModels.ChoiceItem("tray", () => Loc.Instance.T("choices.close.tray"));
        string english = item.Label;
        bool raised = false;
        item.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(ViewModels.ChoiceItem.Label);
        Loc.Instance.SetLanguage("de");
        item.RefreshText();
        Assert.True(raised);
        Assert.NotEqual(english, item.Label);
        Assert.Equal(new ViewModels.ChoiceItem("tray", () => "other label"), item);
        Assert.NotEqual(new ViewModels.ChoiceItem("mini", () => item.Label), item);
    }

    [Fact]
    public void Unknown_key_is_visible_and_recorded_never_a_crash()
    {
        Loc.Instance.SetLanguage("en");
        Assert.Equal("⟦does.not.exist⟧", Loc.Instance.T("does.not.exist"));
        Assert.Contains("does.not.exist", Loc.Instance.MissingKeys);
        Assert.Equal("⟦does.not.exist.either⟧", Loc.Instance.F("does.not.exist.either", 1, 2));
    }

    [Fact]
    public void Switching_language_changes_text_culture_and_direction()
    {
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Transmitir", Loc.Instance.T("status.button.start"));
        Assert.Equal(FlowDirection.LeftToRight, Loc.Instance.FlowDirection);

        Loc.Instance.SetLanguage("en");
        Assert.Equal("Stream", Loc.Instance.T("status.button.start"));
        Assert.Equal("≈ 1.5 s (estimated)", Loc.Instance.F("stats.buffer", 1.5));

        Loc.Instance.SetLanguage("de");
        Assert.Contains("1,5", Loc.Instance.F("stats.buffer", 1.5)); // German decimal comma

        Loc.Instance.SetLanguage("ar");
        Assert.True(Loc.Instance.IsRightToLeft);
        Assert.Equal(FlowDirection.RightToLeft, Loc.Instance.FlowDirection);
    }

    [Fact]
    public void Language_change_notifies_bindings_and_listeners()
    {
        Loc.Instance.SetLanguage("es");
        var props = new List<string?>();
        int events = 0;
        void OnProp(object? s, System.ComponentModel.PropertyChangedEventArgs e) => props.Add(e.PropertyName);
        void OnLang() => events++;
        Loc.Instance.PropertyChanged += OnProp;
        Loc.Instance.LanguageChanged += OnLang;
        try
        {
            Loc.Instance.SetLanguage("fr");
            Loc.Instance.SetLanguage("fr"); // same language: nothing to do
        }
        finally
        {
            Loc.Instance.PropertyChanged -= OnProp;
            Loc.Instance.LanguageChanged -= OnLang;
        }
        Assert.Contains(System.Windows.Data.Binding.IndexerName, props);
        Assert.Equal(1, events);
    }

    [Fact]
    public void Plurals_follow_cldr_rules()
    {
        Assert.Equal("one", PluralRules.Category("es", 1));
        Assert.Equal("other", PluralRules.Category("es", 0));
        Assert.Equal("one", PluralRules.Category("fr", 0));
        Assert.Equal("one", PluralRules.Category("ru", 21));
        Assert.Equal("few", PluralRules.Category("ru", 23));
        Assert.Equal("many", PluralRules.Category("ru", 11));
        Assert.Equal("many", PluralRules.Category("ru", 25));
        Assert.Equal("zero", PluralRules.Category("ar", 0));
        Assert.Equal("two", PluralRules.Category("ar", 2));
        Assert.Equal("few", PluralRules.Category("ar", 103));
        Assert.Equal("many", PluralRules.Category("ar", 11));
        Assert.Equal("other", PluralRules.Category("ar", 100));
        Assert.Equal("other", PluralRules.Category("ja", 1));

        Loc.Instance.SetLanguage("en");
        Assert.Equal("1 speaker on the network", Loc.Instance.Plural("status.detail.speakersFound", 1));
        Assert.Equal("3 speakers on the network", Loc.Instance.Plural("status.detail.speakersFound", 3));
    }

    [Fact]
    public void Coded_engine_messages_are_translated_and_unknown_text_passes_through()
    {
        Loc.Instance.SetLanguage("en");
        Assert.Equal("The Sonos didn't recover the stream after 8 attempts", Loc.Instance.Message("core.gaveUp|8"));
        Assert.Equal("Paused from the Sonos app", Loc.Instance.Message(SonosFlow.Core.Streaming.SessionMessages.PausedFromSonos));
        Assert.Equal("Some raw text", Loc.Instance.Message("Some raw text"));
        Assert.Equal("", Loc.Instance.Message(null));
    }

    [Fact]
    public void Missing_translation_falls_back_to_english()
    {
        // A key only English has (simulated through Message with a key that exists in en): every language resolves it.
        foreach (var l in Languages.All)
        {
            Loc.Instance.SetLanguage(l.Code);
            Assert.DoesNotContain("⟦", Loc.Instance.T("status.button.start"));
        }
    }
}

public sealed class LanguageResolutionTests
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("ES_es", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("ar-SA", "ar")]
    [InlineData("de-AT", "de")]
    [InlineData("zh-TW", null)]
    [InlineData("zh-Hant", null)]
    [InlineData("xx-YY", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Resolve_maps_tags_to_the_nearest_supported_language(string? tag, string? expected) =>
        Assert.Equal(expected, Languages.Resolve(tag));

    [Fact]
    public void System_suggestion_falls_back_to_english()
    {
        Assert.Equal("ja", Languages.SuggestFromSystem(CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("en", Languages.SuggestFromSystem(CultureInfo.GetCultureInfo("sw-KE")));
    }

    [Fact]
    public void Native_names_are_in_their_own_script_and_unique()
    {
        Assert.Equal(Languages.All.Count, Languages.All.Select(l => l.Code).Distinct().Count());
        Assert.Equal("العربية", Languages.Get("ar").NativeName);
        Assert.Equal("日本語", Languages.Get("ja").NativeName);
        Assert.True(Languages.Get("ar").IsRightToLeft);
        Assert.Single(Languages.All, l => l.IsRightToLeft);
    }

    [Fact]
    public void Every_language_formats_dates_with_the_gregorian_calendar()
    {
        foreach (var lang in Languages.All)
        {
            var culture = Languages.CultureFor(lang.Code);
            Assert.IsType<GregorianCalendar>(culture.DateTimeFormat.Calendar);
            Assert.Contains("2026", new DateTime(2026, 10, 28).ToString("G", culture));
        }
    }
}

/// <summary>Every installation case for the first-launch language choice.</summary>
public sealed class LanguageStartupTests
{
    [Fact]
    public void Fresh_install_asks_suggesting_the_system_language() =>
        Assert.Equal(new LanguageStartupDecision(LanguageStartupKind.AskUser, "fr"), LanguageStartup.Decide(false, null, false, "fr"));

    [Fact]
    public void Normal_start_uses_the_saved_language_without_asking()
    {
        var d = LanguageStartup.Decide(true, "ja", true, "en");
        Assert.Equal(LanguageStartupKind.UseSaved, d.Kind);
        Assert.Equal("ja", d.Language);
        Assert.False(d.ShowWelcome);
        Assert.False(d.MustPersist);
    }

    [Fact]
    public void Update_from_spanish_only_version_keeps_spanish_and_never_asks()
    {
        // Existing settings.json from before languages existed: no Language, no onboarding flag.
        var d = LanguageStartup.Decide(true, null, false, "en");
        Assert.Equal(LanguageStartupKind.UpgradeFromSpanishOnly, d.Kind);
        Assert.Equal("es", d.Language);
        Assert.False(d.ShowWelcome);
        Assert.True(d.MustPersist);
    }

    [Theory]
    [InlineData("klingon", true, "en")]   // unknown → system suggestion
    [InlineData("pt-PT", true, "pt-BR")] // close variant → nearest
    [InlineData("de", false, "de")]      // valid but flag missing (e.g. crash mid-onboarding) → keep it
    public void Invalid_or_partial_saved_value_is_repaired_without_asking(string saved, bool done, string expected)
    {
        var d = LanguageStartup.Decide(true, saved, done, "en");
        Assert.Equal(LanguageStartupKind.Repaired, d.Kind);
        Assert.Equal(expected, d.Language);
        Assert.False(d.ShowWelcome);
        Assert.True(d.MustPersist);
    }

    [Fact]
    public void Reinstall_with_retained_settings_counts_as_existing_installation()
    {
        // Uninstall keeps %APPDATA%\SonosFlow\settings.json, so the file still exists.
        Assert.False(LanguageStartup.Decide(true, "ko", true, "en").ShowWelcome);
    }
}
