using System.Collections.ObjectModel;
using SonosFlow.Core.Sonos;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// Keeps the list of speakers in step with what a running search reports, without ever clearing it: a speaker already in the list keeps its
/// item (so the selection and the on-screen row do not blink), new ones are inserted in name order, and a speaker whose address changed
/// is replaced in place. Pure list logic: no I/O, no UI thread requirements beyond the collection's own.
/// </summary>
public static class SpeakerListSync
{
    /// <summary>Identity of a speaker: its Udn (the address when a player did not report one).</summary>
    public static string KeyOf(SonosDevice device) => string.IsNullOrWhiteSpace(device.Udn) ? device.Address.ToString() : device.Udn;

    /// <summary>What <see cref="Merge"/> did, so the caller can fix the selection.</summary>
    public sealed record MergeResult(IReadOnlyList<SpeakerItem> Added, IReadOnlyList<(SpeakerItem Old, SpeakerItem New)> Replaced, ISet<string> Seen);

    public static MergeResult Merge(ObservableCollection<SpeakerItem> list, IEnumerable<SonosDevice> devices)
    {
        var added = new List<SpeakerItem>();
        var replaced = new List<(SpeakerItem, SpeakerItem)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var key = KeyOf(device);
            if (!seen.Add(key)) continue; // the same player twice in one report
            int index = IndexOf(list, key);
            if (index < 0)
            {
                var item = new SpeakerItem(device);
                list.Insert(InsertionPoint(list, item), item);
                added.Add(item);
            }
            else if (list[index].Device != device)
            {
                var old = list[index];
                var fresh = new SpeakerItem(device) { IsSelected = old.IsSelected, IsOnline = old.IsOnline, IsStreaming = old.IsStreaming };
                list[index] = fresh;
                replaced.Add((old, fresh));
            }
        }
        return new MergeResult(added, replaced, seen);
    }

    /// <summary>
    /// Drops the speakers a finished search did not see (they are gone), except those <paramref name="keep"/> says must stay
    /// (the one that is connected). Returns the items removed.
    /// </summary>
    public static IReadOnlyList<SpeakerItem> Prune(ObservableCollection<SpeakerItem> list, ISet<string> seen, Func<SpeakerItem, bool>? keep = null)
    {
        var gone = list.Where(s => !seen.Contains(KeyOf(s.Device)) && keep?.Invoke(s) != true).ToList();
        foreach (var s in gone) list.Remove(s);
        return gone;
    }

    private static int IndexOf(ObservableCollection<SpeakerItem> list, string key)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(KeyOf(list[i].Device), key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static int InsertionPoint(ObservableCollection<SpeakerItem> list, SpeakerItem item)
    {
        int i = 0;
        while (i < list.Count && string.Compare(list[i].Name, item.Name, StringComparison.CurrentCultureIgnoreCase) <= 0) i++;
        return i;
    }
}
