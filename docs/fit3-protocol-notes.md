# Galaxy Fit3 BLE protocol notes

Last reviewed: 2026-10-27

## Evidence and limits

The public [Fit3Free project](https://github.com/Astroisback/Fit3Free) documents a Galaxy Fit3 capture made over BLE without pairing. Its published notes report:

- Standard Heart Rate service `180D`, with Heart Rate Measurement characteristic `2A37`; notifications can be available while the watch is actively measuring.
- Standard Device Information service `180A`.
- Samsung/vendor-specific services, including `1A1A`, `FD69`, and custom 128-bit UUID services. The notes describe some useful characteristics as bond-gated and identify a likely main accessory-protocol channel.
- Some values and services are unavailable without the relevant bond/authentication state.

This is third-party reverse-engineering research, not a Samsung protocol specification. It may vary by firmware. We use it as a research lead, not as a guarantee for every Fit3 firmware version.

## What this Windows app now does

1. **Discovery:** listens for BLE advertisements and filters for generic Samsung/Galaxy/Gear wearable names or Samsung manufacturer data combined with standard watch appearance.
2. **Connection:** opens the selected BLE device through Windows' Bluetooth LE APIs.
3. **GATT inspection:** requests the live service list and characteristic properties and shows them in the app.
4. **Standard heart-rate path:** can subscribe to the standard `2A37` notification characteristic when it is exposed and the watch is publishing measurements.

These are transport/discovery capabilities. A successful BLE connection is not the same thing as the Fit3 believing this PC is an authenticated Galaxy Wearable phone.

## Not implemented / not verified

- Samsung companion pairing and the full Samsung Accessory Protocol handshake.
- Authenticated/bonded proprietary data channels, key negotiation, or phone identity.
- Notifications, settings sync, health history, firmware updates, and other companion-app features.
- Automatic reconnect after a drop.
- A live test on a physical Fit3.

The app must not send guessed proprietary commands or claim phone-like pairing until the handshake is understood and validated against a real device. Standard BLE services can be useful without that proprietary pairing, but they do not reproduce the complete Samsung companion experience.

## Next validation steps

1. Run the app with the Fit3 nearby and confirm its advertised name/address appears.
2. Select it and connect; save the displayed GATT UUIDs and any communication statuses.
3. Confirm whether `180D/2A37` is exposed and whether notifications arrive while a workout or continuous heart-rate measurement is active.
4. Compare observed service/characteristic UUIDs and permissions with the published Fit3Free reference, recording firmware/software versions.
5. Research the proprietary handshake from legitimate, observable behavior before attempting companion pairing. Do not treat the existence of a UUID as evidence that its payload format or authentication is known.
