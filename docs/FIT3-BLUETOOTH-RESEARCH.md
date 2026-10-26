# Galaxy Fit3 Bluetooth research notes

## Objective

Understand the Fit3's publicly observable Bluetooth behavior and determine which parts of a companion connection can be implemented on Windows.

## Current status

- [x] Native WinUI 3 app shell and BLE advertisement discovery service added.
- [ ] Test discovery against a real Galaxy Fit3 on Windows.
- [ ] Record advertised local name, address, connectability, signal strength, and manufacturer/service data where available.
- [ ] Inspect GATT services only after a user explicitly selects a device and Windows grants access.
- [ ] Compare findings with public, documented research and open-source implementations.
- [ ] Determine the complete pairing sequence, including pairing confirmation and any companion-specific protocol.
- [ ] Implement reconnect behavior only after a supported connection flow is known.

## Important distinctions

- Bluetooth visibility does not prove a device is paired.
- A paired Windows Bluetooth device is not automatically a Samsung Galaxy Wearable companion.
- BLE advertisement scanning does not reveal all GATT services; services are generally queried after connecting.
- A service UUID alone does not prove what a proprietary characteristic does.
- No guessed write commands should be sent to the band. Any protocol implementation should be grounded in public documentation or reproducible observations.

## Test log

Add Windows version, Bluetooth adapter, Fit3 firmware version, scan observations, and reproduction steps here. Do not include personal health data or account credentials.
