namespace GalaxyWearable.Windows.Models;

public sealed record WearableDevice(
    string Name,
    string Id,
    string Address,
    short? SignalStrength,
    bool IsConnectable,
    DateTimeOffset LastSeen)
{
    public string SignalText => SignalStrength is short value ? $"{value} dBm" : "Unknown signal";
    public string ConnectableText => IsConnectable ? "Connectable advertisement" : "Advertisement only";
}
