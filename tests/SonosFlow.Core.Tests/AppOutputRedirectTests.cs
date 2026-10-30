using SonosFlow.Core.Audio;

namespace SonosFlow.Core.Tests;

/// <summary>The parts of the per-app output redirect that don't call Windows (those are covered by CaptureCheck route*).</summary>
public sealed class AppOutputRedirectTests
{
    private const string EndpointId = "{0.0.0.00000000}.{3d1f1c4e-7a2b-4a33-9b8f-1d2c3b4a5e6f}";

    [Fact]
    public void Policy_ids_are_unwrapped_to_the_endpoint_id()
    {
        Assert.Equal(EndpointId, AppOutputRouting.FromPolicyId($@"\\?\SWD#MMDEVAPI#{EndpointId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}"));
        Assert.Equal(EndpointId, AppOutputRouting.FromPolicyId(EndpointId));
        Assert.Null(AppOutputRouting.FromPolicyId(""));
        Assert.Null(AppOutputRouting.FromPolicyId(null));
    }

    [Fact]
    public void Nothing_pending_when_there_is_no_state_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        Assert.True(AppOutputRedirect.RecoverPending(file));
    }

    [Fact]
    public void An_unreadable_state_file_never_throws()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        try { Assert.False(AppOutputRedirect.RecoverPending(file)); Assert.Equal("{ not json", File.ReadAllText(file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_redirect_for_an_app_that_is_not_running_stays_pending()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, Record("C:\\nowhere\\sf-no-such-app.exe", ownerPid: 0));
        try
        {
            Assert.False(AppOutputRedirect.RecoverPending(file));
            Assert.True(File.Exists(file)); // kept until Windows accepts the restore
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_live_redirect_of_this_process_is_left_alone()
    {
        // Owned by this process but not one of its live redirects (e.g. a restore Windows refused): it is retried.
        // Owned by another running Audiora: skipped. Here the owner is this test process, which isn't "SonosFlow".
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, Record("C:\\nowhere\\sf-no-such-app.exe", Environment.ProcessId));
        try { Assert.False(AppOutputRedirect.RecoverPending(file)); }
        finally { File.Delete(file); }
    }


    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{ not json")]
    public void Invalid_state_blocks_a_new_redirect_and_is_preserved(string json)
    {
        var file = NewFile();
        File.WriteAllText(file, json);
        var routing = new FakeRouting();
        try
        {
            Assert.False(AppOutputRedirect.RecoverPending(file, routing));
            Assert.Throws<IOException>(() => new AppOutputRedirect(1, "test.exe", EndpointId, file, routing));
            Assert.Equal(json, File.ReadAllText(file));
            Assert.Empty(routing.Writes);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_second_live_redirect_cannot_replace_the_first_and_recovery_leaves_it_alone()
    {
        var file = NewFile();
        var routing = new FakeRouting { Current = "original" };
        try
        {
            using var first = new AppOutputRedirect(1, "test.exe", EndpointId, file, routing);
            var state = File.ReadAllText(file);
            Assert.True(AppOutputRedirect.RecoverPending(file, routing));
            Assert.Equal(EndpointId, routing.Current);
            Assert.Throws<InvalidOperationException>(() => new AppOutputRedirect(1, "test.exe", "another", file, routing));
            Assert.Equal(state, File.ReadAllText(file));
            first.Dispose();
            Assert.True(first.Restored);
            Assert.Equal("original", routing.Current);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_user_output_change_is_preserved_when_stopping()
    {
        var file = NewFile();
        var routing = new FakeRouting { Current = "original" };
        try
        {
            using var redirect = new AppOutputRedirect(1, "test.exe", EndpointId, file, routing);
            routing.Current = "user-choice";
            redirect.Dispose();
            Assert.Equal("user-choice", routing.Current);
            Assert.Single(routing.Writes);
            Assert.True(redirect.Restored);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Failed_restore_stays_pending_and_a_later_recovery_retries_it()
    {
        var file = NewFile();
        var routing = new FakeRouting { Current = "original" };
        try
        {
            using var redirect = new AppOutputRedirect(1, "test.exe", EndpointId, file, routing);
            routing.FailWrite = true;
            redirect.Dispose();
            Assert.False(redirect.Restored);
            Assert.True(File.Exists(file));
            routing.FailWrite = false;
            Assert.True(AppOutputRedirect.RecoverPending(file, routing));
            Assert.Equal("original", routing.Current);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_pending_record_does_not_overwrite_a_user_choice_before_a_new_redirect()
    {
        var file = NewFile();
        File.WriteAllText(file, Record("test.exe", 0));
        var routing = new FakeRouting { Current = "user-choice" };
        try
        {
            using var redirect = new AppOutputRedirect(1, "test.exe", EndpointId, file, routing);
            redirect.Dispose();
            Assert.Equal("user-choice", routing.Current);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Lock_timeout_never_reads_or_modifies_the_pending_record()
    {
        var file = NewFile();
        var original = Record("test.exe", 0);
        File.WriteAllText(file, original);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Local\SonosFlow-AppOutputGuard");
            mutex.WaitOne();
            held.Set();
            try { release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            var routing = new FakeRouting();
            Assert.False(AppOutputRedirect.RecoverPending(file, routing));
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Empty(routing.Writes);
            Assert.Equal(0, routing.Reads);
        }
        finally { release.Set(); owner.Join(TimeSpan.FromSeconds(5)); File.Delete(file); }
    }

    private static string NewFile() => Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");

    private sealed class FakeRouting : AppOutputRedirect.IRouting
    {
        public string? Current { get; set; }
        public bool FailWrite { get; set; }
        public int Reads { get; private set; }
        public List<string?> Writes { get; } = [];
        public string? Get(int pid) { Reads++; return Current; }
        public void Set(int pid, string? deviceId)
        {
            if (FailWrite) throw new IOException("Simulated output device failure");
            Current = deviceId;
            Writes.Add(deviceId);
        }
        public IReadOnlyList<int> ProcessesOf(string exePath) => [1];
    }

    private static string Record(string exe, int ownerPid) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new { ExePath = exe, PreviousDeviceId = (string?)null, TargetDeviceId = EndpointId, OwnerPid = ownerPid } });
}
