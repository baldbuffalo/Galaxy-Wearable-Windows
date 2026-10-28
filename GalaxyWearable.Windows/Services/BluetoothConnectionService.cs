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
    private GattCharacteristic? _heartRateCharacteristic;
    private bool _heartRateSubscribed;

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public event EventHandler<string>? HeartRateChanged;

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
                if (characteristic.Uuid == GattCharacteristicUuids.HeartRateMeasurement)
                    _heartRateCharacteristic = characteristic;
            }
        }

        lines.Add("");
        lines.Add("Fit3 protocol assessment:");
        var serviceIds = _services.Select(service => service.Uuid).ToHashSet();
        if (serviceIds.Contains(GattServiceUuids.HeartRate))
        {
            var notificationStatus = await StartHeartRateNotificationsAsync();
            lines.Add($"• Standard, unbonded Heart Rate Profile: {notificationStatus}");
        }
        else
        {
            lines.Add("• Standard Heart Rate Profile (180D) was not exposed in this session.");
        }

        if (serviceIds.Any(uuid => uuid == Guid.Parse("00001a1a-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung 1A1A service is visible; its documented streaming characteristic may require an existing Bluetooth bond.");
        if (serviceIds.Any(uuid => uuid == Guid.Parse("0000fd69-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung FD69 service is visible; no proprietary writes are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("eedd5e73-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 crypto-handshake service family is visible. Its full handshake is not implemented; no guessed keys or payloads are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("1b7e8251-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 accessory-channel service family is visible; protected operations remain unimplemented.");

        lines.Add("• This connection does not impersonate Samsung's phone app or alter the watch's existing phone pairing.");
        lines.Add("• Protected Samsung channels require protocol/key evidence; UUID discovery alone is not authentication.");

        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string> StartHeartRateNotificationsAsync()
    {
        if (_heartRateCharacteristic is null)
            return "No standard Heart Rate Measurement characteristic (2A37) was found.";

        _heartRateCharacteristic.ValueChanged -= OnHeartRateValueChanged;
        var status = await _heartRateCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

        if (status != GattCommunicationStatus.Success)
            return $"Could not subscribe to heart-rate notifications: {status}.";

        _heartRateCharacteristic.ValueChanged += OnHeartRateValueChanged;
        _heartRateSubscribed = true;
        return "subscribed to standard heart-rate notifications (2A37); live samples depend on the watch currently publishing measurements.";
    }

    private void OnHeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        if (reader.UnconsumedBufferLength < 2)
            return;

        var flags = reader.ReadByte();
        var is16Bit = (flags & 0x01) != 0;
        var bpm = is16Bit && reader.UnconsumedBufferLength >= 2
            ? reader.ReadUInt16()
            : reader.ReadByte();

        HeartRateChanged?.Invoke(this, $"Heart rate: {bpm} BPM");
    }

    public async Task DisconnectAsync()
    {
        if (_heartRateCharacteristic is not null)
        {
            _heartRateCharacteristic.ValueChanged -= OnHeartRateValueChanged;
            if (_heartRateSubscribed)
            {
                try
                {
                    await _heartRateCharacteristic
                        .WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch
                {
                    // Disconnect cleanup should continue if the peripheral has gone away.
                }
            }
            _heartRateSubscribed = false;
            _heartRateCharacteristic = null;
        }

        foreach (var service in _services)
            service.Dispose();
        _services.Clear();

        _device?.Dispose();
        _device = null;
        _heartRateSubscribed = false;
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
