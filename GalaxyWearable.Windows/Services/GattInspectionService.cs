using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.GenericAttributeProfile;
using GalaxyWearable.Windows.Models;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Opens a user-selected BLE device and lists its GATT services.
/// It does not perform Samsung companion pairing or write proprietary commands.
/// </summary>
public sealed class GattInspectionService
{
    public async Task<(BluetoothLEDevice? Device, IReadOnlyList<GattServiceInfo> Services, string Message)> ConnectAndInspectAsync(
        string bluetoothAddress,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(bluetoothAddress, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var address))
            return (null, Array.Empty<GattServiceInfo>(), "The Bluetooth address is invalid.");

        cancellationToken.ThrowIfCancellationRequested();
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
            return (null, Array.Empty<GattServiceInfo>(),
                "Windows could not open this device. It may be out of range or unavailable.");

        try
        {
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            var discoveredServices = result.Services;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (result.Status != GattCommunicationStatus.Success)
                {
                    var status = result.ProtocolError is byte error ? $"Protocol error 0x{error:X2}" : result.Status.ToString();
                    device.Dispose();
                    return (null, Array.Empty<GattServiceInfo>(), $"GATT service discovery failed: {status}");
                }

                var services = discoveredServices.Select(service =>
                    new GattServiceInfo(
                        service.Uuid.ToString(),
                        GetKnownName(service.Uuid),
                        service.DeviceAccessInformation.CurrentStatus.ToString()))
                    .OrderBy(service => service.Name)
                    .ToArray();

                return (device, services, $"Connected to {device.Name}. Found {services.Length} GATT service(s).");
            }
            finally
            {
                // GATT service objects are disposable even when cancellation or
                // a failed discovery result prevents normal inspection.
                foreach (var service in discoveredServices)
                    service.Dispose();
            }
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static string GetKnownName(Guid uuid) => uuid.ToString("D").ToLowerInvariant() switch
    {
        "00001800-0000-1000-8000-00805f9b34fb" => "Generic Access",
        "00001801-0000-1000-8000-00805f9b34fb" => "Generic Attribute",
        "0000180a-0000-1000-8000-00805f9b34fb" => "Device Information",
        "0000180f-0000-1000-8000-00805f9b34fb" => "Battery Service",
        _ => "Vendor or custom service"
    };
}
