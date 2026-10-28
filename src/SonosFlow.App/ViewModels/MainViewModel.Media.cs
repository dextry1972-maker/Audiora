using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private ImageSource? _nowPlayingArtwork;
    /// <summary>Cover published by the app (decoded to 256 px wide, frozen); null when there is none or it cannot be decoded.</summary>
    public ImageSource? NowPlayingArtwork
    {
        get => _nowPlayingArtwork;
        private set { if (Set(ref _nowPlayingArtwork, value)) OnPropertyChanged(nameof(HasArtwork)); }
    }

    public bool HasArtwork => NowPlayingArtwork is not null;

    /// <summary>The info the current <see cref="NowPlayingArtwork"/> was decoded from (avoids re-decoding an unchanged cover).</summary>
    private NowPlaying? _artworkFrom;

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
        ShowArtwork(info);
    }

    private void ShowArtwork(NowPlaying? info)
    {
        if (info?.Artwork is null)
        {
            _artworkFrom = null;
            NowPlayingArtwork = null;
            return;
        }
        if (_artworkFrom is not null && info.SameArtwork(_artworkFrom)) return; // same cover, new title/artist
        _artworkFrom = info;
        NowPlayingArtwork = DecodeArtwork(info.Artwork);
    }

    /// <summary>UI thread. Undecodable or unsupported data gives null (no cover), never an exception.</summary>
    internal static ImageSource? DecodeArtwork(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // decode now, so the stream can be closed
            image.DecodePixelWidth = 256;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) // corrupt or exotic image formats throw a variety of types; a cover is never worth a crash
        {
            AppLog.Write($"medios: carátula no válida ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
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
