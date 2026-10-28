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


    private string? _pendingUpdatePath;

    private async void OpenUpdates_Click(object sender, RoutedEventArgs e)
    {
        MainPage.Visibility = Visibility.Collapsed;
        UpdatesPage.Visibility = Visibility.Visible;
        if (UpdateStatusText.Text.StartsWith("Select Check", StringComparison.Ordinal))
            await CheckForUpdatesAsync();
    }

    private void BackFromUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdatesPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Galaxy-Wearable-Windows-Updater");
            using var response = await client.GetAsync("https://api.github.com/repos/baldbuffalo/Galaxy-Wearable-Windows/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var asset = json.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(x => string.Equals(x.GetProperty("name").GetString(), "GalaxyWearable.Windows.exe", StringComparison.OrdinalIgnoreCase));
            var url = asset.ValueKind == JsonValueKind.Undefined ? null : asset.GetProperty("browser_download_url").GetString();
            var currentPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            {
                UpdateStatusText.Text = "Couldn't find the latest app download.";
                return;
            }

            using var latestResponse = await client.GetAsync(url);
            latestResponse.EnsureSuccessStatusCode();
            var bytes = await latestResponse.Content.ReadAsByteArrayAsync();
            await using var current = File.OpenRead(currentPath);
            var currentHash = await SHA256.HashDataAsync(current);
            var latestHash = SHA256.HashData(bytes);
            if (CryptographicOperations.FixedTimeEquals(currentHash, latestHash))
            {
                UpdateStatusText.Text = "You're using the latest version.";
                return;
            }

            var folder = Path.Combine(Path.GetTempPath(), "GalaxyWearableWindowsUpdate");
            Directory.CreateDirectory(folder);
            _pendingUpdatePath = Path.Combine(folder, "GalaxyWearable.Windows.new.exe");
            await File.WriteAllBytesAsync(_pendingUpdatePath, bytes);
            UpdateStatusText.Text = "A new version is available.";
            UpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't check for updates: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Update check error: {ex}");
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingUpdatePath) || !File.Exists(_pendingUpdatePath))
        {
            UpdateStatusText.Text = "Check for updates again before installing.";
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            UpdateStatusText.Text = "Couldn't locate the app executable.";
            return;
        }

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"GalaxyWearableUpdate-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, """
                param([int]$ProcessId, [string]$CurrentPath, [string]$DownloadedPath, [string]$ScriptPath)
                $ErrorActionPreference = 'Stop'
                while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }
                try {
                    Copy-Item -LiteralPath $DownloadedPath -Destination $CurrentPath -Force
                    Start-Process -FilePath $CurrentPath
                } catch {
                    Add-Type -AssemblyName PresentationFramework
                    [System.Windows.MessageBox]::Show("Update installation failed. Try moving the app to a writable folder. $($_.Exception.Message)", "Galaxy Wearable update")
                } finally {
                    Remove-Item -LiteralPath $DownloadedPath -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
                }
                """);
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                "-ProcessId", Environment.ProcessId.ToString(), "-CurrentPath", currentPath,
                "-DownloadedPath", _pendingUpdatePath, "-ScriptPath", scriptPath })
                psi.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(psi);
            UpdateStatusText.Text = "Installing update and restarting…";
            Application.Current.Exit();
        }
        catch (Exception ex) { UpdateStatusText.Text = $"Couldn't start installer: {ex.Message}"; }
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
