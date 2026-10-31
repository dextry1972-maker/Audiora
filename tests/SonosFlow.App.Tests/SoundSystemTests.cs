using System.IO;
using System.Diagnostics;
using System.Text.Json;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.Sound;
using SonosFlow.App.ViewModels;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Tests;

public sealed class EnchantedSynthTests
{
    public static IEnumerable<object[]> AllSounds => SoundCatalog.All.Select(id => new object[] { id });

    [Theory, MemberData(nameof(AllSounds))]
    public void Every_sound_is_clean_quiet_and_click_free(SoundId id)
    {
        var x = EnchantedSynth.Render(id);
        Assert.NotEmpty(x);
        Assert.All(x, s => Assert.True(float.IsFinite(s)));

        float peak = x.Max(Math.Abs);
        Assert.InRange(peak, 0.05f, EnchantedSynth.PeakCeiling + 0.001f);      // audible, but with headroom (never clips)
        Assert.True(Math.Abs(x[0]) < 1e-3 && Math.Abs(x[^1]) < 1e-3);          // starts and ends at silence: no click
        Assert.True(x.Skip(1).Select((s, i) => Math.Abs(s - x[i])).Max() < 0.30f, "sample jump large enough to click");
        Assert.True(Math.Abs(x.Average()) < 0.01f, "DC offset");

        double rms = Math.Sqrt(x.Where(s => Math.Abs(s) > 0.003).Select(s => (double)s * s).Average());
        Assert.InRange(rms, 0.01, EnchantedSynth.TargetRms + 0.005);           // consistent loudness across the set
    }

    [Theory]
    [InlineData(SoundId.Connected, 400, 900)]
    [InlineData(SoundId.ConfigChanged, 80, 200)]
    [InlineData(SoundId.Navigate, 20, 80)]
    [InlineData(SoundId.ToggleOn, 80, 200)]
    [InlineData(SoundId.ToggleOff, 80, 200)]
    [InlineData(SoundId.Error, 300, 650)]
    [InlineData(SoundId.ConnectionLost, 450, 900)]
    [InlineData(SoundId.StreamStart, 450, 900)]
    [InlineData(SoundId.StreamStop, 450, 950)]
    [InlineData(SoundId.Reconnected, 250, 600)]
    public void Durations_match_the_brief(SoundId id, int minMs, int maxMs) =>
        Assert.InRange(EnchantedSynth.Render(id).Length * 1000.0 / EnchantedSynth.SampleRate, minMs, maxMs);

    [Fact]
    public void Marimba_is_the_voice_in_use()
    {
        Assert.Equal(Timbre.Marimba, EnchantedSynth.DefaultTimbre);
        Assert.True(EnchantedSynth.Render(SoundId.Connected).AsSpan().SequenceEqual(EnchantedSynth.Render(SoundId.Connected, Timbre.Marimba)));
        Assert.False(EnchantedSynth.Render(SoundId.Connected).AsSpan().SequenceEqual(EnchantedSynth.Render(SoundId.Connected, Timbre.Ocarina)));
    }

    [Fact]
    public void Every_timbre_of_every_sound_is_clean_so_any_of_them_can_be_chosen()
    {
        foreach (var timbre in Enum.GetValues<Timbre>())
            foreach (var id in SoundCatalog.All)
            {
                var x = EnchantedSynth.Render(id, timbre);
                string what = $"{id}/{timbre}";
                Assert.True(x.All(float.IsFinite), what);
                Assert.True(x.Max(Math.Abs) is > 0.05f and <= EnchantedSynth.PeakCeiling + 0.001f, what);
                Assert.True(Math.Abs(x[0]) < 1e-3 && Math.Abs(x[^1]) < 1e-3, what);
                Assert.True(x.Skip(1).Select((s, i) => Math.Abs(s - x[i])).Max() < 0.30f, what);
            }
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        foreach (var id in SoundCatalog.All)
            Assert.True(EnchantedSynth.Render(id).AsSpan().SequenceEqual(EnchantedSynth.Render(id)), id.ToString());
    }

