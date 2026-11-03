using System.Text.Json;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Tests;

/// <summary>Aurora 2.0 personalisation: the name for the welcome. Presentation only; stored with the other preferences.</summary>
public sealed class ProfileTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  Elian  ", "Elian")]
    [InlineData("El\u0000ian\r\n", "Elian")]
    [InlineData("José María", "José María")]
    [InlineData("اسم", "اسم")]
    [InlineData("名前", "名前")]
    public void The_name_is_cleaned_of_control_characters_and_padding(string? typed, string expected) =>
        Assert.Equal(expected, MainViewModel.CleanUserName(typed));

    [Fact]
    public void A_very_long_name_is_cut_without_trailing_spaces()
    {
        var typed = new string('a', 30) + " " + new string('b', 30);
        var clean = MainViewModel.CleanUserName(typed);
        Assert.True(clean.Length <= MainViewModel.MaxUserNameLength);
        Assert.False(clean.EndsWith(' '));
        Assert.StartsWith(new string('a', 30), clean);
    }

    [Fact]
    public void Settings_written_before_the_name_existed_keep_working_and_get_sensible_defaults()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"MuteLocal\":false,\"Port\":8790,\"Language\":\"es\",\"LanguageOnboardingDone\":true}")!;
        Assert.Null(old.UserName);
        Assert.True(old.PersonalizedWelcome);      // greeting is on by default, but with no name there is nothing to say
        Assert.False(old.NameOnboardingDone);
        Assert.False(old.MuteLocal);
        Assert.Equal(8790, old.Port);
    }

    [Fact]
    public void The_name_round_trips_through_the_settings_file_format()
    {
        var written = JsonSerializer.Serialize(new AppSettings { UserName = "Elian", PersonalizedWelcome = false, NameOnboardingDone = true });
        var back = JsonSerializer.Deserialize<AppSettings>(written)!;
        Assert.Equal("Elian", back.UserName);
        Assert.False(back.PersonalizedWelcome);
        Assert.True(back.NameOnboardingDone);
    }
}
