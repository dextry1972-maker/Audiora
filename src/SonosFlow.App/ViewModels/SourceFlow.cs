namespace SonosFlow.App.ViewModels;

/// <summary>Where the user is in "first choose what to listen to, then stream".</summary>
public enum SourceFlowStep
{
    /// <summary>Connected, but neither "Todo el PC" nor "Una aplicación" has been chosen yet.</summary>
    ChooseSource,
    /// <summary>"Una aplicación" is chosen but no valid application is selected yet.</summary>
    ChooseApp,
    /// <summary>A valid source is chosen: streaming can start.</summary>
    ReadyToStream,
}

/// <summary>The rules of the guided flow. Pure, so the states the screen shows can be tested without a window.</summary>
public static class SourceFlow
{
    public static SourceFlowStep StepOf(bool sourceChosen, bool appMode, bool hasApp) =>
        !sourceChosen ? SourceFlowStep.ChooseSource
        : appMode && !hasApp ? SourceFlowStep.ChooseApp
        : SourceFlowStep.ReadyToStream;

    /// <summary>
    /// Whether a source counts as already chosen when the app starts. An explicit saved answer wins. Settings written before this
    /// existed (the file is there, the answer is not) belong to someone who has been using the app: their source stays chosen and
    /// nothing changes for them. Only a genuinely fresh install starts with no source chosen.
    /// </summary>
    public static bool InitialChoice(bool? saved, bool settingsFileFound) => saved ?? settingsFileFound;
}
