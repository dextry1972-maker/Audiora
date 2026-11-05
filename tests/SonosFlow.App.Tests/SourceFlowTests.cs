using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Tests;

/// <summary>The states of the guided flow ("first choose what to listen to, then stream"), without a window or a Sonos.</summary>
public sealed class SourceFlowTests
{
    [Fact]
    public void Connected_with_no_source_chosen_asks_for_a_source_whatever_the_remembered_mode()
    {
        Assert.Equal(SourceFlowStep.ChooseSource, SourceFlow.StepOf(sourceChosen: false, appMode: false, hasApp: false));
        Assert.Equal(SourceFlowStep.ChooseSource, SourceFlow.StepOf(sourceChosen: false, appMode: true, hasApp: true));
    }

    [Fact]
    public void The_whole_PC_is_ready_as_soon_as_it_is_chosen()
    {
        Assert.Equal(SourceFlowStep.ReadyToStream, SourceFlow.StepOf(true, appMode: false, hasApp: false));
        Assert.Equal(SourceFlowStep.ReadyToStream, SourceFlow.StepOf(true, appMode: false, hasApp: true));
    }

    [Fact]
    public void One_application_needs_a_valid_application_before_streaming()
    {
        Assert.Equal(SourceFlowStep.ChooseApp, SourceFlow.StepOf(true, appMode: true, hasApp: false));
        Assert.Equal(SourceFlowStep.ReadyToStream, SourceFlow.StepOf(true, appMode: true, hasApp: true));
    }

    [Theory]
    [InlineData(null, false, false)]   // fresh install: nothing chosen yet
    [InlineData(null, true, true)]     // settings written before the flow existed: the user already uses the app, nothing changes
    [InlineData(false, true, false)]   // an explicit answer always wins
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void A_source_counts_as_chosen_only_for_people_who_already_chose_or_used_the_app(bool? saved, bool fileFound, bool expected) =>
        Assert.Equal(expected, SourceFlow.InitialChoice(saved, fileFound));

    [Fact]
    public void Every_step_is_reachable_and_only_one_allows_streaming()
    {
        var steps = new[] { false, true }
            .SelectMany(chosen => new[] { false, true }.SelectMany(app => new[] { false, true }.Select(has => SourceFlow.StepOf(chosen, app, has))))
            .ToList();
        Assert.Contains(SourceFlowStep.ChooseSource, steps);
        Assert.Contains(SourceFlowStep.ChooseApp, steps);
        Assert.Contains(SourceFlowStep.ReadyToStream, steps);
    }
}
