using SonosFlow.Core.Diagnostics;

namespace SonosFlow.Core.Tests;

public sealed class PeriodicityAnalyzerTests
{
    private static List<double> Every(double period, int count, double jitter = 0, double first = 40)
    {
        var rnd = new Random(7);
        return Enumerable.Range(0, count).Select(i => first + i * period + (jitter == 0 ? 0 : (rnd.NextDouble() * 2 - 1) * jitter)).ToList();
    }

    [Theory]
    [InlineData(300)] [InlineData(330)] [InlineData(360)]
    public void Detects_a_regular_5_to_6_minute_pattern(double period)
    {
        var r = PeriodicityAnalyzer.Analyze(Every(period, 8, jitter: 6));
        Assert.True(r.Enough && r.Periodic);
        Assert.InRange(r.MedianIntervalSec, period - 10, period + 10);
        Assert.True(r.Near5To6Min);
    }

    [Fact]
    public void A_regular_pattern_at_another_period_is_not_reported_as_5_to_6_minutes()
    {
        var r = PeriodicityAnalyzer.Analyze(Every(90, 12, jitter: 3));
        Assert.True(r.Periodic);
        Assert.False(r.Near5To6Min);
    }

    [Fact]
    public void Irregular_episodes_are_not_a_pattern()
    {
        // intervals taken from real sessions: 152, 84, 62, 53, 314 s, and 5316, 186 s
        var starts = new List<double> { 0, 152, 236, 298, 351, 665 };
        Assert.False(PeriodicityAnalyzer.Analyze(starts).Periodic);
        Assert.False(PeriodicityAnalyzer.Analyze([0, 5316, 5502, 9000, 9100]).Periodic);
    }

    [Fact]
    public void Fewer_than_four_episodes_never_claim_a_period_even_if_perfectly_regular()
    {
        var r = PeriodicityAnalyzer.Analyze([10, 340, 670]);
        Assert.False(r.Enough);
        Assert.False(r.Periodic);
        Assert.Contains("insuficientes", r.ToString());
    }

    [Fact]
    public void Order_of_the_input_does_not_matter()
    {
        var shuffled = Every(330, 8).OrderBy(_ => Guid.NewGuid()).ToList();
        Assert.True(PeriodicityAnalyzer.Analyze(shuffled).Near5To6Min);
    }
}

public sealed class EpisodeTrackerTests
{
    [Fact]
    public void Anomalies_close_together_are_one_episode_and_distant_ones_are_new()
    {
        var t = new EpisodeTracker(mergeGapSec: 8);
        Assert.True(t.Anomaly(100));
        Assert.False(t.Anomaly(101));
        Assert.False(t.Anomaly(107));   // 6 s after the last: same episode
        Assert.True(t.Anomaly(130));    // 23 s later: a new one
        Assert.Equal([100, 130], t.Starts);
    }
}

public sealed class DriftEstimatorTests
{
    private static (double Ppm, double UncertaintyPpm)? Run(double driftPpm, int seconds, Func<int, bool>? continuous = null)
    {
        var d = new DriftEstimator();
        long frames = (long)Math.Round(48000 * (1 + driftPpm / 1e6));
        for (int s = 1; s <= seconds; s++) d.AddSecond(s, frames, continuous?.Invoke(s) ?? true);
        return d.Estimate();
    }

    [Fact]
    public void Nothing_is_claimed_before_two_minutes_of_continuous_capture() => Assert.Null(Run(200, 100));

    [Theory]
    [InlineData(200)] [InlineData(-300)] [InlineData(0)]
    public void Recovers_positive_negative_and_no_drift(double ppm)
    {
        var e = Run(ppm, 900)!.Value;
        double expected = (Math.Round(48000 * (1 + ppm / 1e6)) / 48000 - 1) * 1e6; // frames are whole numbers: that is the true drift fed in
        Assert.InRange(e.Ppm, expected - 0.01, expected + 0.01);
        Assert.InRange(e.UncertaintyPpm, 0, 100);    // and it states how far a real, block-quantised measurement could be off
    }

