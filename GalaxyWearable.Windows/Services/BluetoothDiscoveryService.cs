using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.Advertisement;
using GalaxyWearable.Windows.Models;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Bluetooth LE discovery filtered to advertisements identifying as a Galaxy Fit3.
/// This does not pair with a device or implement Samsung's companion protocol.
/// </summary>
public sealed class BluetoothDiscoveryService : IDisposable
{
    private const string TargetDeviceName = "Galaxy Fit3";

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
        // Ignore unrelated Bluetooth devices. Only list advertisements whose
        // advertised name identifies the target Galaxy Fit3.
        var name = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name)
            || !name.Contains(TargetDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _devices[args.BluetoothAddress] = new WearableDevice(
            name,
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
