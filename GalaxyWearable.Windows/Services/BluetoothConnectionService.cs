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
        lines.Add("This is a BLE/GATT connection, not proof of Samsung companion pairing.");
        lines.Add("Samsung-specific authenticated services are listed only when Windows allows discovery.");

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
        return "Subscribed to standard heart-rate notifications (2A37), if the watch is currently publishing measurements.";
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
            _heartRateCharacteristic = null;
        }

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
