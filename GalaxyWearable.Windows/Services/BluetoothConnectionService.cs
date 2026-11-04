using System.Globalization;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.GenericAttributeProfile;
using global::Windows.Storage.Streams;

namespace GalaxyWearable.Windows.Services;

/// <summary>
/// Opens a BLE GATT connection and inspects services without pretending that
/// a GATT connection completes Samsung's proprietary companion pairing flow.
/// </summary>
public sealed class BluetoothConnectionService : IDisposable, IAsyncDisposable
{
    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private string? _gattSessionSetupNote;
    private readonly List<GattDeviceService> _services = new();
    private GattCharacteristic? _sapNotifyCharacteristic;
    private GattCharacteristic? _sapWriteCharacteristic;
    private TaskCompletionSource<bool>? _sapHandshakeCompleted;
    private TaskCompletionSource<bool>? _sapResponseReceived;
    private readonly SemaphoreSlim _sapWriteLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly List<string> _sapDiagnostics = new();
    private int _sapTransportMtu;
    private int _sapHandshakeStarted;
    private bool _sapWatchInfoRequestSent;
    private volatile bool _sapWatchInfoResponseReceived;
    private volatile bool _sapDeviceStatusRequestSent;
    private int _disposed;

    private static readonly Guid SapServiceUuid = Guid.Parse("00001a1a-0000-1000-8000-00805f9b34fb");
    private static readonly Guid SapNotifyUuid = Guid.Parse("797ae4e9-2e58-4fe8-b48d-b5c79599fb9b");
    private static readonly Guid SapWriteUuid = Guid.Parse("63e30bad-4206-4596-839f-e47cbf7a4b5d");
    public event Action<string, bool>? ConnectionStateChanged;

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public string? ConnectedBluetoothAddress => _device?.BluetoothAddress.ToString("X12");