    [Fact]
    public void Silence_and_pacer_fills_are_not_drift()
    {
        // every other 40 s: no capture at all (loopback with nothing to play) — only the continuous runs count
        var e = Run(150, 900, s => s % 80 < 40)!.Value;
        double expected = (Math.Round(48000 * (1 + 150 / 1e6)) / 48000 - 1) * 1e6;
        Assert.InRange(e.Ppm, expected - 0.01, expected + 0.01);
    }

    [Fact]
    public void Runs_shorter_than_thirty_seconds_are_ignored()
    {
        var d = new DriftEstimator();
        for (int s = 1; s <= 20; s++) d.AddSecond(s, 48000 * 2, true); // a burst of nonsense in a short run
        d.AddSecond(21, 0, false);
        Assert.Equal(0, d.ObservedSec);
    }
}

public sealed class IntervalStatsTests
{
    [Fact]
    public void Counts_mean_max_and_histogram_and_resets_on_take()
    {
        var s = new IntervalStats();
        long Ms(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
        s.Add(Ms(3)); s.Add(Ms(12)); s.Add(Ms(700));
        var snap = s.Take();
        Assert.Equal(3, snap.Count);
        Assert.InRange(snap.MaxMs, 699, 701);
        Assert.InRange(snap.MeanMs, 238, 240);
        Assert.Equal(1, snap.Histogram[0]);                       // ≤ 5 ms
        Assert.Equal(1, snap.Histogram[2]);                       // 10–20 ms
        Assert.Equal(1, snap.Histogram[^1]);                      // > 640 ms
        Assert.Equal(0, s.Take().Count);
    }
}

public sealed class StreamDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audiora-diag-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static long Ms(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);

    [Fact]
    public void A_quiet_second_has_no_anomaly()
    {
        using var d = new StreamDiagnostics(null, 48000, "app");
        for (int i = 0; i < 100; i++) d.CaptureBlock(480, audible: true);
        var s = d.Tick(backlogMs: 10, bufferEstS: 1.4, sendRateKBps: 192);
        Assert.False(s.Anomaly);
        Assert.Equal(100, s.CaptureBlocks);
        Assert.Equal(48000, s.CaptureFrames);
        Assert.Empty(d.EpisodeStarts);
    }

    [Fact]
    public void A_pacer_fill_right_after_audio_is_suspicious_but_one_after_silence_is_not()
    {
        using var silent = new StreamDiagnostics(null, 48000, "system");
        silent.CaptureBlock(480, audible: false);
        silent.PacerFill(19200);
        var quiet = silent.Tick(0, null, 0);
        Assert.Equal(19200, quiet.FillBytes);
        Assert.Equal(0, quiet.SuspiciousFillBytes);
        Assert.False(quiet.Anomaly);                              // nothing was playing: a legitimate silence

        using var playing = new StreamDiagnostics(null, 48000, "system");
        playing.CaptureBlock(480, audible: true);
        playing.PacerFill(19200);                                 // audio was flowing, then the pacer had to fill
        var s = playing.Tick(0, null, 192);
        Assert.Equal(19200, s.SuspiciousFillBytes);
        Assert.Contains(s.Reasons, r => r.StartsWith("relleno_sospechoso"));
    }

    [Fact]
    public void A_pause_on_purpose_is_never_reported_as_a_capture_problem()
    {
        using var d = new StreamDiagnostics(null, 48000, "app");
        d.CaptureBlock(480, audible: true);
        d.PacerFill(19200);
        Assert.False(d.Tick(0, null, 192, paused: true).Anomaly);
    }

