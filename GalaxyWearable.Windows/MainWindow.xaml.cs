using GalaxyWearable.Windows.Models;
using GalaxyWearable.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Devices.Radios;
using Windows.System;

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

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

            if (bluetoothRadio is null || bluetoothRadio.State != RadioState.On)
            {
                ScanButton.Content = "Start scanning";
                DiscoveryHint.Text = "Bluetooth is turned off. Turn on Bluetooth in Windows Settings, then try scanning again. Opening Bluetooth settings…";
                await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
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
                DiscoveryHint.Text = "Windows could not start Bluetooth scanning. Check that Bluetooth is on and try again.";
            }
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x800710DF)
            || ex.HResult == unchecked((int)0x80070005))
        {
            ScanButton.Content = "Start scanning";
            DiscoveryHint.Text = "Bluetooth is unavailable or Windows denied access. Turn on Bluetooth in Windows Settings and try again.";
            await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
        }
        catch
        {
            ScanButton.Content = "Start scanning";
            DiscoveryHint.Text = "Could not start Bluetooth scanning. Make sure Bluetooth is turned on in Windows Settings, then try again.";
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
