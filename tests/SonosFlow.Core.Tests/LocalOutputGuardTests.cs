using System.Runtime.InteropServices;
using System.Text.Json;
using SonosFlow.Core.Audio;

namespace SonosFlow.Core.Tests;

public sealed class LocalOutputGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sf-local-{Guid.NewGuid():N}");
    private string StateFile => Path.Combine(_directory, "guard.json");

    private sealed class Volume : LocalOutputGuard.IVolume
    {
        private bool _mute;
        public bool FailRestore;
        public bool Mute
        {
            get => _mute;
            set { if (!value && FailRestore) throw new COMException("disconnected"); _mute = value; }
        }
        public float Level { get; set; } = 0.35f;
    }

    [Fact]
    public void Failed_restore_retains_record_and_dispose_can_retry()
    {
        var volume = new Volume();
        var guard = new LocalOutputGuard("fake", volume, true, StateFile);
        volume.FailRestore = true;
        Assert.Throws<COMException>(guard.Dispose);
        Assert.True(File.Exists(StateFile));
        volume.FailRestore = false;
        guard.Dispose();
        guard.Dispose();
        Assert.False(volume.Mute);
        Assert.Equal(0.35f, volume.Level);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void Unavailable_device_keeps_legacy_record_until_recovery_succeeds()
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(new { DeviceId = "fake", WasMuted = false, PreviousVolume = 0.35f, VolumeChanged = true });
        File.WriteAllText(StateFile, json);
        Assert.False(LocalOutputGuard.RecoverPending(StateFile, _ => throw new COMException("missing")));
        Assert.Equal(json, File.ReadAllText(StateFile));
        var volume = new Volume { Mute = true, Level = 1f };
        Assert.True(LocalOutputGuard.RecoverPending(StateFile, _ => volume));
        Assert.False(volume.Mute);
        Assert.Equal(0.35f, volume.Level);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void Corrupt_record_is_preserved_and_blocks_a_new_guard()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StateFile, "{ broken");
        Assert.False(LocalOutputGuard.RecoverPending(StateFile, _ => new Volume()));
        var volume = new Volume();
        Assert.Throws<IOException>(() => new LocalOutputGuard("fake", volume, false, StateFile));
        Assert.False(volume.Mute);
        Assert.Equal("{ broken", File.ReadAllText(StateFile));
    }

    [Fact]
    public void Recovery_and_a_second_guard_leave_a_live_owner_untouched()
    {
        var volume = new Volume();
        using var guard = new LocalOutputGuard("fake", volume, true, StateFile);
        Assert.False(LocalOutputGuard.RecoverPending(StateFile, _ => throw new Exception("must not access")));
        Assert.Throws<IOException>(() => new LocalOutputGuard("fake", volume, true, StateFile));
        Assert.True(volume.Mute);
        Assert.Equal(1f, volume.Level);
    }

    [Fact]
    public void User_volume_and_unmute_are_preserved()
    {
        var volume = new Volume();
        using (var guard = new LocalOutputGuard("fake", volume, true, StateFile))
        {
            volume.Level = 0.62f;
            volume.Mute = false;
        }
        Assert.Equal(0.62f, volume.Level);
        Assert.False(volume.Mute);
    }

    [Fact]
    public void Dispose_lock_timeout_keeps_the_record_recoverable()
    {
        var volume = new Volume();
        var guard = new LocalOutputGuard("fake", volume, false, StateFile);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Local\SonosFlow-LocalOutputGuard");
            mutex.WaitOne();
            held.Set();
            try { release.Wait(); } finally { mutex.ReleaseMutex(); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<IOException>(guard.Dispose);
            Assert.True(volume.Mute);
            Assert.True(File.Exists(StateFile));
        }
        finally { release.Set(); thread.Join(TimeSpan.FromSeconds(5)); }
        Assert.True(LocalOutputGuard.RecoverPending(StateFile, _ => volume));
        Assert.False(volume.Mute);
        guard.Dispose();
    }

    [Fact]
    public void Late_dispose_cannot_restore_or_delete_a_new_owners_record()
    {
        var volume = new Volume();
        var old = new LocalOutputGuard("fake", volume, true, StateFile);
        volume.FailRestore = true;
        Assert.Throws<COMException>(old.Dispose);
        volume.FailRestore = false;
        Assert.True(LocalOutputGuard.RecoverPending(StateFile, _ => volume));
        using var next = new LocalOutputGuard("fake", volume, true, StateFile);
        string record = File.ReadAllText(StateFile);
        old.Dispose();
        Assert.Equal(record, File.ReadAllText(StateFile));
        Assert.True(volume.Mute);
        Assert.Equal(1f, volume.Level);
    }

    [Fact]
    public void Repeated_start_stop_restores_original_state()
    {
        var volume = new Volume { Mute = true };
        for (int i = 0; i < 50; i++)
        {
            using (var guard = new LocalOutputGuard("fake", volume, true, StateFile))
                Assert.True(volume.Mute);
            Assert.True(volume.Mute);
            Assert.Equal(0.35f, volume.Level);
            Assert.False(File.Exists(StateFile));
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
