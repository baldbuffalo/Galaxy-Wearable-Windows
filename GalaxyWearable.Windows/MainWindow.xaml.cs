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
    private string? _connectionLostDeviceId;
    private Radio? _bluetoothRadio;
    private bool _isRestoringConnection;
    private bool _isClosed;
    private string SavedDevicePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalaxyWearableWindows", "paired-device.json");

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.DevicesChanged += Discovery_DevicesChanged;
        _connection.ConnectionStateChanged += Connection_ConnectionStateChanged;
        _ = InitializeBluetoothLifecycleAsync();
        Closed += async (_, _) =>
        {
            _isClosed = true;
            _discovery.DevicesChanged -= Discovery_DevicesChanged;
            _connection.ConnectionStateChanged -= Connection_ConnectionStateChanged;
            if (_bluetoothRadio is not null)
                _bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged;
            _discovery.Dispose();

            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BLE shutdown cleanup failed: {ex}");
                _connection.Dispose();
            }
        };
    }


    private string? _pendingUpdatePath;
    private string? _pendingUpdateUrl;
    private string? _pendingUpdateSha256;

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
            if (!string.IsNullOrWhiteSpace(_pendingUpdatePath) && File.Exists(_pendingUpdatePath))
                File.Delete(_pendingUpdatePath);
            _pendingUpdatePath = null;
            _pendingUpdateUrl = null;
            _pendingUpdateSha256 = null;

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Galaxy-Wearable-Windows-Updater");
            using var response = await client.GetAsync(
                "https://api.github.com/repos/baldbuffalo/Galaxy-Wearable-Windows/releases/latest",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var tag = json.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            if (tag is null
                || !tag.StartsWith("build-", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(tag.AsSpan("build-".Length), out var releaseBuild))
                throw new InvalidDataException("The latest release has an unexpected version tag.");

            var currentBuild = typeof(MainWindow).Assembly.GetName().Version?.Build ?? 0;
            if (releaseBuild <= currentBuild)
            {
                UpdateStatusText.Text = "You're using the latest version.";
                return;
            }

            if (!json.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The latest GitHub release has no downloadable assets.");

            var asset = assets.EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), "GalaxyWearable.Windows.exe", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined
                || !asset.TryGetProperty("browser_download_url", out var urlElement)
                || string.IsNullOrWhiteSpace(urlElement.GetString()))
                throw new InvalidDataException("The latest release has no GalaxyWearable.Windows.exe asset.");

            var candidateUrl = urlElement.GetString();
            if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var updateUri)
                || updateUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(updateUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !updateUri.AbsolutePath.StartsWith(
                    "/baldbuffalo/Galaxy-Wearable-Windows/releases/download/",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update URL is not a trusted HTTPS release asset for this repository.");

            _pendingUpdateUrl = updateUri.AbsoluteUri;
            if (asset.TryGetProperty("digest", out var digestElement))
            {
                var digest = digestElement.GetString();
                if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    _pendingUpdateSha256 = digest["sha256:".Length..];
            }

            UpdateStatusText.Text = $"Build {releaseBuild} is available. Select Update now to download and install it.";
            UpdateButton.Visibility = Visibility.Visible;
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

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingUpdateUrl))
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

        UpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Downloading update… the app package is large, so this may take a while.";
        string? temporaryDownload = null;
        try
        {
            const long maxInstallerBytes = 500L * 1024 * 1024;
            var folder = Path.Combine(Path.GetTempPath(), "GalaxyWearableWindowsUpdate");
            Directory.CreateDirectory(folder);
            temporaryDownload = Path.Combine(folder, $"GalaxyWearable.Windows.{Guid.NewGuid():N}.download");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Galaxy-Wearable-Windows-Updater");
            using (var response = await client.GetAsync(_pendingUpdateUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && (contentLength < 10L * 1024 * 1024 || contentLength > maxInstallerBytes))
                    throw new InvalidDataException($"The downloaded app size ({contentLength:N0} bytes) is outside the expected range.");

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = new FileStream(
                    temporaryDownload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
            }

            var downloadedInfo = new FileInfo(temporaryDownload);
            if (downloadedInfo.Length < 10L * 1024 * 1024 || downloadedInfo.Length > maxInstallerBytes)
                throw new InvalidDataException($"The downloaded app size ({downloadedInfo.Length:N0} bytes) is outside the expected range.");

            await using (var downloaded = File.OpenRead(temporaryDownload))
            {
                var header = new byte[2];
                if (await downloaded.ReadAsync(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidDataException("The release asset is not a valid Windows executable.");
            }

            if (!string.IsNullOrWhiteSpace(_pendingUpdateSha256))
            {
                await using var downloaded = File.OpenRead(temporaryDownload);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded));
                if (!string.Equals(actualHash, _pendingUpdateSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded executable failed GitHub's published SHA-256 digest check.");
            }

            _pendingUpdatePath = Path.Combine(folder, "GalaxyWearable.Windows.new.exe");
            File.Move(temporaryDownload, _pendingUpdatePath, overwrite: true);
            temporaryDownload = null;

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
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                "-ProcessId", Environment.ProcessId.ToString(), "-CurrentPath", currentPath,
                "-DownloadedPath", _pendingUpdatePath, "-ScriptPath", scriptPath })
                psi.ArgumentList.Add(arg);

            if (System.Diagnostics.Process.Start(psi) is null)
                throw new InvalidOperationException("Windows did not start the update installer.");

            UpdateStatusText.Text = "Installing update and restarting…";
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't download or start the update: {ex.Message}";
            UpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
            System.Diagnostics.Debug.WriteLine($"Update install error: {ex}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryDownload) && File.Exists(temporaryDownload))
            {
                try { File.Delete(temporaryDownload); }
                catch { /* Temp-file cleanup is best-effort. */ }
            }
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed)
            return;

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
            if (_isClosed)
                return;

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

                if (_isClosed)
                    return;

                radios = await Radio.GetRadiosAsync();
                if (_isClosed)
                    return;

                bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            }

            if (bluetoothRadio is null || bluetoothRadio.State != RadioState.On)
            {
                ScanButton.Content = "Find devices";
                DiscoveryHint.Text = "Bluetooth scanning is currently unavailable.";
                return;
            }

            await _discovery.StartAsync();
            if (_isClosed)
            {
                _discovery.Stop();
                return;
            }

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
            if (!_isClosed)
            {
                ScanButton.Content = "Find devices";
                DiscoveryHint.Text = $"Could not start Bluetooth scanning: {ex.Message}";
            }
            System.Diagnostics.Debug.WriteLine($"Bluetooth scan start error: {ex}");
        }
        finally
        {
            if (!_isClosed)
                ScanButton.IsEnabled = true;
        }
    }

    private async void DevicesList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingDevices || DevicesList.SelectedItem is not WearableDevice selected)
            return;

        // Startup auto-reconnect and a manual selection must not mutate the same
        // GATT session at the same time.
        if (_isRestoringConnection)
        {
            _isRefreshingDevices = true;
            try { DevicesList.SelectedItem = null; }
            finally { _isRefreshingDevices = false; }
            DiscoveryHint.Text = "Reconnecting to your saved wearable. Select a device again when this finishes.";
            return;
        }

        if (_connectedDeviceId == selected.Id && _connection.IsConnected)
        {
            DiscoveryHint.Text = $"{selected.Name} is connected.";
            return;
        }

        if (_connectingDeviceId is not null)
        {
            if (!string.Equals(_connectingDeviceId, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                // The connection service owns one GATT session. Revert the UI
                // selection instead of starting a second session concurrently.
                _isRefreshingDevices = true;
                try
                {
                    DevicesList.SelectedItem = _devices.GetValueOrDefault(_connectingDeviceId);
                }
                finally
                {
                    _isRefreshingDevices = false;
                }

                var activeName = _devices.GetValueOrDefault(_connectingDeviceId)?.Name ?? "the selected wearable";
                DiscoveryHint.Text = $"Connecting to {activeName}. Wait for this attempt to finish before selecting another device.";
            }
            return;
        }

        _connectingDeviceId = selected.Id;
        _connectionLostDeviceId = null;
        DiscoveryHint.Text = $"Connecting to {selected.Name}…";

        try
        {
            // Do not invoke Windows pairing or Samsung's new-phone setup flow. Open
            // the currently available BLE services only, preserving the existing S26 bond.
            var protocolReport = await _connection.ConnectAndInspectAsync(selected.Id, selected.DeviceInformationId);
            _connectedDeviceId = _connection.ConnectedBluetoothAddress ?? selected.Id;
            _connectionLostDeviceId = null;

            var saveWarning = string.Empty;
            try
            {
                await SavePairedDeviceAsync(selected);
            }
            catch (Exception saveException)
            {
                // A settings-folder write failure must not misreport an active
                // Bluetooth connection as failed or leave the user guessing.
                saveWarning = " The connection is active, but automatic reconnect could not be saved.";
                System.Diagnostics.Debug.WriteLine($"Saving the selected wearable failed: {saveException}");
            }

            DiscoveryHint.Text = $"Connected to {selected.Name} over BLE. Existing phone pairing was not changed.{saveWarning} {protocolReport}";
        }
        catch (Exception ex)
        {
            _connectedDeviceId = null;
            _connectionLostDeviceId = selected.Id;
            _isRefreshingDevices = true;
            try { DevicesList.SelectedItem = null; }
            finally { _isRefreshingDevices = false; }
            DiscoveryHint.Text = $"Couldn't open a BLE session to {selected.Name}. No new-phone setup was started. Select it to retry. Details: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"BLE connection error: {ex}");
        }
        finally
        {
            _connectingDeviceId = null;
        }
    }


    private async Task SavePairedDeviceAsync(WearableDevice device)
    {
        var folder = Path.GetDirectoryName(SavedDevicePath)!;
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(new SavedWearable(device.Name, _connection.ConnectedBluetoothAddress ?? device.Id, device.DeviceInformationId));
        await File.WriteAllTextAsync(SavedDevicePath, json);
    }

    private async Task InitializeBluetoothLifecycleAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(item => item.Kind == RadioKind.Bluetooth);
            if (_isClosed)
                return;

            _bluetoothRadio = radio;
            if (_bluetoothRadio is not null)
                _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;

            if (!_isClosed && _bluetoothRadio?.State == RadioState.On)
                await RestoreSavedDeviceAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bluetooth lifecycle initialization failed: {ex}");
        }
    }

    private async void BluetoothRadio_StateChanged(Radio sender, object args)
    {
        try
        {
            if (_isClosed)
                return;

            if (sender.State != RadioState.On)
            {
                _discovery.Stop();
                try
                {
                    await _connection.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Disconnect after Bluetooth radio change failed: {ex}");
                }

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosed)
                        return;
                    ScanButton.Content = "Find devices";
                    ScanButton.IsEnabled = true;
                    _connectedDeviceId = null;
                    DiscoveryHint.Text = "Bluetooth is off. Scanning stopped; your saved wearable will be retried when Bluetooth is on.";
                });
                return;
            }

            await Task.Delay(1200);
            if (!_isClosed && sender.State == RadioState.On)
                await RestoreSavedDeviceAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bluetooth radio state handler failed: {ex}");
        }
    }

    private async Task RestoreSavedDeviceAsync()
    {
        if (_isRestoringConnection || _connection.IsConnected)
            return;
        _isRestoringConnection = true;
        try
        {
            if (!File.Exists(SavedDevicePath))
                return;

            var saved = JsonSerializer.Deserialize<SavedWearable>(await File.ReadAllTextAsync(SavedDevicePath));
            if (saved is null || string.IsNullOrWhiteSpace(saved.Id))
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                    DiscoveryHint.Text = $"Reconnecting to {saved.Name}…";
            });
            await _connection.ConnectAndInspectAsync(saved.Id, saved.DeviceInformationId);
            _connectedDeviceId = _connection.ConnectedBluetoothAddress ?? saved.Id;
            _connectionLostDeviceId = null;
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                    DiscoveryHint.Text = $"Connected to saved wearable {saved.Name}.";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Saved wearable reconnect failed: {ex}");
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                    DiscoveryHint.Text = "Saved wearable is not reachable yet. It will be retried when Bluetooth is turned on again.";
            });
        }
        finally
        {
            _isRestoringConnection = false;
        }
    }

    private sealed record SavedWearable(string Name, string Id, string? DeviceInformationId = null);

    private void Connection_ConnectionStateChanged(string address, bool connected)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
                return;

            // Ignore delayed events from a previously selected wearable.
            if (!string.Equals(_connectedDeviceId, address, StringComparison.OrdinalIgnoreCase))
                return;

            if (!connected)
            {
                // Disconnect events from an intentional session replacement can
                // arrive after a new session to the same address has connected.
                if (_connection.IsConnected
                    && string.Equals(_connection.ConnectedBluetoothAddress, address, StringComparison.OrdinalIgnoreCase))
                    return;

                _connectedDeviceId = null;
                _connectionLostDeviceId = address;
                _isRefreshingDevices = true;
                try { DevicesList.SelectedItem = null; }
                finally { _isRefreshingDevices = false; }
                var name = _devices.GetValueOrDefault(address)?.Name ?? "your wearable";
                DiscoveryHint.Text = $"Connection to {name} was lost. Select it to reconnect.";
            }
        });
    }

    private void Discovery_DevicesChanged(object? sender, IReadOnlyList<WearableDevice> devices)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
                return;

            // Reconcile against the service snapshot so expired BLE advertisements
            // disappear from the UI instead of remaining indefinitely.
            _devices.Clear();
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
                : _connectingDeviceId is not null
                    ? $"Connecting to {_devices.GetValueOrDefault(_connectingDeviceId)?.Name ?? "your wearable"}…"
                    : _isRestoringConnection
                        ? "Reconnecting to your saved wearable…"
                        : _connectionLostDeviceId is not null
                            ? $"Connection to {_devices.GetValueOrDefault(_connectionLostDeviceId)?.Name ?? "your saved wearable"} isn't active. Select it to try again."
                            : _devices.Count == 0
                                ? "Choose Find devices to look for nearby Samsung wearables."
                                : $"Found {_devices.Count} likely Samsung wearable(s). Select a device to connect.";
        });
    }
}
