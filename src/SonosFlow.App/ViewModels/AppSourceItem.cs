using System.Windows.Media;
using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.ViewModels;

/// <summary>An application that can be streamed on its own.</summary>
public sealed class AppSourceItem(AudioAppInfo info) : ObservableObject
{
    private AudioAppInfo _info = info;
    private bool _isSelected;

    public AudioAppInfo Info { get => _info; set { _info = value; OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(Name)); } }
    public string Name => Info.Name;
    public string Key => Key_(Info.ExePath ?? Info.ExeName, Info.IsIsolatedYouTubeMusic);
    public bool IsActive => Info.IsActive;
    public string StateText => Info.IsActive ? "Reproduciendo" : "En silencio";
    public ImageSource? Icon { get; } = IconCache.Get(info.ExePath);
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public static string Key_(string exe, bool isolated) => (isolated ? "isolated|" : "") + exe.ToLowerInvariant();
}
