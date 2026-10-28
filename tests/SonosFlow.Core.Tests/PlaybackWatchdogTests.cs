using SonosFlow.Core.Streaming;

namespace SonosFlow.Core.Tests;

public sealed class PlaybackWatchdogTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    /// <summary>Feeds a healthy player from t=0 up to (excluding) <paramref name="until"/>; position advances 1 s/s.</summary>
    private static void PlayHealthy(PlaybackWatchdog w, int until)
    {
        for (int t = 0; t < until; t++)
            Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(t), "PLAYING", 1, S(0.01), S(t)));
    }

    [Fact]
    public void Silence_is_healthy_because_bytes_and_position_keep_flowing()
    {
        var w = new PlaybackWatchdog(1000);
        PlayHealthy(w, 3600); // an hour, whatever the content: silence frames are always sent
    }

    [Fact]
    public void Position_that_stops_advancing_is_frozen_after_8_s()
    {
        var w = new PlaybackWatchdog(1000);
        PlayHealthy(w, 30);
        for (int t = 30; t < 37; t++) Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(t), "PLAYING", 1, S(0.01), S(29)));
        Assert.Equal(PlaybackVerdict.Frozen, w.Observe(S(37), "PLAYING", 1, S(0.01), S(29)));
    }

    [Fact]
    public void Unknown_position_never_counts_as_frozen()
    {
        var w = new PlaybackWatchdog(1000);
        for (int t = 0; t < 120; t++) Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(t), "PLAYING", 1, S(0.01), null));
    }

    [Fact]
    public void Player_that_stops_taking_audio_is_detected_after_margin_plus_3_s()
    {
        var w = new PlaybackWatchdog(2000);
        Assert.Equal(S(5), w.NotConsumingAfter.Subtract(S(0))); // 2 s margin + 3 s
        PlayHealthy(w, 20);
        Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(20), "PLAYING", 1, S(4.9), S(20)));
        Assert.Equal(PlaybackVerdict.NotConsuming, w.Observe(S(21), "PLAYING", 1, S(5), S(21)));
    }

    [Fact]
    public void Warmup_after_a_reconnect_hides_stale_signals()
    {
        var w = new PlaybackWatchdog(1000);
        PlayHealthy(w, 20);
        w.Reset(S(20));
        Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(25), "PLAYING", 1, S(9), S(0)));
        Assert.Equal(PlaybackVerdict.NotConsuming, w.Observe(S(31), "PLAYING", 1, S(9), S(0)));
    }

    [Fact]
    public void Stopped_paused_or_no_client_is_not_playing_immediately()
    {
        var w = new PlaybackWatchdog(1000);
        Assert.Equal(PlaybackVerdict.NotPlaying, w.Observe(S(1), "STOPPED", 1, S(0), null));
        Assert.Equal(PlaybackVerdict.NotPlaying, w.Observe(S(2), "PAUSED_PLAYBACK", 1, S(0), null));
        Assert.Equal(PlaybackVerdict.NotPlaying, w.Observe(S(3), "PLAYING", 0, S(0), null));
    }

    [Fact]
    public void Transitioning_for_more_than_15_s_is_not_playing()
    {
        var w = new PlaybackWatchdog(1000);
        for (int t = 0; t <= 15; t++) Assert.Equal(PlaybackVerdict.Healthy, w.Observe(S(t), "TRANSITIONING", 1, S(0), null));
        Assert.Equal(PlaybackVerdict.NotPlaying, w.Observe(S(16), "TRANSITIONING", 1, S(0), null));
    }

    [Theory]
    [InlineData("http://192.168.137.1:8780/sonosflow.wav", true)]
    [InlineData("", true)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a123?sid=9", false)]
    [InlineData("x-rincon-mp3radio://stream.example/live", false)]
    public void Recognises_our_stream_uri(string current, bool ours) =>
        Assert.Equal(ours, PlaybackWatchdog.IsOurStream(current, "http://192.168.137.1:8780/sonosflow.wav"));
}

public sealed class RecoveryPolicyTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Short_glitches_under_5_s_do_not_reconnect()
    {
        var p = new RecoveryPolicy();
        for (int t = 0; t < 5; t++) Assert.Equal(RecoveryDecision.None, p.Observe(S(t), healthy: false));
        Assert.Equal(RecoveryDecision.None, p.Observe(S(5), healthy: true));
        Assert.Equal(0, p.Attempts);
    }

    [Fact]
    public void Attempts_back_off_and_give_up_after_8()
    {
        var p = new RecoveryPolicy();
        var attempts = new List<int>();
        RecoveryDecision d = RecoveryDecision.None;
        int t = 0;
        for (; t < 1000 && d != RecoveryDecision.GiveUp; t++)
        {
            d = p.Observe(S(t), healthy: false);
            if (d == RecoveryDecision.Reconnect) attempts.Add(t);
        }
        Assert.Equal([5, 10, 20, 40, 80, 140, 200, 260], attempts);
        Assert.Equal(RecoveryDecision.GiveUp, d);
        Assert.True(t < 600, "gives up well before 10 min");
    }

    [Fact]
    public void Thirty_seconds_healthy_resets_the_count()
    {
        var p = new RecoveryPolicy();
        for (int t = 0; t <= 5; t++) p.Observe(S(t), healthy: false);
        Assert.Equal(1, p.Attempts);
        for (int t = 6; t <= 36; t++) p.Observe(S(t), healthy: true);
        Assert.Equal(0, p.Attempts);
    }
}
