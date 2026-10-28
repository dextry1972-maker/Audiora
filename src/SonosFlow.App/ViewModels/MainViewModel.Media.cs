using System.ComponentModel;
using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// "Ahora suena": title and artist of the media session that belongs to the current source, as
/// Windows reports it (see <see cref="MediaSessionWatcher"/>). Display only; the stream never depends on it.
/// </summary>
public sealed partial class MainViewModel
{
    private MediaSessionWatcher? _media;

    private string? _nowPlayingTitle;
    /// <summary>Null when nothing that belongs to the source publishes a title.</summary>
    public string? NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set { if (Set(ref _nowPlayingTitle, value)) NowPlayingNotify(); }
    }

    private string? _nowPlayingArtist;
    /// <summary>Artist without YouTube's " - Topic" suffix; null when the app gives none.</summary>
    public string? NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set { if (Set(ref _nowPlayingArtist, value)) NowPlayingNotify(); }
    }

    public bool HasNowPlaying => NowPlayingTitle is not null;

    /// <summary>"Título — Artista" (or just the title).</summary>
    public string NowPlayingText => NowPlayingTitle is not { } title ? ""
        : NowPlayingArtist is { } artist ? $"{title} — {artist}" : title;

    private void NowPlayingNotify()
    {
        OnPropertyChanged(nameof(HasNowPlaying));
        OnPropertyChanged(nameof(NowPlayingText));
    }

    /// <summary>Starts following the media session of the current source. Call once, from the constructor.</summary>
    internal void InitMedia()
    {
        if (_media is not null) return;
        _media = new MediaSessionWatcher(_ui, CurrentMediaTarget());
        _media.Changed += ShowNowPlaying;
        PropertyChanged += OnSourcePropertyChanged;
        _ui.ShutdownStarted += (_, _) => _media?.Dispose();
        _media.Start();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Raised on every app-list refresh too: SetTarget ignores an unchanged target.
        if (e.PropertyName == nameof(SourceName)) _media?.SetTarget(CurrentMediaTarget());
    }

    private MediaTarget CurrentMediaTarget() => !IsAppMode
        ? MediaTarget.Pc
        : SelectedApp?.Info is { } app ? MediaTarget.App(app.ProcessId, app.ExeName, app.IsIsolatedYouTubeMusic) : MediaTarget.None;

    private void ShowNowPlaying(NowPlaying? info)
    {
        NowPlayingTitle = info?.Title;
        NowPlayingArtist = info is null ? null : CleanArtist(info.Artist);
    }

    /// <summary>YouTube's auto-generated music channels are named "Artista - Topic".</summary>
    internal static string? CleanArtist(string? artist)
    {
        var a = artist?.Trim();
        if (string.IsNullOrEmpty(a)) return null;
        if (a.EndsWith(" - Topic", StringComparison.Ordinal)) a = a[..^" - Topic".Length].TrimEnd();
        return a.Length == 0 ? null : a;
    }
}
