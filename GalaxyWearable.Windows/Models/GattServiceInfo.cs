namespace GalaxyWearable.Windows.Models;

public sealed record GattServiceInfo(string Uuid, string Name, string AccessStatus)
{
    public override string ToString() => $"{Name}  ·  {Uuid}  ·  {AccessStatus}";
}
