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
    public void An_unreadable_state_file_is_dropped_never_retried_forever()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        Assert.True(AppOutputRedirect.RecoverPending(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_redirect_for_an_app_that_is_not_running_stays_pending()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sf-redirect-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, $$"""{"ExePath":"C:\\nowhere\\sf-no-such-app.exe","PreviousDeviceId":null,"TargetDeviceId":"{{EndpointId.Replace("\\", "\\\\")}}"}""");
        try
        {
            Assert.False(AppOutputRedirect.RecoverPending(file));
            Assert.True(File.Exists(file)); // kept until the app runs again
        }
        finally { File.Delete(file); }
    }
}