    [Fact]
    public void Each_kind_of_trouble_is_named()
    {
        using var d = new StreamDiagnostics(null, 48000, "app");
        d.ServerWrite(Ms(400));
        d.Dropped(19200);
        var s = d.Tick(backlogMs: 900, bufferEstS: 0.2, sendRateKBps: 40);
        Assert.Contains(s.Reasons, r => r.StartsWith("escritura_lenta"));
        Assert.Contains(s.Reasons, r => r.StartsWith("descartes"));
        Assert.Contains(s.Reasons, r => r.StartsWith("cola"));
        Assert.Equal(1, s.DroppedChunks);
        Assert.Equal(19200, s.DroppedBytes);
    }

    [Fact]
    public void Anomalies_in_the_same_moment_are_one_episode_with_one_event()
    {
        using var d = new StreamDiagnostics(null, 48000, "app");
        for (int i = 0; i < 4; i++) d.Tick(backlogMs: 900, bufferEstS: null, sendRateKBps: 0);
        Assert.Single(d.EpisodeStarts);
        Assert.Single(d.RecentEvents(), e => e.Kind == "episodio");
        Assert.Contains("insuficientes", d.Periodicity.ToString());
    }

    [Fact]
    public void The_file_holds_events_and_summaries_but_no_addresses_and_no_audio()
    {
        using (var d = new StreamDiagnostics(_dir, 48000, "system"))
        {
            for (int i = 0; i < 25; i++) { d.CaptureBlock(480, true); d.Tick(10, 1.4, 192); }
            d.Event("http_abierta", "cliente#1 GET margen=1000ms");
            d.Finish();
            Assert.NotNull(d.FilePath);
        }
        var file = Directory.GetFiles(_dir, "diag-*.jsonl").Single();
        var text = File.ReadAllText(file);
        Assert.Contains("\"k\":\"inicio\"", text);
        Assert.Contains("\"k\":\"resumen10s\"", text);
        Assert.Contains("\"k\":\"resumen\"", text);
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
        foreach (var line in File.ReadLines(file)) System.Text.Json.JsonDocument.Parse(line).Dispose(); // every line is valid JSON
    }

    [Fact]
    public void The_file_has_a_size_cap_and_says_so()
    {
        using (var d = new StreamDiagnostics(_dir, 48000, "app", maxFileBytes: 4096))
            for (int i = 0; i < 500; i++) d.Event("nota", new string('x', 60));
        var file = Directory.GetFiles(_dir, "diag-*.jsonl").Single();
        Assert.True(new FileInfo(file).Length < 4096 + 200);
        Assert.Contains("truncado", File.ReadAllText(file));
    }

