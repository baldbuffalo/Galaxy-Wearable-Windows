# Galaxy Wearable for Windows

A native Windows companion app aiming to support Samsung Galaxy smartwatches and fitness bands across product generations. The Galaxy Fit3 is the first physical test device, not the app's only target.

> **Status:** Early research build. This project is not affiliated with or endorsed by Samsung. BLE discovery/connection does not mean a watch has completed Samsung's proprietary companion-pairing protocol.

## Design goals

- Model-independent discovery and BLE connection, without maintaining a hardcoded list of watch models.
- Shared GATT inspection and standard Bluetooth feature support where devices expose compatible services.
- Pluggable capability/protocol handlers for device families that require different behavior.
- Clear separation between standard BLE functionality and Samsung-specific authenticated companion features.

## Current features

- Scan for likely Samsung wearables using advertised names, Samsung manufacturer data, and standard watch/heart-rate service indicators.
- Select a discovered device and open a Bluetooth LE connection.
- Enumerate exposed GATT services and characteristics and display their UUIDs/properties.
- Subscribe to the standard Heart Rate Measurement characteristic (`2A37`) when exposed and actively notifying.
- Attempt to turn on the Windows Bluetooth radio before scanning, subject to Windows permissions and hardware support.

## Important limitations

The app does **not yet impersonate a Galaxy phone**. Samsung companion pairing/authentication, proprietary accessory-protocol handshakes, authenticated data channels, notifications/settings sync, health history, firmware updates, and automatic reconnect are not implemented or verified. A successful BLE connection is only the transport layer.

Samsung watches do not all expose identical services or use identical pairing flows. Support must be verified per device family and firmware; the Fit3 is our first test target. The app does not send guessed proprietary commands or claim full phone-like pairing.

See [Fit3 protocol research notes](docs/fit3-protocol-notes.md) for the initial device research, which is explicitly a test case rather than the whole product scope.

## Requirements

- Windows 10 version 1809 or later (Windows 11 recommended)
- Visual Studio 2022 with the **WinUI application development** workload and Windows App SDK support
- Bluetooth LE-capable hardware for live discovery and connection

## Build

Open `GalaxyWearable.Windows.sln` in Visual Studio, restore NuGet packages, select the `x64` configuration, and run the `GalaxyWearable.Windows` project. Bluetooth discovery requires Bluetooth to be enabled in Windows. SDK and package versions are intentionally unchanged.

## Project layout

- `GalaxyWearable.Windows/` — WinUI 3 app and UI
- `GalaxyWearable.Windows/Models/` — device models
- `GalaxyWearable.Windows/Services/` — shared BLE discovery and connection/GATT services
- `docs/fit3-protocol-notes.md` — initial Fit3 test-device research and limitations

## References

- [Fit3Free: public Galaxy Fit3 BLE/GATT observations](https://github.com/Astroisback/Fit3Free) — third-party reverse-engineering research, not an official Samsung specification.
- [Samsung Gulf: pairing the Galaxy Fit3 with a phone](https://www.samsung.com/ae/support/mobile-devices/pairing-your-galaxy-fit3-with-your-phone/).

## License

MIT — see [LICENSE](LICENSE).