    [Fact]
    public void Sounds_are_distinct_from_each_other()
    {
        // Error vs lost (must never be confused), start vs stop, on vs off
        foreach (var (a, b) in new[] { (SoundId.Error, SoundId.ConnectionLost), (SoundId.StreamStart, SoundId.StreamStop), (SoundId.ToggleOn, SoundId.ToggleOff), (SoundId.Connected, SoundId.Reconnected) })
        {
            var x = EnchantedSynth.Render(a); var y = EnchantedSynth.Render(b);
            int n = Math.Min(x.Length, y.Length);
            double diff = Enumerable.Range(0, n).Sum(i => Math.Abs(x[i] - y[i])) / n;
            Assert.True(diff > 0.005, $"{a} and {b} sound alike");
        }
    }

    [Fact]
    public void Rendering_the_whole_set_is_cheap()
    {
        var sw = Stopwatch.StartNew();
        var lines = SoundCatalog.All.Select(id =>
        {
            var t = sw.Elapsed.TotalMilliseconds;
            var b = EnchantedSynth.Render(id);
            return $"{id,-15} {sw.Elapsed.TotalMilliseconds - t,6:n1} ms  {b.Length / 48.0,5:n0} ms de audio  {b.Length * 4 / 1024,4} KB";
        }).ToList();
        lines.Add($"TOTAL {sw.Elapsed.TotalMilliseconds:n1} ms");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "audiora-sound-timing.txt"), lines);
        Assert.True(sw.Elapsed.TotalMilliseconds < 3000, "synthesis of the whole set must not weigh on startup");
    }

    [Fact]
    public void The_whole_set_stays_small_in_memory()
    {
        long bytes = SoundCatalog.All.Sum(id => EnchantedSynth.Render(id).Length * 4L);
        Assert.True(bytes < 2_000_000, $"{bytes} bytes");
    }
}

public sealed class SoundPolicyTests
{
    private static readonly SoundSettings On = new();
    private static SoundDecision Decide(SoundPolicy p, SoundId id, SoundSettings? s = null, bool captured = false, long now = 100_000, int voices = 0, bool preview = false) =>
        p.Decide(id, s ?? On, captured, now, voices, preview);

    [Theory]
    [InlineData(SoundId.Navigate)] [InlineData(SoundId.ToggleOn)] [InlineData(SoundId.ToggleOff)]
    public void Interface_switch_governs_navigation_and_toggles(SoundId id) =>
        Assert.Equal(SoundDecision.Disabled, Decide(new(), id, On with { Interface = false }));

    [Fact]
    public void Each_switch_governs_only_its_own_sounds()
    {
        foreach (var (id, off) in new[]
        {
            (SoundId.StreamStart, On with { Notification = false }), (SoundId.StreamStop, On with { Notification = false }), (SoundId.ConfigChanged, On with { Notification = false }),
            (SoundId.Connected, On with { Connection = false }), (SoundId.Reconnected, On with { Connection = false }),
            (SoundId.ConnectionLost, On with { ConnectionLoss = false }), (SoundId.Error, On with { Error = false }),
        })
        {
            Assert.Equal(SoundDecision.Disabled, Decide(new(), id, off));
            Assert.Equal(SoundDecision.Play, Decide(new(), id)); // all others on: plays
        }
        // Turning one off leaves the others untouched.
        Assert.Equal(SoundDecision.Play, Decide(new(), SoundId.Error, On with { ConnectionLoss = false, Connection = false }));
        Assert.Equal(SoundDecision.Play, Decide(new(), SoundId.ConnectionLost, On with { Error = false }));
    }

    [Fact]
    public void Volume_zero_is_silence_even_for_previews() =>
        Assert.All(new[] { false, true }, preview => Assert.Equal(SoundDecision.Muted, Decide(new(), SoundId.Error, On with { Volume = 0 }, preview: preview)));

    [Fact]
    public void A_sound_that_would_reach_the_Sonos_is_never_played_not_even_a_preview() =>
        Assert.All(new[] { false, true }, preview => Assert.Equal(SoundDecision.CaptureConflict, Decide(new(), SoundId.Connected, captured: true, preview: preview)));

    [Fact]
    public void Preview_plays_even_when_the_category_is_off()
    {
        var off = On with { Error = false };
        Assert.Equal(SoundDecision.Disabled, Decide(new(), SoundId.Error, off));
        Assert.Equal(SoundDecision.Play, Decide(new(), SoundId.Error, off, preview: true));
    }

