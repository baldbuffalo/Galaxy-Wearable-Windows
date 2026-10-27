# Galaxy Wearable for Windows

A native Windows companion app exploring Bluetooth connectivity and interoperability with Samsung Galaxy wearables, starting with the Galaxy Fit3.

> **Status:** Early research build. This project is not affiliated with or endorsed by Samsung. BLE discovery/connection does not mean the watch has completed Samsung's proprietary companion-pairing protocol.

## Current features

- Scan for likely Samsung/Galaxy/Gear wearables using advertisement names, Samsung manufacturer data, and standard watch/heart-rate service indicators.
- Select a discovered device and open a Bluetooth LE connection.
- Enumerate exposed GATT services and characteristics and display their UUIDs/properties.
- Subscribe to the standard Heart Rate Measurement characteristic (`2A37`) when exposed and actively notifying.
- Attempt to turn on the Windows Bluetooth radio before scanning, subject to Windows permissions and hardware support.

## Important limitations

The app does **not yet impersonate a Galaxy phone**. Samsung companion pairing/authentication, the proprietary accessory-protocol handshake, authenticated data channels, notifications/settings sync, health history, firmware updates, and automatic reconnect are not implemented or verified. A successful BLE connection is only the transport layer.

The app does not send guessed proprietary commands or claim full phone-like pairing. See [Fit3 protocol research notes](docs/fit3-protocol-notes.md) for the known evidence, limits, and next validation steps.

## Requirements

- Windows 10 version 1809 or later (Windows 11 recommended)
- Visual Studio 2022 with the **WinUI application development** workload and Windows App SDK support
- Bluetooth LE-capable hardware for live discovery and connection

## Build

Open `GalaxyWearable.Windows.sln` in Visual Studio, restore NuGet packages, select the `x64` configuration, and run the `GalaxyWearable.Windows` project. Bluetooth discovery requires Bluetooth to be enabled in Windows. SDK and package versions are intentionally unchanged.

## Project layout

- `GalaxyWearable.Windows/` — WinUI 3 app and UI
- `GalaxyWearable.Windows/Models/` — device models
- `GalaxyWearable.Windows/Services/` — BLE discovery and connection/GATT services
- `docs/fit3-protocol-notes.md` — research evidence and protocol limitations

## References

- [Fit3Free: public Galaxy Fit3 BLE/GATT observations](https://github.com/Astroisback/Fit3Free) — third-party reverse-engineering research, not an official Samsung specification.
- [Samsung Gulf: pairing the Galaxy Fit3 with a phone](https://www.samsung.com/ae/support/mobile-devices/pairing-your-galaxy-fit3-with-your-phone/).

## License

MIT — see [LICENSE](LICENSE).
