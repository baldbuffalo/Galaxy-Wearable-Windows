using GalaxyWearable.Windows.Models;
using GalaxyWearable.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Devices.Radios;

namespace GalaxyWearable.Windows;

public sealed partial class MainWindow : Window
{
    private readonly BluetoothDiscoveryService _discovery = new();
    private readonly BluetoothConnectionService _connection = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<string, WearableDevice> _devices = new();

    public MainWindow()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.DevicesChanged += Discovery_DevicesChanged;
        _connection.HeartRateChanged += Connection_HeartRateChanged;
        Closed += (_, _) =>
        {
            _discovery.Dispose();
            _connection.Dispose();
        };
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
                try
                {
                    await bluetoothRadio.SetStateAsync(RadioState.On);
                }
                catch
                {
                    // Windows may block programmatic radio changes for unpackaged desktop apps.
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
                DiscoveryHint.Text = "Scanning nearby Bluetooth LE advertisements. Select a device to connect.";
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

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsConnected)
        {
            ConnectButton.IsEnabled = false;
            try
            {
                await _connection.DisconnectAsync();
                ConnectButton.Content = "Connect selected";
                ConnectionHint.Text = "BLE connection closed.";
                GattDetails.Text = string.Empty;
            }
            catch (Exception ex)
            {
                ConnectionHint.Text = $"Disconnect issue: {ex.Message}";
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
            return;
        }

        if (DevicesList.SelectedItem is not WearableDevice selected)
        {
            ConnectionHint.Text = "Select your Galaxy Fit3 or another detected watch first.";
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectionHint.Text = $"Connecting to {selected.Name} and discovering GATT services…";
        GattDetails.Text = string.Empty;

        try
        {
            GattDetails.Text = await _connection.ConnectAndInspectAsync(selected.Id);
            ConnectButton.Content = "Disconnect";
            ConnectionHint.Text = "BLE connected. Samsung companion pairing is not yet established.";
        }
        catch (Exception ex)
        {
            ConnectionHint.Text = $"Could not connect: {ex.Message}";
            ConnectButton.Content = "Connect selected";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void HeartRateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConnectionHint.Text = await _connection.StartHeartRateNotificationsAsync();
        }
        catch (Exception ex)
        {
            ConnectionHint.Text = $"Heart-rate subscription failed: {ex.Message}";
        }
    }

    private void Connection_HeartRateChanged(object? sender, string message)
    {
        _dispatcherQueue.TryEnqueue(() => ConnectionHint.Text = message);
    }

    private void Discovery_DevicesChanged(object? sender, IReadOnlyList<WearableDevice> devices)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var device in devices)
                _devices[device.Id] = device;

            var selectedId = (DevicesList.SelectedItem as WearableDevice)?.Id;
            var items = _devices.Values.OrderByDescending(device => device.LastSeen).ToArray();
            DevicesList.ItemsSource = items;
            if (selectedId is not null)
                DevicesList.SelectedItem = items.FirstOrDefault(device => device.Id == selectedId);

            DiscoveryHint.Text = $"Found {_devices.Count} likely Samsung wearable(s). Select one to connect.";
        });
    }
}