    public async Task<string> ConnectAndInspectAsync(string bluetoothAddress, string? deviceInformationId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sessionLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await ConnectAndInspectCoreAsync(bluetoothAddress, deviceInformationId);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<string> ConnectAndInspectCoreAsync(string bluetoothAddress, string? deviceInformationId)
    {
        var hasAddress = ulong.TryParse(
            bluetoothAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address);
        if (!hasAddress && string.IsNullOrWhiteSpace(deviceInformationId))
            throw new ArgumentException("The selected device has neither a valid Bluetooth address nor a Windows device ID.", nameof(bluetoothAddress));

        await DisconnectCoreAsync();

        // Prefer Windows' device identity when available because BLE private
        // addresses can rotate. Fall back to the last observed address if needed.
        if (!string.IsNullOrWhiteSpace(deviceInformationId))
        {
            try
            {
                _device = await BluetoothLEDevice.FromIdAsync(deviceInformationId);
            }
            catch
            {
                _device = null;
            }
        }

        if (_device is null && hasAddress)
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

        if (_device is null)
            throw new InvalidOperationException("Windows could not open this Bluetooth LE device. Make sure it is nearby, available, and not blocked by another connection.");

        _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

        // Keep a Windows GATT session alive across discovery and SAP CCCD setup.
        // This does not pair, unpair, reset, or replace the phone's bond.
        try
        {
            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_gattSession is null)
            {
                _gattSessionSetupNote = "Windows did not provide a GATT session.";
            }
            else if (_gattSession.CanMaintainConnection)
            {
                _gattSession.MaintainConnection = true;
                _gattSessionSetupNote = "Requested a maintained GATT connection through Windows.";
            }
            else
            {
                _gattSessionSetupNote = "Windows GATT session does not support MaintainConnection for this device.";
            }
        }
        catch (Exception ex)
        {
            _gattSessionSetupNote = $"Could not initialize a maintained GATT session: {DescribeException(ex)}";
        }

        try
        {
            var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Connected to the BLE device, but service discovery returned: {result.Status}.");

            _services.AddRange(result.Services);
        }
        catch
        {
            // Do not retain a half-open device after GATT discovery fails.
            await DisconnectCoreAsync();
            throw;
        }

        var lines = new List<string>
        {
            $"Device: {_device.Name}",
            $"BLE connection: {_device.ConnectionStatus}",
            $"GATT services found: {_services.Count}",
            "",
            "Services and characteristics:"
        };
        if (!string.IsNullOrWhiteSpace(_gattSessionSetupNote))
            lines.Add($"• GATT session: {_gattSessionSetupNote}");
        lines.Add(GetGattLinkSnapshot("After service discovery"));

        foreach (var service in _services)
        {
            lines.Add($"• Service {service.Uuid}");
            try
            {
                var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (characteristicsResult.Status != GattCommunicationStatus.Success)
                {
                    lines.Add($"  Could not enumerate characteristics: {characteristicsResult.Status}");
                    continue;
                }

                foreach (var characteristic in characteristicsResult.Characteristics)
                    lines.Add($"  • {characteristic.Uuid} [{characteristic.CharacteristicProperties}]");
            }
            catch (Exception ex)
            {
                lines.Add($"  Characteristic enumeration failed: {ex.Message}");
            }
        }

        lines.Add("");
        lines.Add("Fit3 protocol assessment:");
        var serviceIds = _services.Select(service => service.Uuid).ToHashSet();
        if (serviceIds.Any(uuid => uuid == Guid.Parse("00001a1a-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung 1A1A service is visible; its documented streaming characteristic may require an existing Bluetooth bond.");
        if (serviceIds.Any(uuid => uuid == Guid.Parse("0000fd69-0000-1000-8000-00805f9b34fb")))
            lines.Add("• Samsung FD69 service is visible; no proprietary writes are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("eedd5e73-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 crypto-handshake service family is visible. Its full handshake is not implemented; no guessed keys or payloads are sent.");
        if (_services.Any(service => service.Uuid.ToString().StartsWith("1b7e8251-", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• A known Fit3 accessory-channel service family is visible; protected operations remain unimplemented.");

        var identity = await ReadStandardDeviceIdentityAsync();
        if (!string.IsNullOrWhiteSpace(identity))
            lines.Add($"• Standard device information: {identity}");

        lines.Add("");
        lines.Add("Samsung Accessory Protocol (SAP):");
        try
        {
            lines.AddRange(await StartSapSessionAsync());
        }
        catch (Exception ex)
        {
            lines.Add($"• SAP probe failed without starting pairing/setup: {DescribeException(ex)}");
        }

        lines.Add("");
        lines.Add("• Watch setup/reset state: UNKNOWN until a documented status response can be decoded.");
        lines.Add("• Windows GATT authentication may be requested for SAP when you explicitly select this watch. The app does not send Samsung NEW_PHONE, watch-reset, or first-run setup messages.");
        lines.Add("• SAP capability negotiation is not a claim that Windows has inherited the phone's Bluetooth bond.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeException(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? "(Windows provided no exception message)"
            : ex.Message.Trim();
        var details = $"{ex.GetType().FullName ?? ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}; Message={message}";

        if (ex.InnerException is not null)
        {
            var innerMessage = string.IsNullOrWhiteSpace(ex.InnerException.Message)
                ? "(no message)"
                : ex.InnerException.Message.Trim();
            details += $" | Inner={ex.InnerException.GetType().FullName ?? ex.InnerException.GetType().Name}; HRESULT=0x{ex.InnerException.HResult:X8}; Message={innerMessage}";
        }

        return details;
    }

    private async Task<IReadOnlyList<string>> StartSapSessionAsync()
    {
        var service = _services.FirstOrDefault(item => item.Uuid == SapServiceUuid);
        if (service is null)
            return new[] { "• SAP service (1A1A) is not visible in this BLE session." };

        GattCharacteristicsResult result;
        try
        {
            result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            return new[] { $"• SAP characteristic discovery threw an error: {DescribeException(ex)}" };
        }

        if (result.Status != GattCommunicationStatus.Success)
            return new[] { $"• SAP characteristic discovery failed: {result.Status}." };

        _sapNotifyCharacteristic = result.Characteristics.FirstOrDefault(item => item.Uuid == SapNotifyUuid);
        _sapWriteCharacteristic = result.Characteristics.FirstOrDefault(item => item.Uuid == SapWriteUuid);
        if (_sapNotifyCharacteristic is null || _sapWriteCharacteristic is null)
            return new[]
            {
                "• SAP service is visible, but the expected notify/write characteristics were not both found.",
                $"  Notify characteristic found: {_sapNotifyCharacteristic is not null}; write characteristic found: {_sapWriteCharacteristic is not null}."
            };

        if ((_sapNotifyCharacteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0)
            return new[] { "• SAP notify characteristic does not advertise notification support." };

        if ((_sapWriteCharacteristic.CharacteristicProperties &
             (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
            return new[] { "• SAP write characteristic does not advertise a supported write property." };

        lock (_sapDiagnostics)
            _sapDiagnostics.Clear();
        Volatile.Write(ref _sapHandshakeStarted, 0);
        _sapTransportMtu = 0;
        _sapWatchInfoRequestSent = false;
        _sapWatchInfoResponseReceived = false;
        _sapDeviceStatusRequestSent = false;
        _sapHandshakeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sapResponseReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkBeforeCccd = GetGattLinkSnapshot("Immediately before SAP CCCD write");

        // Ask Windows to negotiate an encrypted and authenticated BLE link for
        // the protected SAP attributes. This may trigger the normal Windows
        // Bluetooth pairing/authentication UI. It does not send Samsung
        // NEW_PHONE, reset, or first-run setup messages.
        try
        {
            _sapNotifyCharacteristic.ProtectionLevel =
                GattProtectionLevel.EncryptionAndAuthenticationRequired;
            _sapWriteCharacteristic.ProtectionLevel =
                GattProtectionLevel.EncryptionAndAuthenticationRequired;
            AddSapDiagnostic("• Requested Windows GATT security: encryption + authentication for both SAP characteristics. Windows will use its own OS-managed Bluetooth security state; this does not import the S26 bond.");
        }
        catch (Exception ex)
        {
            return new[]
            {
                "• Windows could not apply the requested SAP GATT protection level.",
                $"  {DescribeException(ex)}",
                "• No Samsung pairing, reset, or new-phone setup messages were sent."
            };
        }

        _sapNotifyCharacteristic.ValueChanged -= SapNotifyCharacteristic_ValueChanged;
        _sapNotifyCharacteristic.ValueChanged += SapNotifyCharacteristic_ValueChanged;

        GattWriteResult notifyResult;
        try
        {
            notifyResult = await _sapNotifyCharacteristic
                .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }
        catch (Exception ex)
        {
            _sapNotifyCharacteristic.ValueChanged -= SapNotifyCharacteristic_ValueChanged;
            return new[]
            {
                linkBeforeCccd,
                GetGattLinkSnapshot("After SAP CCCD exception"),
                $"• Enabling SAP notifications threw a Windows GATT error: {DescribeException(ex)}"
            };
        }

        var linkAfterCccd = GetGattLinkSnapshot("Immediately after SAP CCCD write");
        if (notifyResult.Status != GattCommunicationStatus.Success)
        {
            _sapNotifyCharacteristic.ValueChanged -= SapNotifyCharacteristic_ValueChanged;

            var protocolErrorText = notifyResult.ProtocolError is byte protocolError
                ? protocolError switch
                {
                    0x05 => "0x05 (ATT_INSUFFICIENT_AUTHENTICATION)",
                    0x0F => "0x0F (ATT_INSUFFICIENT_ENCRYPTION)",
                    _ => $"0x{protocolError:X2}"
                }
                : "not supplied by Windows";

            return new[]
            {
                linkBeforeCccd,
                $"• Could not enable SAP notifications: {notifyResult.Status}.",
                $"  ATT protocol error: {protocolErrorText}.",
                linkAfterCccd,
                "• No Samsung NEW_PHONE, reset, or first-run setup messages were sent."
            };
        }

        AddSapDiagnostic(linkAfterCccd);
        AddSapDiagnostic("• Subscribed to the documented SAP notification characteristic.");
        var completed = await Task.WhenAny(_sapHandshakeCompleted.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        if (completed != _sapHandshakeCompleted.Task)
        {
            AddSapDiagnostic("• No 105-byte SAP capability request arrived within 6 seconds; no pairing or setup was attempted.");
            return GetSapDiagnosticsSnapshot();
        }

        // Wait for the documented read-only OOBE Device Status response.
        // The status query is sent only after a valid WatchInfo response.
        if (_sapWatchInfoRequestSent)
        {
            var responseTask = _sapResponseReceived?.Task;
            if (responseTask is not null && await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(5))) != responseTask)
            {
                AddSapDiagnostic(!_sapWatchInfoResponseReceived
                    ? "• No WatchInfo response arrived within 5 seconds; Device Status was not requested."
                    : "• WatchInfo succeeded, but no Device Status response arrived within 5 seconds.");
            }
        }
        return GetSapDiagnosticsSnapshot();
    }

    private async void SapNotifyCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);

            if (bytes.Length == 105 && bytes[0] == 104 && bytes[1] == 0x14)
            {
                await HandleSapCapabilityRequestAsync(bytes);
                return;
            }

            if (TryDecodeSapFrame(bytes, out var serviceId, out var message))
            {
                var hex = Convert.ToHexString(message);
                AddSapDiagnostic($"• SAP frame received from service {serviceId}: {hex}");

                if (serviceId == 1 && message.Length > 0 && message[0] == 0x41)
                {
                    _sapWatchInfoResponseReceived = true;
                    var ascii = System.Text.Encoding.ASCII.GetString(message);
                    var firmwareMatch = System.Text.RegularExpressions.Regex.Match(ascii, @"R390[A-Z0-9]{7,12}");
                    if (firmwareMatch.Success)
                        AddSapDiagnostic($"• Fit3 firmware reported by WatchInfo: {firmwareMatch.Value}");

                    await SendSapDeviceStatusRequestAsync();
                }
                else if (serviceId == 1 && message.Length > 0 && message[0] == 0x42)
                {
                    AddSapDiagnostic("• Read-only OOBE Device Status response received. Raw response retained above; status fields are not yet decoded.");
                    _sapResponseReceived?.TrySetResult(true);
                }
            }
            else
            {
                AddSapDiagnostic($"• SAP notification received ({bytes.Length} bytes); it was not a recognized capability request or valid frame.");
            }
        }
        catch (Exception ex)
        {
            AddSapDiagnostic($"• SAP notification processing failed: {ex.Message}");
        }
    }

    private async Task HandleSapCapabilityRequestAsync(byte[] request)
    {
        if (Interlocked.Exchange(ref _sapHandshakeStarted, 1) != 0)
            return;

        _sapTransportMtu = (request[0x2c] << 8) | request[0x2d];
        AddSapDiagnostic($"• Received SAP capability request (105 bytes); advertised transport MTU: {_sapTransportMtu}.");

        var response = (byte[])request.Clone();
        response[1] = 0x15;
        var replyStatus = await WriteSapBytesAsync(response);
        if (replyStatus != GattCommunicationStatus.Success)
        {
            AddSapDiagnostic($"• SAP capability response write failed: {replyStatus}. The protocol session is not ready.");
            _sapHandshakeCompleted?.TrySetResult(false);
            return;
        }

        AddSapDiagnostic("• Sent the matching SAP capability response (opcode 0x15).");

        // The smallest encoded WatchInfo frame is 17 bytes. Bound the reported
        // transport size to the protocol's documented 4096-byte reassembly limit.
        if (_sapTransportMtu < 17 || _sapTransportMtu > 4096)
        {
            AddSapDiagnostic("• The advertised SAP transport MTU is outside the supported 17–4096 byte range; WatchInfo was not sent.");
            _sapHandshakeCompleted?.TrySetResult(true);
            return;
        }

        try
        {
            // Read-only OOBE WatchInfo request (service 1, message 1). Do not send
            // device-status, locale/time setup, or user-agreement messages here.
            byte[] watchInfo = { 0x01, 0x04, 0x02, 0x00, 0x00, 0x00, 0x05, 0x00, 0x0e };
            var frame = EncodeSapSingleFrame(1, watchInfo, _sapTransportMtu);
            var watchInfoStatus = await WriteSapBytesAsync(frame);
            _sapWatchInfoRequestSent = watchInfoStatus == GattCommunicationStatus.Success;
            AddSapDiagnostic(_sapWatchInfoRequestSent
                ? "• Sent read-only WatchInfo request; waiting for the band response."
                : $"• WatchInfo request write failed: {watchInfoStatus}.");
        }
        catch (Exception ex)
        {
            AddSapDiagnostic($"• WatchInfo request could not be encoded or written: {ex.Message}");
            _sapHandshakeCompleted?.TrySetResult(false);
            return;
        }

        _sapHandshakeCompleted?.TrySetResult(true);
    }

    private async Task SendSapDeviceStatusRequestAsync()
    {
        if (_sapDeviceStatusRequestSent || _sapTransportMtu < 17)
            return;

        _sapDeviceStatusRequestSent = true;
        // OOBE message 2 is a documented status query. It does not send
        // locale/time initialization, user agreement, reset, or new-phone data.
        byte[] deviceStatusRequest = { 0x02, 0x01, 0x00 };
        try
        {
            var frame = EncodeSapSingleFrame(1, deviceStatusRequest, _sapTransportMtu);
            var status = await WriteSapBytesAsync(frame);
            AddSapDiagnostic(status == GattCommunicationStatus.Success
                ? "• Sent the documented read-only OOBE Device Status query."
                : $"• Device Status query write failed: {status}.");
        }
        catch (Exception ex)
        {
            AddSapDiagnostic($"• Device Status query could not be encoded or written: {ex.Message}");
        }
    }

    private async Task<GattCommunicationStatus> WriteSapBytesAsync(byte[] bytes)
    {
        if (_sapWriteCharacteristic is null)
            return GattCommunicationStatus.Unreachable;

        await _sapWriteLock.WaitAsync();
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            var properties = _sapWriteCharacteristic.CharacteristicProperties;
            var option = (properties & GattCharacteristicProperties.Write) != 0
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;
            var result = await _sapWriteCharacteristic.WriteValueWithResultAsync(writer.DetachBuffer(), option);
            if (result.Status == GattCommunicationStatus.Success
                && option == GattWriteOption.WriteWithoutResponse)
            {
                // The Fit3 transport reference recommends pacing no-response writes.
                await Task.Delay(50);
            }
            return result.Status;
        }
        finally
        {
            _sapWriteLock.Release();
        }
    }

    private static byte[] EncodeSapSingleFrame(byte serviceId, byte[] message, int transportMtu)
    {
        var body = new byte[] { 0x00, 0x40, serviceId, serviceId }.Concat(message).ToArray();
        byte[] prefix;
        if (transportMtu < 256)
        {
            if (body.Length > 255)
                throw new InvalidOperationException("SAP short frame is too large.");
            prefix = new[] { (byte)body.Length, (byte)body.Length };
        }
        else
        {
            var length = new[] { (byte)(body.Length >> 8), (byte)(body.Length & 0xff) };
            var crc = SapCrc16(length);
            prefix = length.Concat(new[] { (byte)(crc >> 8), (byte)(crc & 0xff) }).ToArray();
        }

        var bodyCrc = SapCrc16(body);
        var frame = prefix.Concat(body).Concat(new[] { (byte)(bodyCrc >> 8), (byte)(bodyCrc & 0xff) }).ToArray();
        if (frame.Length > transportMtu)
            throw new InvalidOperationException($"SAP WatchInfo frame ({frame.Length} bytes) exceeds the advertised MTU ({transportMtu}).");
        return frame;
    }

    private static bool TryDecodeSapFrame(byte[] frame, out int serviceId, out byte[] message)
    {
        serviceId = 0;
        message = Array.Empty<byte>();
        if (frame.Length < 8)
            return false;

        var shortLength = (int)frame[0];
        var shortPrefix = frame[0] == frame[1] && shortLength + 4 == frame.Length;
        var longLength = (frame[0] << 8) | frame[1];
        var expectedLengthCrc = SapCrc16(new[] { frame[0], frame[1] });
        var longPrefix = frame.Length >= 10 && longLength + 6 == frame.Length
            && frame[2] == (byte)(expectedLengthCrc >> 8)
            && frame[3] == (byte)(expectedLengthCrc & 0xff);

        int prefixLength;
        if (shortPrefix)
            prefixLength = 2;
        else if (longPrefix)
            prefixLength = 4;
        else
            return false;

        var bodyLength = frame.Length - prefixLength - 2;
        if (bodyLength < 4)
            return false;
        var body = frame.Skip(prefixLength).Take(bodyLength).ToArray();
        var expectedBodyCrc = SapCrc16(body);
        if (frame[^2] != (byte)(expectedBodyCrc >> 8) || frame[^1] != (byte)(expectedBodyCrc & 0xff))
            return false;
        if ((body[0] >> 6) != 0 || body[2] != body[3] || ((body[0] >> 1) & 0x03) != 0)
            return false;

        serviceId = body[2];
        message = body.Skip(4).ToArray();
        return true;
    }

    private static ushort SapCrc16(IEnumerable<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xa001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        ConnectionStateChanged?.Invoke(
            sender.BluetoothAddress.ToString("X12"),
            sender.ConnectionStatus == BluetoothConnectionStatus.Connected);
    }

    private string GetGattLinkSnapshot(string stage)
    {
        var bleStatus = _device?.ConnectionStatus.ToString() ?? "Unavailable";
        if (_gattSession is null)
            return $"• {stage}: BLE={bleStatus}; GATT session unavailable.";

        return $"• {stage}: BLE={bleStatus}; GATT session={_gattSession.SessionStatus}; " +
               $"CanMaintainConnection={_gattSession.CanMaintainConnection}; " +
               $"MaintainConnection={_gattSession.MaintainConnection}; ATT MTU={_gattSession.MaxPduSize}.";
    }

    private void DisposeGattSession()
    {
        if (_gattSession is null)
            return;

        try
        {
            _gattSession.MaintainConnection = false;
        }
        catch
        {
            // Disposal must continue even if Windows has already closed the session.
        }

        _gattSession.Dispose();
        _gattSession = null;
        _gattSessionSetupNote = null;
    }

    private void AddSapDiagnostic(string message)
    {
        lock (_sapDiagnostics)
            _sapDiagnostics.Add(message);
    }

    private string[] GetSapDiagnosticsSnapshot()
    {
        lock (_sapDiagnostics)
            return _sapDiagnostics.ToArray();
    }

    private async Task<string?> ReadStandardDeviceIdentityAsync()
    {
        var service = _services.FirstOrDefault(item =>
            item.Uuid == Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb"));
        if (service is null)
            return null;

        GattCharacteristicsResult result;
        try
        {
            result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            return $"service present; characteristic discovery failed: {ex.Message}";
        }

        if (result.Status != GattCommunicationStatus.Success)
            return $"service present; characteristic discovery returned {result.Status}";

        var fields = new List<string>();
        foreach (var characteristic in result.Characteristics)
        {
            var label = characteristic.Uuid == Guid.Parse("00002a29-0000-1000-8000-00805f9b34fb") ? "manufacturer"
                : characteristic.Uuid == Guid.Parse("00002a24-0000-1000-8000-00805f9b34fb") ? "model"
                : characteristic.Uuid == Guid.Parse("00002a26-0000-1000-8000-00805f9b34fb") ? "firmware"
                : characteristic.Uuid == Guid.Parse("00002a28-0000-1000-8000-00805f9b34fb") ? "software"
                : null;
            if (label is null || (characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0)
                continue;

            try
            {
                var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success || read.Value is null)
                    continue;
                using var reader = DataReader.FromBuffer(read.Value);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                var value = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(value))
                    fields.Add($"{label} {value}");
            }
            catch
            {
                // Some standard identity fields may be unavailable without affecting BLE connection.
            }
        }

        return fields.Count == 0 ? "service present, no readable identity fields" : string.Join(", ", fields);
    }

    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _sessionLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
                await DisconnectCoreAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private Task DisconnectCoreAsync()
    {
        DisposeGattSession();

        if (_sapNotifyCharacteristic is not null)
            _sapNotifyCharacteristic.ValueChanged -= SapNotifyCharacteristic_ValueChanged;
        _sapNotifyCharacteristic = null;
        _sapWriteCharacteristic = null;
        _sapHandshakeCompleted?.TrySetCanceled();
        _sapResponseReceived?.TrySetCanceled();
        _sapHandshakeCompleted = null;
        _sapResponseReceived = null;
        Volatile.Write(ref _sapHandshakeStarted, 0);
        _sapWatchInfoRequestSent = false;
        _sapWatchInfoResponseReceived = false;
        _sapDeviceStatusRequestSent = false;
        _sapTransportMtu = 0;
        lock (_sapDiagnostics)
            _sapDiagnostics.Clear();

        foreach (var service in _services)
            service.Dispose();
        _services.Clear();

        if (_device is not null)
        {
            var address = _device.BluetoothAddress.ToString("X12");
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            _device.Dispose();
            _device = null;
            ConnectionStateChanged?.Invoke(address, false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Synchronous fallback for callers that cannot await disposal. The
        // window uses DisposeAsync so active WinRT operations finish first.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CleanupResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Do not dispose GATT services while an async connection/inspection is
        // still using them. Waiting asynchronously avoids blocking the UI thread.
        await _sessionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            CleanupResources();
            _sessionLock.Release();
        }
    }

    private void CleanupResources()
    {
        DisposeGattSession();

        if (_sapNotifyCharacteristic is not null)
            _sapNotifyCharacteristic.ValueChanged -= SapNotifyCharacteristic_ValueChanged;
        _sapNotifyCharacteristic = null;
        _sapWriteCharacteristic = null;
        _sapHandshakeCompleted?.TrySetCanceled();
        _sapResponseReceived?.TrySetCanceled();
        _sapHandshakeCompleted = null;
        _sapResponseReceived = null;
        Volatile.Write(ref _sapHandshakeStarted, 0);
        _sapWatchInfoRequestSent = false;
        _sapWatchInfoResponseReceived = false;
        _sapDeviceStatusRequestSent = false;
        _sapTransportMtu = 0;

        foreach (var service in _services)
            service.Dispose();
        _services.Clear();

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
    }
}
