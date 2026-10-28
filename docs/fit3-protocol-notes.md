# Galaxy Fit3 BLE protocol notes

Last reviewed: 2026-10-27

> **Scope note:** The Galaxy Fit3 is the first available physical test device for Galaxy Wearable for Windows. The product goal is support for Samsung Galaxy watches and fitness bands generally, not Fit3-only support. These notes describe one test case and must not be treated as a universal Samsung protocol specification.

## Evidence and limits

The public [Fit3Free project](https://github.com/Astroisback/Fit3Free) documents a Galaxy Fit3 capture made over BLE without pairing. Its published notes report:

- Standard Device Information service `180A`.
- Samsung/vendor-specific services, including `1A1A`, `FD69`, and custom 128-bit UUID services. The Fit3 SAP channel on service `1A1A` has a published capability exchange and transport framing reference.
- Some values and services are unavailable without the relevant bond/authentication state.

This is third-party reverse-engineering research, not a Samsung protocol specification. It may vary by firmware. We use it as a research lead for the Fit3 only, not as a guarantee for every Fit3 firmware version or other Samsung wearable.

## Shared app architecture

The general-purpose Windows app uses shared BLE discovery, connection, and GATT inspection. Standard Bluetooth services can be used when a particular device exposes them. Samsung companion pairing and device-specific features need evidence-based protocol handlers/capabilities; they must not be assumed identical across all product families.

## SAP capability handshake implementation

The public [Fit3Bridge protocol reference](https://github.com/GokulArumugam/Fit3Bridge/blob/main/docs/PROTOCOL.md) documents the following Fit3 SAP exchange for firmware R390XXU0AZA3. This Windows implementation was written independently from that protocol description; it does not copy the reference project's GPL source code.

- Service: `00001a1a-0000-1000-8000-00805f9b34fb`
- Band-to-host notification characteristic: `797ae4e9-2e58-4fe8-b48d-b5c79599fb9b`
- Host-to-band write characteristic: `63e30bad-4206-4596-839f-e47cbf7a4b5d`
- The band sends a raw 105-byte capability request with byte 0 = 104 and byte 1 = `0x14`; the host replies with the same bytes except byte 1 = `0x15`.
- The transport MTU is read from request offsets `0x2c` and `0x2d`.
- After a successful capability reply, the app sends a framed, read-only WatchInfo request (SAP service 1, message `01 04 02 00 00 00 05 00 0e`) and logs valid single-frame responses, including a firmware identifier if present.
- Frame encoding/decoding validates the documented CRC-16/ARC framing.

This is the **SAP transport capability handshake**, not proof of phone identity or a universal authentication bypass. The protocol reference says there is no additional application-level cryptography in this transport handshake; BLE bond requirements and firmware-specific access restrictions are separate. The app deliberately does not send the remaining OOBE setup messages (device status, locale/time initialization, or user agreement), and does not invoke Windows pairing or reset the watch.

## What this Windows app now does

1. **Discovery:** listens for BLE advertisements and filters for likely Samsung wearables using generic advertised names and standard metadata.
2. **Connection:** opens the selected BLE device through Windows' Bluetooth LE APIs.
3. **GATT inspection:** requests the live service list and characteristic properties and shows them in the app.

5. **Standard identity fields:** reads readable manufacturer, model, firmware, and software fields from `180A` when the device exposes them. These fields may be placeholders or unavailable on some wearables.

These are transport/discovery capabilities. A successful BLE connection is not the same thing as a Samsung watch believing this PC is an authenticated Galaxy Wearable phone.

## Not implemented / not verified

- Full Samsung-phone identity/pairing emulation and any bond-dependent authorization. The documented SAP capability exchange is implemented, but a successful SAP transport handshake does not mean the PC is authenticated as a Samsung phone.
- Authenticated/bonded proprietary data channels, key negotiation, or phone identity.
- Notifications, settings sync, health history, firmware updates, and other companion-app features.
- Automatic reconnect after a drop.
- Live testing against a physical Fit3 or any other watch.

The app must not send guessed proprietary commands or claim phone-like pairing until the handshake is understood and validated against real devices. Standard BLE services can be useful without that proprietary pairing, but they do not reproduce the complete Samsung companion experience.

## Findings from public protocol research (2026-10-28)

The public Fit3Free capture reports proprietary services including `1A1A`, `FD69`, and custom 128-bit services, with useful data characteristics on at least some channels requiring a bond. A service UUID being visible is not proof that its payload is usable.

The Windows app now deliberately avoids calling Windows `PairAsync` from its device-selection flow and does not initiate Samsung's new-phone setup. It connects to the BLE address, enumerates visible services. This can provide a limited unbonded data path without changing the existing S26 pairing. It is **not** phone-protocol emulation: the authenticated Samsung accessory handshake, bond/key state, and proprietary message framing remain unknown and are not guessed. The app cannot reuse the S26's existing bond merely by connecting to the same Fit3; Bluetooth bond credentials are managed by each host's Bluetooth stack.

A true compatible-phone implementation still needs a captured, authorized protocol trace and a complete understanding of the pairing/authentication exchange. We must not infer keys, replay unknown payloads, or write to proprietary characteristics based only on their UUIDs.

## Next validation steps

1. Run the app with the Fit3 nearby and confirm its advertised name/address appears.
2. Select it and connect; save the displayed GATT UUIDs and any communication statuses.
4. Confirm whether the read-only WatchInfo request receives a valid SAP response and record the firmware/software versions.
5. Compare observed service/characteristic UUIDs and permissions with the published Fit3Free/Fit3Bridge references, recording firmware/software versions.
6. After the first Fit3 baseline, repeat discovery and capability tests on other Samsung watch families when hardware is available.
7. Investigate bond-dependent pairing and authorization separately using an authorized protocol trace. Do not treat the SAP capability exchange as proof that the PC has authenticated as a phone.
