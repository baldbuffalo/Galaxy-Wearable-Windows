using GalaxyWearable.Windows.Models;
using GalaxyWearable.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Devices.Radios;

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

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery.IsScanning)
        {
            _discovery.Stop();
            ScanButton.Content = "Start scanning";
            DiscoveryHint.Text = "Scanning stopped. Previously discovered devices remain listed.";
            return;
        }

        ScanButton.IsEnabled = false;
        DiscoveryHint.Text = "Starting Bluetooth scan…";

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

            if (bluetoothRadio is not null && bluetoothRadio.State != RadioState.On)
            {
                // Try to enable Bluetooth automatically. Windows may deny this for
                // unpackaged desktop apps or radios that do not allow programmatic control.
                try
                {
                    await bluetoothRadio.SetStateAsync(RadioState.On);
                }
                catch
                {
                    // Continue below; do not interrupt the user's scan action.
                }

                radios = await Radio.GetRadiosAsync();
                bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            }

            if (bluetoothRadio is null || bluetoothRadio.State != RadioState.On)
            {
                ScanButton.Content = "Start scanning";
                DiscoveryHint.Text = "Bluetooth scanning is currently unavailable.";
                return;
            }

            _discovery.Start();

            if (_discovery.IsScanning)
            {
                ScanButton.Content = "Stop scanning";
                DiscoveryHint.Text = "Scanning nearby Bluetooth LE devices…";
            }
            else
            {
                ScanButton.Content = "Start scanning";
                DiscoveryHint.Text = "Bluetooth scanning could not be started.";
            }
        }
        catch
        {
            ScanButton.Content = "Start scanning";
            DiscoveryHint.Text = "Bluetooth scanning is currently unavailable.";
        }
        finally
        {
            ScanButton.IsEnabled = true;
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
