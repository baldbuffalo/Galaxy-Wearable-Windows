using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.Advertisement;
using GalaxyWearable.Windows.Models;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Bluetooth LE discovery that identifies likely Samsung wearables from generic
/// Samsung branding or Samsung manufacturer data, rather than specific watch models.
/// This does not pair with a device or implement Samsung's companion protocol.
/// </summary>
public sealed class BluetoothDiscoveryService : IDisposable
{
    // Bluetooth SIG company identifier for Samsung Electronics.
    private const ushort SamsungCompanyId = 0x0075;

    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active
    };
    private readonly ConcurrentDictionary<ulong, WearableDevice> _devices = new();

    public event EventHandler<IReadOnlyList<WearableDevice>>? DevicesChanged;
    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public BluetoothDiscoveryService()
    {
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    public void Start()
    {
        if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created
            or BluetoothLEAdvertisementWatcherStatus.Stopped)
        {
            _watcher.Start();
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

        // Do not maintain a list of watch models. Accept generic Samsung/Galaxy/Gear
        // branding, or an advertisement explicitly carrying Samsung's company ID.
        var hasSamsungBranding =
            name.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gear", StringComparison.OrdinalIgnoreCase);

        if (!hasSamsungManufacturerData && !hasSamsungBranding)
            return;

        var displayName = string.IsNullOrWhiteSpace(name)
            ? "Samsung wearable"
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

    private static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6)
            .Select(index => ((address >> (index * 8)) & 0xFF).ToString("X2"))
            .Reverse());

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // The UI can inspect Status if Windows stops scanning (for example, Bluetooth off).
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
    }
}
