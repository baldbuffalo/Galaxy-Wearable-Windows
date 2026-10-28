using System.Collections.Concurrent;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.Advertisement;
using global::Windows.Devices.Enumeration;
using GalaxyWearable.Windows.Models;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Finds likely Samsung watches using generic Bluetooth advertisement metadata,
/// without a list of individual watch models. Discovery alone does not pair with
/// a device or implement Samsung's companion protocol.
/// </summary>
public sealed class BluetoothDiscoveryService : IDisposable
{
    private const ushort SamsungCompanyId = 0x0075;
    private const byte AppearanceDataType = 0x19;

    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active
    };
    private readonly ConcurrentDictionary<ulong, WearableDevice> _devices = new();

    public event EventHandler<IReadOnlyList<WearableDevice>>? DevicesChanged;
    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public string? LastStopError { get; private set; }

    public BluetoothDiscoveryService()
    {
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    public async Task StartAsync()
    {
        LastStopError = null;

        // A quick stop/start can leave the watcher in Stopping briefly. Wait
        // for that transition instead of silently skipping Start().
        for (var attempt = 0; attempt < 20
             && _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Stopping; attempt++)
            await Task.Delay(50);

        if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created
            or BluetoothLEAdvertisementWatcherStatus.Stopped)
        {
            _watcher.Start();
        }
        else if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
        {
            throw new InvalidOperationException($"Bluetooth LE watcher cannot start from status {_watcher.Status}.");
        }

        // Let active BLE discovery populate Windows' unpaired-device enumeration,
        // then include matching devices even if their advertisements omit watch metadata.
        // This is specifically for pairing new watches, not just listing paired devices.
        await Task.Delay(1200);
        await AddUnpairedSamsungWearablesAsync();

        // The watcher status can update asynchronously after Start() returns.
        // Wait briefly before the UI decides that scanning failed.
        for (var attempt = 0; attempt < 10 && !IsScanning; attempt++)
            await Task.Delay(100);

        if (!IsScanning)
        {
            var detail = LastStopError is null ? string.Empty : $" Windows reported: {LastStopError}.";
            throw new InvalidOperationException(
                $"Bluetooth LE watcher did not enter the Started state (status: {_watcher.Status}).{detail}");
        }
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
    }

    private async Task AddUnpairedSamsungWearablesAsync()
    {
        try
        {
            // Enumerate both paired and unpaired BLE devices. Filtering only for
            // PairingState(false) silently hid watches that were already paired in
            // Windows, including devices that were not currently advertising.
            foreach (var isPaired in new[] { true, false })
            {
                var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(isPaired);
                var knownDevices = await DeviceInformation.FindAllAsync(selector);

                foreach (var info in knownDevices)
                {
                    var name = info.Name?.Trim() ?? string.Empty;
                    if (!NameSuggestsSamsungWearable(name))
                        continue;

                    try
                    {
                        using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                        if (device is null || device.BluetoothAddress == 0)
                            continue;

                        var displayName = string.IsNullOrWhiteSpace(device.Name) ? name : device.Name.Trim();
                        if (_devices.TryGetValue(device.BluetoothAddress, out var observed)
                            && observed.SignalStrength is not null
                            && DateTimeOffset.Now - observed.LastSeen < TimeSpan.FromSeconds(30))
                        {
                            // Keep live advertisement data (RSSI/connectability) instead
                            // of replacing it with weaker paired-device enumeration data.
                            _devices[device.BluetoothAddress] = observed with
                            {
                                Name = displayName,
                                DeviceInformationId = info.Id
                            };
                        }
                        else
                        {
                            _devices[device.BluetoothAddress] = new WearableDevice(
                                displayName,
                                device.BluetoothAddress.ToString("X12"),
                                FormatAddress(device.BluetoothAddress),
                                null,
                                false,
                                DateTimeOffset.Now,
                                info.Id);
                        }
                    }
                    catch
                    {
                        // Keep scanning advertisements if Windows cannot open this device.
                    }
                }
            }

            PublishDevices();
        }
        catch (Exception ex)
        {
            // Windows may restrict device enumeration; advertisement scanning remains active.
            LastStopError ??= $"Could not enumerate unpaired Bluetooth LE devices: {ex.Message}";
        }
    }

    private static bool NameSuggestsSamsungWearable(string name)
    {
        var normalized = name.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        var brandAndType =
            (name.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Gear", StringComparison.OrdinalIgnoreCase))
            && (name.Contains("Watch", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Fit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Gear", StringComparison.OrdinalIgnoreCase));

        // Model-only names need a stricter filter: Samsung also uses SM-R codes
        // for Galaxy Buds. Watch/band model families use R3xx, R7xx, R8xx, R9xx,
        // and the older Gear Sport R600; the L7xx family includes Galaxy Watch Ultra.
        var watchBandModelCode =
            System.Text.RegularExpressions.Regex.IsMatch(
                normalized, @"SM-R(3\d{2}|500|600|7\d{2}|8\d{2}|9\d{2})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(
                normalized, @"SM-V7\d{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || normalized.Contains("SM-L7", StringComparison.OrdinalIgnoreCase);

        return brandAndType || watchBandModelCode;
    }

    private void PublishDevices()
    {
        // BLE private addresses can rotate. Remove stale advertisement-only
        // entries while retaining devices found in Windows' paired-device list.
        var staleBefore = DateTimeOffset.Now - TimeSpan.FromMinutes(10);
        foreach (var entry in _devices)
        {
            if (entry.Value.SignalStrength is not null && entry.Value.LastSeen < staleBefore)
                _devices.TryRemove(entry.Key, out _);
        }

        var snapshot = _devices.Values
            .OrderByDescending(device => device.LastSeen)
            .ToArray();
        DevicesChanged?.Invoke(this, snapshot);
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
        var hasSamsungManufacturerData = args.Advertisement.ManufacturerData
            .Any(data => data.CompanyId == SamsungCompanyId);
        var hasWatchAppearance = HasWatchAppearance(args.Advertisement);
        // Use generic advertised watch identity instead of enumerating Samsung models.
        // Standard watch appearance plus Samsung manufacturer data is model-independent;
        // names are a separate fallback for devices that omit appearance metadata.
        var nameSuggestsSamsungWearable = NameSuggestsSamsungWearable(name);

        if (!(hasSamsungManufacturerData && hasWatchAppearance)
            && !nameSuggestsSamsungWearable)
            return;

        var addressId = args.BluetoothAddress.ToString("X12");
        var address = FormatAddress(args.BluetoothAddress);
        var seenAt = DateTimeOffset.Now;

        _devices.AddOrUpdate(
            args.BluetoothAddress,
            _ => new WearableDevice(
                string.IsNullOrWhiteSpace(name) ? "Samsung watch" : name,
                addressId,
                address,
                args.RawSignalStrengthInDBm,
                args.IsConnectable,
                seenAt),
            (_, existing) => new WearableDevice(
                string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                addressId,
                address,
                args.RawSignalStrengthInDBm,
                args.IsConnectable,
                seenAt,
                existing.DeviceInformationId));

        PublishDevices();
    }

    private static bool HasWatchAppearance(BluetoothLEAdvertisement advertisement)
    {
        foreach (var section in advertisement.GetSectionsByType(AppearanceDataType))
        {
            using var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(section.Data);
            if (reader.UnconsumedBufferLength < 2)
                continue;

            var appearance = reader.ReadUInt16();

            // Bluetooth SIG appearance values: generic watch (0x00C0),
            // sports watch (0x00C1), and smartwatch (0x00C2).
            if (appearance is 0x00C0 or 0x00C1 or 0x00C2)
                return true;
        }

        return false;
    }

    private static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6)
            .Select(index => ((address >> (index * 8)) & 0xFF).ToString("X2"))
            .Reverse());

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        LastStopError = args.Error == global::Windows.Devices.Bluetooth.BluetoothError.Success
            ? null
            : args.Error.ToString();
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
    }
}
