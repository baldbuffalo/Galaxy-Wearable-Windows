using GalaxyWearable.Windows.Models;
using GalaxyWearable.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using global::Windows.Devices.Radios;

namespace GalaxyWearable.Windows;

public sealed partial class MainWindow : Window
{
    private readonly BluetoothDiscoveryService _discovery = new();
    private readonly BluetoothConnectionService _connection = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<string, WearableDevice> _devices = new();
    private bool _isRefreshingDevices;
    private string? _connectingDeviceId;
    private string? _connectedDeviceId;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.DevicesChanged += Discovery_DevicesChanged;
        Closed += (_, _) =>
        {
            _discovery.Dispose();
            _connection.Dispose();
        };
    }


    private void OpenUpdates_Click(object sender, RoutedEventArgs e)
    {
        MainPage.Visibility = Visibility.Collapsed;
        UpdatesPage.Visibility = Visibility.Visible;
    }

    private void BackFromUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdatesPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking the latest release and comparing SHA-256 hashes…";

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Galaxy-Wearable-Windows-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var releaseResponse = await client.GetAsync(
                "https://api.github.com/repos/baldbuffalo/Galaxy-Wearable-Windows/releases/latest");
            releaseResponse.EnsureSuccessStatusCode();

            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync();
            using var releaseJson = await JsonDocument.ParseAsync(releaseStream);

            var assets = releaseJson.RootElement.GetProperty("assets");
            string? downloadUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(),
                                  "GalaxyWearable.Windows.exe",
                                  StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                UpdateStatusText.Text = "The latest GitHub release does not contain the app executable.";
                return;
            }

            var currentPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            {
                UpdateStatusText.Text = "Could not locate the running executable to calculate its SHA-256 hash.";
                return;
            }

            var currentHash = await CalculateSha256Async(currentPath);
            using var latestResponse = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            latestResponse.EnsureSuccessStatusCode();
            await using var latestStream = await latestResponse.Content.ReadAsStreamAsync();
            var latestHashBytes = await SHA256.HashDataAsync(latestStream);
            var latestHash = Convert.ToHexString(latestHashBytes);

            if (string.Equals(currentHash, latestHash, StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatusText.Text = $"You're up to date. SHA-256: {currentHash}";
            }
            else
            {
                UpdateStatusText.Text =
                    $"A different build is available (release {releaseJson.RootElement.GetProperty("tag_name").GetString()}). " +
                    $"The SHA-256 hashes differ.\nInstalled: {currentHash}\nLatest: {latestHash}\n" +
                    "This check does not install the update automatically.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't check for updates: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Update check error: {ex}");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private static async Task<string> CalculateSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery.IsScanning)
        {
            _discovery.Stop();
            ScanButton.Content = "Find devices";
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
                ScanButton.Content = "Find devices";
                DiscoveryHint.Text = "Bluetooth scanning is currently unavailable.";
                return;
            }

            await _discovery.StartAsync();

            if (_discovery.IsScanning)
            {
                ScanButton.Content = "Stop scanning";
                DiscoveryHint.Text = "Looking for nearby Samsung Galaxy watches and bands…";
            }
            else
            {
                ScanButton.Content = "Find devices";
                DiscoveryHint.Text = "Bluetooth scanning could not be started.";
            }
        }
        catch (Exception ex)
        {
            ScanButton.Content = "Find devices";
            DiscoveryHint.Text = $"Could not start Bluetooth scanning: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Bluetooth scan start error: {ex}");
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void DevicesList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingDevices || DevicesList.SelectedItem is not WearableDevice selected)
            return;

        if (_connectedDeviceId == selected.Id && _connection.IsConnected)
        {
            DiscoveryHint.Text = $"{selected.Name} is connected.";
            return;
        }

        if (_connectingDeviceId == selected.Id)
            return;

        _connectingDeviceId = selected.Id;
        DiscoveryHint.Text = $"Connecting to {selected.Name}…";

        try
        {
            await _connection.ConnectAndInspectAsync(selected.Id);
            _connectedDeviceId = selected.Id;
            DiscoveryHint.Text = $"Connected to {selected.Name}.";
        }
        catch (Exception ex)
        {
            _connectedDeviceId = null;
            DiscoveryHint.Text = $"Couldn't connect to {selected.Name}. Make sure it is nearby and available, then select it again.";
            System.Diagnostics.Debug.WriteLine($"BLE connection error: {ex}");
        }
        finally
        {
            _connectingDeviceId = null;
        }
    }

    private void Discovery_DevicesChanged(object? sender, IReadOnlyList<WearableDevice> devices)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var device in devices)
                _devices[device.Id] = device;

            var selectedId = (DevicesList.SelectedItem as WearableDevice)?.Id;
            var items = _devices.Values.OrderByDescending(device => device.LastSeen).ToArray();

            _isRefreshingDevices = true;
            try
            {
                DevicesList.ItemsSource = items;
                if (selectedId is not null)
                    DevicesList.SelectedItem = items.FirstOrDefault(device => device.Id == selectedId);
            }
            finally
            {
                _isRefreshingDevices = false;
            }

            DiscoveryHint.Text = _connectedDeviceId is not null
                ? $"Connected to {_devices.GetValueOrDefault(_connectedDeviceId)?.Name ?? "your wearable"}."
                : _devices.Count == 0
                    ? "Choose Find devices to look for nearby Samsung wearables."
                    : $"Found {_devices.Count} likely Samsung wearable(s). Select a device to connect.";
        });
    }
}
