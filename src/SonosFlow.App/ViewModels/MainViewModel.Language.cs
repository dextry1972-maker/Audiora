using SonosFlow.App.Infrastructure;
using SonosFlow.App.Localization;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// UI language. Changing it only swaps text: the stream, the selected speaker and source, the volume and every other
/// setting stay as they are, and the welcome screen is never shown again.
/// </summary>
public sealed partial class MainViewModel
{
    private static Loc L => Loc.Instance;

    /// <summary>Last coded problem received from the session (language-independent, for logs and de-duplication).</summary>
    private string? _lastErrorCode;

    public IReadOnlyList<LanguageInfo> LanguageChoices => Languages.All;

    private static string RedirectStateFile => SonosFlow.Core.Streaming.StreamingSession.RedirectStateFileFor(AppPaths.GuardStateFile);

    public string SelectedLanguageCode
    {
        get => Loc.Instance.Code;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Loc.Instance.Code) return;
            Loc.Instance.SetLanguage(value);
            if (AppSettings.PersistenceEnabled)
            {
                _settings.Language = Loc.Instance.Code;
                _settings.LanguageOnboardingDone = true;
                _settings.Save();
            }
            AppLog.Write($"idioma: {Loc.Instance.Code}");
        }
    }

    /// <summary>Computed texts (status, buttons, source…) are rebuilt in the new language.</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        foreach (var a in Apps) a.RefreshText();
        foreach (var c in CloseActions.Concat(PrebufferChoices).Concat(SleepChoices)) c.RefreshText();
    }
}
