namespace SonosFlow.App.Localization;

public enum LanguageStartupKind
{
    /// <summary>Genuinely fresh install (no settings file): ask, suggesting <see cref="LanguageStartupDecision.Language"/>.</summary>
    AskUser,
    /// <summary>Normal start: the saved language is valid.</summary>
    UseSaved,
    /// <summary>Settings from a version before languages existed (Spanish-only): keep Spanish, don't interrupt.</summary>
    UpgradeFromSpanishOnly,
    /// <summary>The saved value is unknown or invalid: use the nearest supported language and repair the setting.</summary>
    Repaired,
}

public sealed record LanguageStartupDecision(LanguageStartupKind Kind, string Language)
{
    public bool ShowWelcome => Kind == LanguageStartupKind.AskUser;
    /// <summary>The settings must be saved with <see cref="Language"/> and onboarding marked done.</summary>
    public bool MustPersist => Kind is LanguageStartupKind.UpgradeFromSpanishOnly or LanguageStartupKind.Repaired;
}

/// <summary>
/// Which language to start with, and whether to show the welcome screen. Pure logic so every installation case is
/// tested: fresh install, normal start, update from a Spanish-only version, missing or invalid value, reinstall
/// with retained settings (the settings file survives uninstall, so it counts as an existing installation).
/// </summary>
public static class LanguageStartup
{
    public static LanguageStartupDecision Decide(bool settingsFileExisted, string? savedLanguage, bool onboardingDone, string systemSuggestion)
    {
        if (!settingsFileExisted) return new(LanguageStartupKind.AskUser, systemSuggestion);

        var resolved = Languages.Resolve(savedLanguage);
        if (resolved is not null && onboardingDone)
            return new(resolved == savedLanguage ? LanguageStartupKind.UseSaved : LanguageStartupKind.Repaired, resolved);

        // Existing settings but no language: an update from a version without languages (whose UI was Spanish).
        if (string.IsNullOrWhiteSpace(savedLanguage)) return new(LanguageStartupKind.UpgradeFromSpanishOnly, Languages.Source);

        // Something saved but unusable (typo, removed language, hand edit): never erase settings to re-ask.
        return new(LanguageStartupKind.Repaired, resolved ?? systemSuggestion);
    }
}
