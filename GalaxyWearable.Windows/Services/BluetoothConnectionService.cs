using System.Globalization;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.GenericAttributeProfile;
using global::Windows.Storage.Streams;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Opens a BLE GATT connection and inspects services without pretending that
/// a GATT connection completes Samsung's proprietary companion pairing flow.
/// </summary>
public sealed class BluetoothConnectionService : IDisposable
{
    private BluetoothLEDevice? _device;
    private readonly List<GattDeviceService> _services = new();
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public async Task<string> ConnectAndInspectAsync(string bluetoothAddress)
    {
        if (!ulong.TryParse(bluetoothAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException("The selected device has an invalid Bluetooth address.", nameof(bluetoothAddress));

        await DisconnectAsync();

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device is null)
            throw new InvalidOperationException("Windows could not open this Bluetooth LE device. Make sure it is nearby and advertising.");

        var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Connected to the BLE device, but service discovery returned: {result.Status}.");

        _services.AddRange(result.Services);

        var lines = new List<string>
        {
            $"Device: {_device.Name}",
            $"BLE connection: {_device.ConnectionStatus}",
            $"GATT services found: {_services.Count}",
            "",
            "Services and characteristics:"
        };

        foreach (var service in _services)
        {
            lines.Add($"• Service {service.Uuid}");
            var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                lines.Add($"  Could not enumerate characteristics: {characteristicsResult.Status}");
                continue;
            }

            foreach (var characteristic in characteristicsResult.Characteristics)
            {
                lines.Add($"  • {characteristic.Uuid} [{characteristic.CharacteristicProperties}]");
            }
        }

        lines.Add("");
        lines.Add("Fit3 protocol assessment:");
        var serviceIds = _services.Select(service => service.Uuid).ToHashSet();
        if (serviceIds.Any(uuid => uuid == Guid.Parse("00001a1a-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung 1A1A service is visible; its documented streaming characteristic may require an existing Bluetooth bond.");
        if (serviceIds.Any(uuid => uuid == Guid.Parse("0000fd69-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung FD69 service is visible; no proprietary writes are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("eedd5e73-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 crypto-handshake service family is visible. Its full handshake is not implemented; no guessed keys or payloads are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("1b7e8251-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 accessory-channel service family is visible; protected operations remain unimplemented.");

        var identity = await ReadStandardDeviceIdentityAsync();
        if (!string.IsNullOrWhiteSpace(identity))
            lines.Add($"• Standard device information: {identity}");

        lines.Add("• This connection does not impersonate Samsung's phone app or alter the watch's existing phone pairing.");
        lines.Add("• Protected Samsung channels require protocol/key evidence; UUID discovery alone is not authentication.");

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string?> ReadStandardDeviceIdentityAsync()
    {
        var service = _services.FirstOrDefault(item =>
            item.Uuid == Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb"));
        if (service is null)
            return null;

        var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            return $"service present; characteristic discovery returned {result.Status}";

        var fields = new List<string>();
        foreach (var characteristic in result.Characteristics)
        {
            var label = characteristic.Uuid == Guid.Parse("00002a29-0000-1000-8000-00805f9b34fb") ? "manufacturer"
                : characteristic.Uuid == Guid.Parse("00002a24-0000-1000-8000-00805f9b34fb") ? "model"
                : characteristic.Uuid == Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb") ? "firmware"
                : characteristic.Uuid == Guid.Parse("00002a28-0000-1000-8000-00805f9b34fb") ? "software"
                : null;
            if (label is null || (characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0)
                continue;

            try
            {
                var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success || read.Value is null)
                    continue;
                using var reader = DataReader.FromBuffer(read.Value);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                var value = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(value))
                    fields.Add($"{label} {value}");
            }
            catch
            {
                // Some standard identity fields may be unavailable without affecting BLE connection.
            }
        }

        return fields.Count == 0 ? "service present, no readable identity fields" : string.Join(", ", fields);
    }

    public async Task DisconnectAsync()
    {
        foreach (var service in _services)
            service.Dispose();
        _services.Clear();

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        _services.Clear();
        _device?.Dispose();
        _device = null;
    }
}