    [Fact]
    public void Old_diagnostic_files_are_pruned_but_nothing_else_is_touched()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 40; i++)
        {
            var p = Path.Combine(_dir, $"diag-2020010{i % 10}-0000{i:00}-1.jsonl");
            File.WriteAllText(p, "{}");
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-100 + i));
        }
        File.WriteAllText(Path.Combine(_dir, "stream-keep.csv"), "x");
        File.WriteAllText(Path.Combine(_dir, "app-events.log"), "x");
        using (new StreamDiagnostics(_dir, 48000, "app")) { }
        Assert.InRange(Directory.GetFiles(_dir, "diag-*.jsonl").Length, 1, 30);
        Assert.True(File.Exists(Path.Combine(_dir, "stream-keep.csv")));
        Assert.True(File.Exists(Path.Combine(_dir, "app-events.log")));
    }

    [Fact]
    public void An_unwritable_location_disables_the_file_and_never_throws()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"audiora-block-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where a directory should be");
        try
        {
            using var d = new StreamDiagnostics(Path.Combine(blocker, "sub"), 48000, "app");
            Assert.True(d.FileDisabled);
            d.Event("nota", "still works in memory");
            d.Tick(0, null, 0);
            Assert.NotEmpty(d.RecentEvents());
        }
        finally { File.Delete(blocker); }
    }

    [Theory]
    [InlineData("no se pudo volver a capturar «Mi Sala»: Access denied", "«…»")]
    [InlineData("2 instancia(s) más de chrome.exe tampoco sonará(n)", "<programa>")]
    [InlineData("conexión 192.168.137.158:37456 GET /x", "<ip>")]
    [InlineData(@"fallo en C:\Users\Persona\AppData\x.log", "<ruta>")]
    [InlineData("salida {0.0.0.00000000}.{8f0d8b58-aaaa-bbbb-cccc-0123456789ab}", "<id>")]
    public void Free_text_never_carries_names_programs_addresses_paths_or_device_ids(string raw, string marker)
    {
        var clean = StreamDiagnostics.Sanitize(raw);
        Assert.Contains(marker, clean);
        Assert.DoesNotContain("Mi Sala", clean);
        Assert.DoesNotContain("chrome", clean);
        Assert.DoesNotContain("192.168", clean);
        Assert.DoesNotContain("Persona", clean);
        Assert.DoesNotContain("8f0d8b58", clean);
    }

    [Fact]
    public void Events_written_to_the_file_are_sanitised_and_long_text_is_cut()
    {
        using (var d = new StreamDiagnostics(_dir, 48000, "app"))
            d.Event("nota", "reinicio de «Mi Sala» en 10.0.0.5:8780 con chrome.exe " + new string('z', 400));
        var text = File.ReadAllText(Directory.GetFiles(_dir, "diag-*.jsonl").Single());
        Assert.DoesNotContain("Mi Sala", text);
        Assert.DoesNotContain("10.0.0.5", text);
        Assert.DoesNotContain("chrome", text);
        Assert.DoesNotContain(new string('z', 200), text);
    }

    [Fact]
    public void Two_sessions_in_the_same_millisecond_get_their_own_files()
    {
        for (int i = 0; i < 4; i++) { var d = new StreamDiagnostics(_dir, 48000, "app"); Assert.False(d.FileDisabled); d.Dispose(); }
        Assert.Equal(4, Directory.GetFiles(_dir, "diag-*.jsonl").Length);
    }

    [Fact]
    public void Non_finite_numbers_and_odd_locations_never_throw()
    {
        using var d = new StreamDiagnostics(_dir, 48000, "app");
        var s = d.Tick(double.NaN, double.PositiveInfinity, double.NegativeInfinity);
        Assert.False(s.Anomaly);
        Assert.Null(s.BufferEstS);
        for (int i = 0; i < 10; i++) d.Tick(double.NaN, double.NaN, double.NaN);   // forces a summary line too
        Assert.False(d.FileDisabled);
        foreach (var odd in new[] { "", "\0bad", "?:\\no\\such", new string('a', 400) })
            using (new StreamDiagnostics(odd, 48000, "app")) { } // whatever the location, construction must not throw
    }

    [Fact]
    public void The_suspicious_fill_is_reported_in_ms_of_the_real_format()
    {
        using var d = new StreamDiagnostics(null, 44100, "system");
        d.CaptureBlock(441, audible: true);
        d.PacerFill(44100 * 4 / 10);                       // 100 ms at 44.1 kHz stereo 16 bit
        var s = d.Tick(0, null, 0);
        Assert.Contains(s.Reasons, r => r == "relleno_sospechoso 100ms");
    }

    [Fact]
    public async Task Recording_from_many_threads_loses_nothing_and_is_consistent()
    {
        using var d = new StreamDiagnostics(null, 48000, "app");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 5000; i++) { d.CaptureBlock(10, i % 2 == 0); d.ServerWrite(Ms(1)); d.Processed(StreamDiagnostics.Timestamp()); }
        })));
        var s = d.Tick(0, null, 0);
        Assert.Equal(40000, s.CaptureBlocks);
        Assert.Equal(400000, s.CaptureFrames);
        Assert.Equal(40000, s.Writes);
        Assert.Equal(40000, s.ProcessCount);
    }
}