    [Fact]
    public void The_same_sound_is_rate_limited_and_plays_again_after_its_interval()
    {
        var p = new SoundPolicy();
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.Navigate, now: 1000));
        Assert.Equal(SoundDecision.RateLimited, Decide(p, SoundId.Navigate, now: 1100));
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.Navigate, now: 1200));
    }

    [Fact]
    public void Fast_clicking_never_turns_into_noise()
    {
        var p = new SoundPolicy();
        int played = 0;
        // 40 toggles/navigations in 1 s (25 ms apart), alternating kinds
        for (int i = 0; i < 40; i++)
            if (Decide(p, i % 2 == 0 ? SoundId.ToggleOn : SoundId.Navigate, now: 5000 + i * 25) == SoundDecision.Play) played++;
        Assert.InRange(played, 1, 10);
    }

    [Fact]
    public void An_important_sound_hushes_micro_sounds_right_after_it()
    {
        var p = new SoundPolicy();
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.Connected, now: 1000));
        Assert.Equal(SoundDecision.Superseded, Decide(p, SoundId.Navigate, now: 1200));
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.Navigate, now: 1500));
    }

    [Fact]
    public void Voices_are_capped_but_errors_and_connection_events_always_get_through()
    {
        var p = new SoundPolicy();
        Assert.Equal(SoundDecision.Busy, Decide(p, SoundId.StreamStop, voices: SoundPolicy.MaxVoices));
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.Error, voices: SoundPolicy.MaxVoices));
        Assert.Equal(SoundDecision.Play, Decide(p, SoundId.ConnectionLost, voices: SoundPolicy.MaxVoices));
    }

    [Fact]
    public void Gain_is_monotonic_and_independent_of_the_system()
    {
        double previous = -1;
        for (int v = 0; v <= 100; v += 10) { double g = SoundSettings.GainFor(v); Assert.True(g > previous || v == 0); previous = g; }
        Assert.Equal(0, SoundSettings.GainFor(0));
        Assert.Equal(1, SoundSettings.GainFor(100), 6);
        Assert.Equal(SoundSettings.GainFor(100), SoundSettings.GainFor(250)); // clamped
    }
}

public sealed class SoundGateTests
{
    private const string Own = @"C:\Apps\SonosFlow.exe";

    [Fact]
    public void Idle_is_never_a_conflict() =>
        Assert.False(SoundGate.WouldReachSonos(false, false, null, Own, null, null, "def"));

    [Theory]
    [InlineData(null, null, "def", true)]    // both follow the default: same device → would be captured
    [InlineData("a", "a", "def", true)]      // same explicit device
    [InlineData("a", null, "a", true)]       // effects follow a default that is the captured device
    [InlineData("a", "b", "def", false)]     // effects on another device: safe
    [InlineData(null, "b", "def", false)]
    [InlineData(null, null, null, true)]     // default unknown: assume the worst
    public void Whole_pc_capture_records_the_device_the_effects_play_on(string? capture, string? effects, string? def, bool expected) =>
        Assert.Equal(expected, SoundGate.WouldReachSonos(true, false, null, Own, capture, effects, def));

    [Fact]
    public void One_app_capture_never_includes_Audiora_unless_Audiora_is_the_source()
    {
        Assert.False(SoundGate.WouldReachSonos(true, true, @"C:\Games\game.exe", Own, null, null, "def"));
        Assert.True(SoundGate.WouldReachSonos(true, true, @"D:\other\sonosflow.EXE", Own, null, "b", "def"));
    }
}

public sealed class SoundDirectorTests
{
    private static (SoundDirector Director, List<SoundId> Played) Create()
    {
        var played = new List<SoundId>();
        return (new SoundDirector(played.Add), played);
    }

    private static void Go(SoundDirector d, params UiState[] states) { foreach (var s in states) d.OnState(s); }

    [Fact]
    public void Initial_state_makes_no_sound()
    {
        var (d, played) = Create();
        Go(d, UiState.Discovering, UiState.Ready, UiState.NoSpeaker);
        Assert.Empty(played);
    }

