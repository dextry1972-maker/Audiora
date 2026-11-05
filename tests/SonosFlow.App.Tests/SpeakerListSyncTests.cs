using System.Collections.ObjectModel;
using System.Net;
using SonosFlow.App.ViewModels;
using SonosFlow.Core.Sonos;

namespace SonosFlow.App.Tests;

/// <summary>The speaker list while a search runs: updated in place, no duplicates, selection kept, nothing cleared.</summary>
public sealed class SpeakerListSyncTests
{
    private static SonosDevice Device(string udn, string room, string ip = "10.0.0.5") => new(IPAddress.Parse(ip), room, "Sonos Five", "S23", udn);

    private static string[] Names(ObservableCollection<SpeakerItem> list) => list.Select(s => s.Name).ToArray();

    [Fact]
    public void New_speakers_are_added_in_name_order()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("B", "Salón")]);
        SpeakerListSync.Merge(list, [Device("A", "Cocina"), Device("C", "Terraza")]);

        Assert.Equal(["Cocina", "Salón", "Terraza"], Names(list));
    }

    [Fact]
    public void Reporting_the_same_speaker_again_keeps_its_row_and_its_selection()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("A", "Salón")]);
        var row = list[0];
        row.IsSelected = true;
        int changes = 0;
        list.CollectionChanged += (_, _) => changes++;

        var again = SpeakerListSync.Merge(list, [Device("A", "Salón"), Device("A", "Salón")]);

        Assert.Same(row, list[0]);
        Assert.True(row.IsSelected);
        Assert.Equal(0, changes);                    // not even a replace: nothing blinks
        Assert.Empty(again.Added);
        Assert.Empty(again.Replaced);
    }

    [Fact]
    public void A_speaker_that_changed_address_is_replaced_in_place_and_keeps_its_selected_flag()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("A", "Cocina"), Device("B", "Salón", "10.0.0.5")]);
        list[1].IsSelected = true;
        var old = list[1];

        var result = SpeakerListSync.Merge(list, [Device("B", "Salón", "10.0.0.77")]);

        Assert.Equal(2, list.Count);
        Assert.Equal("10.0.0.77", list[1].Address);
        Assert.True(list[1].IsSelected);
        Assert.NotSame(old, list[1]);
        var (was, now) = Assert.Single(result.Replaced);
        Assert.Same(old, was);
        Assert.Same(list[1], now);
    }

    [Fact]
    public void A_report_that_lacks_known_speakers_does_not_remove_them()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("A", "Cocina"), Device("B", "Salón")]);

        SpeakerListSync.Merge(list, [Device("A", "Cocina")]);

        Assert.Equal(["Cocina", "Salón"], Names(list));
    }

    [Fact]
    public void Pruning_removes_only_what_the_finished_search_did_not_see()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("A", "Cocina"), Device("B", "Salón"), Device("C", "Terraza")]);
        var seen = SpeakerListSync.Merge(new ObservableCollection<SpeakerItem>(), [Device("A", "Cocina"), Device("C", "Terraza")]).Seen;

        var gone = SpeakerListSync.Prune(list, seen);

        Assert.Equal(["Cocina", "Terraza"], Names(list));
        Assert.Equal("Salón", Assert.Single(gone).Name);
    }

    [Fact]
    public void Pruning_never_removes_a_speaker_the_caller_wants_to_keep()
    {
        var list = new ObservableCollection<SpeakerItem>();
        SpeakerListSync.Merge(list, [Device("A", "Cocina"), Device("B", "Salón")]);
        list[1].IsStreaming = true;                                    // e.g. the connected one

        SpeakerListSync.Prune(list, new HashSet<string>(), keep: s => s.IsStreaming);

        Assert.Equal("Salón", Assert.Single(list).Name);
    }

    [Fact]
    public void A_speaker_without_a_Udn_is_told_apart_by_its_address()
    {
        var list = new ObservableCollection<SpeakerItem>();

        SpeakerListSync.Merge(list, [Device("", "Uno", "10.0.0.1"), Device("", "Dos", "10.0.0.2")]);
        SpeakerListSync.Merge(list, [Device("", "Uno", "10.0.0.1")]);

        Assert.Equal(2, list.Count);
    }
}
