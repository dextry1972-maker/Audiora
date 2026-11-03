using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.ViewModels;

/// <summary>Settings → Personalización: the name used by the welcome and the greeting in the sidebar. Presentation only.</summary>
public sealed partial class MainViewModel
{
    public const int MaxUserNameLength = 40;

    /// <summary>Name as typed, cleaned: no control characters, trimmed, at most <see cref="MaxUserNameLength"/> characters.</summary>
    public static string CleanUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Where(c => !char.IsControl(c)).ToArray();
        var text = new string(chars).Trim();
        return text.Length <= MaxUserNameLength ? text : text[..MaxUserNameLength].TrimEnd();
    }

    public string UserName
    {
        get => _settings.UserName ?? "";
        set
        {
            var clean = CleanUserName(value);
            if (clean == (_settings.UserName ?? "")) return;
            _settings.UserName = clean.Length == 0 ? null : clean;
            _settings.NameOnboardingDone = true;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(GreetingText));
            OnPropertyChanged(nameof(HasGreeting));
        }
    }

    public bool PersonalizedWelcome
    {
        get => _settings.PersonalizedWelcome;
        set
        {
            if (_settings.PersonalizedWelcome == value) return;
            _settings.PersonalizedWelcome = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGreeting));
        }
    }

    /// <summary>Whether the sidebar greets by name.</summary>
    public bool HasGreeting => PersonalizedWelcome && !string.IsNullOrEmpty(_settings.UserName);

    /// <summary>"Hola, Elian". The name is isolated (first-strong isolate) so it stays whole inside right-to-left text.</summary>
    public string GreetingText => L.F("main.greeting", "\u2068" + (_settings.UserName ?? "") + "\u2069");
}