    [Fact]
    public void Start_then_real_connection_then_stop()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting);
        Assert.Equal([SoundId.StreamStart], played); // no success before the connection is real
        Go(d, UiState.Streaming);
        Assert.Equal([SoundId.StreamStart, SoundId.Connected], played);
        Go(d, UiState.Ready);
        Assert.Equal([SoundId.StreamStart, SoundId.Connected, SoundId.StreamStop], played);
    }

    [Fact]
    public void A_failed_start_is_an_error_and_never_a_success()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting, UiState.Error);
        Assert.Equal([SoundId.StreamStart, SoundId.Error], played);
    }

    [Fact]
    public void A_cancelled_start_is_not_a_stop()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting, UiState.Ready);
        Assert.Equal([SoundId.StreamStart], played);
    }

    [Fact]
    public void One_lost_per_outage_and_reconnected_only_after_a_real_recovery()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting, UiState.Streaming);
        played.Clear();
        Go(d, UiState.Recovering, UiState.Recovering, UiState.Recovering); // repeated attempts: no repeated sound
        Assert.Equal([SoundId.ConnectionLost], played);
        Go(d, UiState.Streaming);
        Assert.Equal([SoundId.ConnectionLost, SoundId.Reconnected], played);
        Go(d, UiState.Recovering, UiState.Streaming); // a new outage is a new sound
        Assert.Equal([SoundId.ConnectionLost, SoundId.Reconnected, SoundId.ConnectionLost, SoundId.Reconnected], played);
    }

    [Fact]
    public void Giving_up_while_recovering_is_a_stop_or_an_error_and_never_a_reconnection()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting, UiState.Streaming, UiState.Recovering);
        played.Clear();
        Go(d, UiState.Error);
        Assert.Equal([SoundId.Error], played);
    }

    [Fact]
    public void A_failed_session_makes_one_sound_the_error_and_not_a_stop_before_it()
    {
        var (d, played) = Create();
        Go(d, UiState.Ready, UiState.Starting, UiState.Streaming);
        played.Clear();
        d.Failing = true;               // the view model is tearing the failed session down…
        Go(d, UiState.Ready);
        d.Failing = false;
        Go(d, UiState.Error);           // …and then reports the error
        Assert.Equal([SoundId.Error], played);
    }

    [Fact]
    public void The_first_start_is_announced_even_before_any_other_state_was_seen()
    {
        var (d, played) = Create();
        d.OnState(UiState.Starting);
        Assert.Equal([SoundId.StreamStart], played);
    }

    [Fact]
    public void Automatic_starts_are_silent_but_a_later_outage_still_is_announced()
    {
        var (d, played) = Create();
        d.Quiet = true;
        Go(d, UiState.Ready, UiState.Starting, UiState.Streaming);
        d.Quiet = false;
        Assert.Empty(played);
        Go(d, UiState.Recovering);
        Assert.Equal([SoundId.ConnectionLost], played);
    }
}

public sealed class SoundServiceTests
{
    private sealed class FakeOutput : ISoundOutput
    {
        public readonly List<(float[] Buffer, float Gain, string? Device)> Plays = [];
        public int Voices { get; set; }
        public bool Disposed;
        public int ActiveVoices => Voices;
        public int Cancels;
        public Func<string, bool>? LastVeto;
        public void Play(float[] mono, float gain, string? deviceId, Func<string, bool>? veto = null) { LastVeto = veto; Plays.Add((mono, gain, deviceId)); }
        public void CancelAll() => Cancels++;
        public void Dispose() => Disposed = true;
    }

    private static (SoundService Service, FakeOutput Output, List<Action> Queue) Create(SoundSettings? settings = null, bool captured = false, Func<long>? clock = null)
    {
        var output = new FakeOutput();
        var queue = new List<Action>();
        var service = new SoundService(() => settings ?? new SoundSettings(), _ => captured, output, clock ?? (() => 100_000), queue.Add);
        return (service, output, queue);
    }

    [Fact]
    public void Play_returns_at_once_and_never_blocks_the_caller_with_audio_work()
    {
        var (service, output, queue) = Create();
        Assert.Equal(SoundDecision.Play, service.Play(SoundId.Connected));
        Assert.Empty(output.Plays);   // nothing ran on the caller's thread
        Assert.Single(queue);
        queue[0]();
        Assert.Single(output.Plays);
    }

