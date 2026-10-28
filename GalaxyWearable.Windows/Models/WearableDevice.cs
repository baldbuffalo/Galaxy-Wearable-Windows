using Microsoft.UI.Xaml.Media.Imaging;

namespace GalaxyWearable.Windows.Models;

public sealed record WearableDevice(
    string Name,
    string Id,
    string Address,
    short? SignalStrength,
    bool IsConnectable,
    DateTimeOffset LastSeen,
    string? DeviceInformationId = null)
{
    public string SignalText => SignalStrength is short value ? $"{value} dBm" : "Unknown signal";
    public string ConnectableText => SignalStrength is null ? "Saved in Windows Bluetooth devices" : IsConnectable ? "Nearby via Bluetooth" : "Advertising only";

    // Use official Samsung product renders when the detected name identifies a known model.
    // Unknown models deliberately have no guessed model-specific image.
    public BitmapImage? ProductImage
    {
        get
        {
            var normalizedName = Name.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

            if (normalizedName.Contains("Fit3", StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains("SM-R390", StringComparison.OrdinalIgnoreCase))
            {
                return new BitmapImage(new Uri(
                    "https://images.samsung.com/is/image/samsung/p6pim/ae/sm-r390nzaamea/gallery/ae-galaxy-fit3-r390-sm-r390nzaamea-539783176?$1164_776_PNG$="));
            }

            return null;
        }
    }

    public string ProductImageDescription => Name;
}
