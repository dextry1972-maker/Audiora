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
        try { Assert.True(AppOutputRedirect.RecoverPending(file)); }
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

    private static string Record(string exe, int ownerPid) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new { ExePath = exe, PreviousDeviceId = (string?)null, TargetDeviceId = EndpointId, OwnerPid = ownerPid } });
}