    [Fact]
    public void Each_event_plays_only_its_own_sound_at_the_independent_volume()
    {
        var (service, output, queue) = Create(new SoundSettings(Volume: 50, DeviceId: "dev-x"));
        service.Play(SoundId.Error);
        queue.ForEach(a => a());
        var play = Assert.Single(output.Plays);
        Assert.True(play.Buffer.AsSpan().SequenceEqual(EnchantedSynth.Render(SoundId.Error)));
        Assert.False(play.Buffer.AsSpan().SequenceEqual(EnchantedSynth.Render(SoundId.ConnectionLost)));
        Assert.Equal((float)SoundSettings.GainFor(50), play.Gain, 5);
        Assert.Equal("dev-x", play.Device);
    }

    [Fact]
    public void Volume_changes_take_effect_on_the_next_sound()
    {
        int volume = 20;
        var output = new FakeOutput();
        long now = 100_000;
        var service = new SoundService(() => new SoundSettings(Volume: volume), _ => false, output, () => now += 5000, a => a());
        service.Play(SoundId.Error);
        volume = 90;
        service.Play(SoundId.Error);
        Assert.Equal(2, output.Plays.Count);
        Assert.True(output.Plays[1].Gain > output.Plays[0].Gain * 2);
    }

    [Fact]
    public void Nothing_reaches_the_output_when_it_would_be_captured_by_the_stream()
    {
        var (service, output, queue) = Create(captured: true);
        Assert.Equal(SoundDecision.CaptureConflict, service.Play(SoundId.Connected));
        Assert.Equal(SoundDecision.CaptureConflict, service.Play(SoundId.Connected, preview: true));
        Assert.Empty(queue);
        Assert.Empty(output.Plays);
    }

    [Fact]
    public void A_busy_output_drops_low_priority_sounds_but_never_an_error()
    {
        var (service, output, queue) = Create();
        output.Voices = SoundPolicy.MaxVoices;
        Assert.Equal(SoundDecision.Busy, service.Play(SoundId.StreamStop));
        Assert.Empty(queue);
        Assert.Equal(SoundDecision.Play, service.Play(SoundId.Error));
    }

    [Fact]
    public void Starting_a_stream_cuts_sounds_still_playing_only_where_it_would_capture_them()
    {
        var (safe, safeOut, _) = Create(captured: false);
        safe.CancelIfCaptured();
        Assert.Equal(0, safeOut.Cancels);
        var (risky, riskyOut, _) = Create(captured: true);
        risky.CancelIfCaptured();
        Assert.Equal(1, riskyOut.Cancels);
    }

    [Fact]
    public void The_final_check_is_built_when_the_sound_is_requested_and_handed_to_the_output()
    {
        var output = new FakeOutput();
        int built = 0;
        var service = new SoundService(() => new SoundSettings(), _ => false, output, () => 100_000, a => a(), () => { built++; return id => id == "captured"; });
        service.Play(SoundId.Error);
        Assert.Equal(1, built);
        Assert.NotNull(output.LastVeto);
        Assert.True(output.LastVeto!("captured"));
        Assert.False(output.LastVeto!("other"));
    }

    [Fact]
    public void Quiesce_silences_everything_for_shutdown()
    {
        var (service, _, queue) = Create();
        service.Quiesce();
        Assert.Equal(SoundDecision.Disabled, service.Play(SoundId.StreamStop));
        Assert.Empty(queue);
    }

    [Fact]
    public void Dispose_releases_the_output()
    {
        var (service, output, _) = Create();
        service.Dispose();
        Assert.True(output.Disposed);
    }

    [Fact]
    public async Task Preload_renders_every_sound_ahead_of_time()
    {
        var (service, _, _) = Create();
        await service.PreloadAsync();
        Assert.Equal(SoundCatalog.All.Count, service.LoadedSounds);
    }
}

public sealed class SoundSettingsPersistenceTests
{
    [Fact]
    public void A_fresh_install_has_gentle_defaults_and_follows_the_system_device()
    {
        var s = new AppSettings();
        Assert.True(s.InterfaceSounds && s.NotificationSounds && s.ConnectionSounds && s.ConnectionLossSounds && s.ErrorSounds);
        Assert.Equal(60, s.SoundsVolume);
        Assert.Null(s.SoundsDeviceId);
    }

