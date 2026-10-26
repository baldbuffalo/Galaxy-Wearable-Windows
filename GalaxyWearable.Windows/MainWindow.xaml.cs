using GalaxyWearable.Windows.Models;
using GalaxyWearable.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GalaxyWearable.Windows;

public sealed partial class MainWindow : Window
{
    private readonly BluetoothDiscoveryService _discovery = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<string, WearableDevice> _devices = new();

    public MainWindow()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.DevicesChanged += Discovery_DevicesChanged;
        Closed += (_, _) => _discovery.Dispose();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery.IsScanning)
        {
            _discovery.Stop();
            ScanButton.Content = "Start scanning";
            DiscoveryHint.Text = "Scanning stopped. Previously discovered devices remain listed.";
        }
        else
        {
            try
            {
                _discovery.Start();
                ScanButton.Content = "Stop scanning";
                DiscoveryHint.Text = "Scanning nearby Bluetooth LE advertisements…";
            }
            catch (Exception ex)
            {
                DiscoveryHint.Text = $"Could not start scanning: {ex.Message}";
            }
        }
    }

    private void Discovery_DevicesChanged(object? sender, IReadOnlyList<WearableDevice> devices)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var device in devices)
                _devices[device.Id] = device;

            DevicesList.ItemsSource = _devices.Values
                .OrderByDescending(device => device.LastSeen)
                .ToArray();

            DiscoveryHint.Text = $"Found {_devices.Count} device(s). This scan does not pair or authenticate.";
        });
    }
}
