# Galaxy Wearable for Windows

A native Windows companion app exploring Bluetooth connectivity and interoperability with Samsung Galaxy wearables, starting with the Galaxy Fit3.

> **Status:** Early development. This project is not affiliated with or endorsed by Samsung. A Windows Bluetooth connection is not the same as completing Samsung's proprietary companion-pairing protocol.

## Goals

- Native Windows 10/11 desktop experience, built with C# and WinUI 3.
- Modular device and capability architecture so support can expand without copying Samsung's screen-by-screen UI.
- Discover nearby Bluetooth LE devices and inspect basic advertisement information.
- Build toward stable connection and automatic reconnection.
- Investigate documented and publicly researched Fit3 services before implementing device-specific communication.
- Clearly distinguish tested features from experimental or unavailable features.

## Current milestone

The initial app shell provides a device discovery view and a service layer for Bluetooth LE advertisements. It does **not** yet claim to pair as the official Galaxy Wearable companion, read all health data, or support firmware updates.

## Requirements

- Windows 10 version 1809 or later (Windows 11 recommended)
- Visual Studio 2022 with the **WinUI application development** workload and Windows App SDK support
- Bluetooth hardware for live discovery

## Build

Open `GalaxyWearable.Windows.sln` in Visual Studio 2022, restore NuGet packages, select the `x64` configuration, and run the `GalaxyWearable.Windows` project. Bluetooth discovery requires Bluetooth to be enabled in Windows.

## Project layout

- `GalaxyWearable.Windows/` — WinUI 3 app and UI
- `Models/` — device and capability models
- `Services/` — Bluetooth discovery and future protocol implementations

## Protocol research

The Fit3 may expose standard Bluetooth services alongside Samsung-specific services. Initial discovery is intentionally read-only: it does not send guessed proprietary commands or attempt to bypass authentication. Any protocol support will be documented with its evidence and test status.

## License

MIT — see [LICENSE](LICENSE).
