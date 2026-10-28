using System.Collections.Concurrent;
using global::Windows.Devices.Bluetooth.Advertisement;
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

        if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created
            or BluetoothLEAdvertisementWatcherStatus.Stopped)
        {
            _watcher.Start();
        }

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

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
        var hasSamsungManufacturerData = args.Advertisement.ManufacturerData
            .Any(data => data.CompanyId == SamsungCompanyId);
        var hasWatchAppearance = HasWatchAppearance(args.Advertisement);
        var advertisesHeartRateService = args.Advertisement.ServiceUuids
            .Any(uuid => uuid == global::Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids.HeartRate);

        // Use generic advertised watch identity instead of enumerating Samsung models.
        // Standard watch appearance is the strongest model-independent signal.
        var nameSuggestsSamsungWearable =
            (name.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Gear", StringComparison.OrdinalIgnoreCase))
            && (name.Contains("Watch", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Fit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Gear", StringComparison.OrdinalIgnoreCase));

        if (!(hasSamsungManufacturerData && (hasWatchAppearance || advertisesHeartRateService))
            && !nameSuggestsSamsungWearable)
            return;

        var displayName = string.IsNullOrWhiteSpace(name)
            ? "Samsung watch"
            : name;

        _devices[args.BluetoothAddress] = new WearableDevice(
            displayName,
            args.BluetoothAddress.ToString("X12"),
            FormatAddress(args.BluetoothAddress),
            args.RawSignalStrengthInDBm,
            args.IsConnectable,
            DateTimeOffset.Now);

        var snapshot = _devices.Values
            .OrderByDescending(device => device.LastSeen)
            .ToArray();

        DevicesChanged?.Invoke(this, snapshot);
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