    [Fact]
    public void Sound_settings_survive_a_restart_and_older_files_still_load()
    {
        var s = new AppSettings
        {
            InterfaceSounds = false, NotificationSounds = false, ConnectionSounds = false, ConnectionLossSounds = false,
            ErrorSounds = false, SoundsVolume = 25, SoundsDeviceId = "{0.0.0.00000000}.abc",
        };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.False(back.InterfaceSounds || back.NotificationSounds || back.ConnectionSounds || back.ConnectionLossSounds || back.ErrorSounds);
        Assert.Equal(25, back.SoundsVolume);
        Assert.Equal("{0.0.0.00000000}.abc", back.SoundsDeviceId);

        var old = JsonSerializer.Deserialize<AppSettings>("""{ "Port": 8780, "MuteLocal": true }""")!; // written before the sounds existed
        Assert.True(old.InterfaceSounds);
        Assert.Equal(60, old.SoundsVolume);
    }
}

public sealed class SoundLimiterTests
{
    [Fact]
    public void The_mix_never_reaches_full_scale_and_leaves_normal_levels_alone()
    {
        Assert.Equal(0.5f, SoundLimiter.Limit(0.5f));
        Assert.Equal(-0.8f, SoundLimiter.Limit(-0.8f));
        foreach (float x in new[] { 0.9f, 1.5f, 3f, 100f })
        {
            Assert.InRange(SoundLimiter.Limit(x), 0.8f, 0.9901f);
            Assert.Equal(-SoundLimiter.Limit(x), SoundLimiter.Limit(-x));
        }
        Assert.True(SoundLimiter.Limit(1.5f) > SoundLimiter.Limit(0.9f)); // monotonic: no inversion
    }
}

public sealed class WasapiEffectOutputTests
{
    [Fact]
    public void A_veto_prevents_the_sound_from_ever_starting()
    {
        using var output = new WasapiEffectOutput();
        output.Play(EnchantedSynth.Render(SoundId.ToggleOn), 0f, null, _ => true);
        Assert.Equal(0, output.ActiveVoices);
    }

    [Fact]
    public void Cancel_all_drops_the_voices_and_the_output_keeps_working()
    {
        using var output = new WasapiEffectOutput();
        output.Play(EnchantedSynth.Render(SoundId.Connected), 0f, null);
        output.CancelAll();
        Assert.Equal(0, output.ActiveVoices);
        output.Play(EnchantedSynth.Render(SoundId.ToggleOn), 0f, null);
        Assert.True(SpinWait.SpinUntil(() => output.ActiveVoices == 0, 3000));
    }

    private static float[] Silent(double seconds) => new float[(int)(seconds * EnchantedSynth.SampleRate)];

    [Fact]
    public void Real_output_plays_silently_closes_when_idle_and_releases_everything()
    {
        using var output = new WasapiEffectOutput();
        output.Play(EnchantedSynth.Render(SoundId.ToggleOn), 0f, null); // gain 0: a real WASAPI session, no audible sound
        // On a PC without an output device nothing can open: that is the "device missing" path and must not throw.
        Assert.InRange(output.ActiveVoices, 0, 1);
        SpinWait.SpinUntil(() => output.ActiveVoices == 0, 3000);
        Assert.Equal(0, output.ActiveVoices);
    }

