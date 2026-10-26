using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Sonos;

namespace SonosFlow.App.ViewModels;

public sealed class SpeakerItem(SonosDevice device) : ObservableObject
{
    private bool _isOnline = true;
    private bool _isSelected;
    private bool _isStreaming;

    public SonosDevice Device { get; } = device;
    public string Name => string.IsNullOrWhiteSpace(Device.RoomName) ? Device.ModelName : Device.RoomName;
    public string Model => Device.ModelName;
    public string Address => Device.Address.ToString();

    public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsStreaming { get => _isStreaming; set => Set(ref _isStreaming, value); }
}