    [Fact]
    public void An_unknown_device_falls_back_without_throwing_and_does_not_leak()
    {
        using var current = Process.GetCurrentProcess();
        current.Refresh();
        int handlesBefore = current.HandleCount;
        for (int i = 0; i < 12; i++)
        {
            using var output = new WasapiEffectOutput();
            output.Play(Silent(0.05), 0f, i % 2 == 0 ? "{no-such-device}" : null);
            Thread.Sleep(30);
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        current.Refresh();
        Assert.True(current.HandleCount - handlesBefore < 60, $"handles {handlesBefore} → {current.HandleCount}");
    }

    [Fact]
    public void Overlapping_sounds_share_one_client_and_all_end()
    {
        using var output = new WasapiEffectOutput();
        for (int i = 0; i < 6; i++) output.Play(Silent(0.2), 0f, null);
        Assert.InRange(output.ActiveVoices, 0, 6);
        Assert.True(SpinWait.SpinUntil(() => output.ActiveVoices == 0, 4000));
    }
}

/// <summary>
/// Real audio, silent for the user: the tone and the effects play on "Steam Streaming Speakers" (a virtual output
/// nobody hears). Proves that a per-app capture never contains Audiora's effects and that a whole-device capture
/// would (which is exactly why the gate silences them there). Skipped silently where that output doesn't exist.
/// </summary>
public sealed class SoundIsolationTests
{
    private static string? FindCaptureCheck()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SonosFlow.sln")))
            {
                var tool = Path.Combine(dir.FullName, "tools", "SonosFlow.CaptureCheck", "bin", "Release", "net8.0-windows", "SonosFlow.CaptureCheck.exe");
                return File.Exists(tool) ? tool : null;
            }
        return null;
    }

    [Fact]
    public async Task Effects_never_enter_a_per_app_capture_but_would_enter_a_whole_device_capture()
    {
        var steam = AudioDevices.ListOutputs().FirstOrDefault(d => d.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));
        var tool = FindCaptureCheck();
        if (steam is null || tool is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;

        using var child = Process.Start(new ProcessStartInfo(tool, "tone-quiet 200 40 \"Steam Streaming\"") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await Task.Delay(2500);
            using var perApp = LoopbackSource.ForProcess(child.Id, "tone", 48000);
            using var wholeDevice = new LoopbackSource(AudioDevices.Resolve(steam.Id));
            perApp.Start(); wholeDevice.Start();
            await Task.Delay(1200);

            using var output = new WasapiEffectOutput();
            float peakApp = 0, peakDevice = 0;
            for (int i = 0; i < 3; i++)
            {
                output.Play(EnchantedSynth.Render(SoundId.Connected), 0.5f, steam.Id);
                for (int k = 0; k < 50; k++)
                {
                    await Task.Delay(20);
                    peakApp = Math.Max(peakApp, perApp.LastPeak);
                    peakDevice = Math.Max(peakDevice, wholeDevice.LastPeak);
                }
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "audiora-isolation.txt"),
                $"capture per app peak={peakApp:g4} ({20 * Math.Log10(Math.Max(peakApp, 1e-9)):n1} dBFS); whole device peak={peakDevice:g4} ({20 * Math.Log10(Math.Max(peakDevice, 1e-9)):n1} dBFS)");
            Assert.True(peakApp < 0.01f, $"effects leaked into the per-app capture: peak {peakApp}");   // only the −60 dBFS tone
            Assert.True(peakDevice > 0.05f, $"the whole-device capture should have heard the effects: peak {peakDevice}");
        }
        finally { try { child.Kill(true); } catch (InvalidOperationException) { } }
    }
}

/// <summary>Writes one audition WAV per timbre when AUDIORA_DEMO_DIR is set; otherwise does nothing.</summary>
public sealed class SoundDemoExport
{
    [Fact]
    public void Export_timbre_demos_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("AUDIORA_DEMO_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        SoundId[] order = [SoundId.Connected, SoundId.ConnectionLost, SoundId.Error, SoundId.StreamStart, SoundId.StreamStop,
            SoundId.ConfigChanged, SoundId.ToggleOn, SoundId.ToggleOff, SoundId.Navigate, SoundId.Reconnected];
        foreach (var timbre in Enum.GetValues<Timbre>())
        {
            var samples = new List<float>();
            foreach (var id in order)
            {
                samples.AddRange(EnchantedSynth.Render(id, timbre).Select(x => x * 0.85f));
                samples.AddRange(new float[(int)(EnchantedSynth.SampleRate * 0.9)]); // a pause between sounds
            }
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{(int)timbre + 1}-{timbre}.wav")));
            int bytes = samples.Count * 4;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(EnchantedSynth.SampleRate); w.Write(EnchantedSynth.SampleRate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var x in samples) { short v = (short)Math.Round(Math.Clamp(x, -1f, 1f) * short.MaxValue); w.Write(v); w.Write(v); }
        }
    }
}
