# PTPMonitor

PTP (Precision Time Protocol) monitoring and diagnostic tool.

![Dashboard Screenshot](assets/dashboard_live.png)

## Features
- Network diagnostic for PTPv1/v2.
- Real-time status visualization.
- BMC Algorithm status analysis.

## Documentation
- [Manual (JP)](docs/manual_JP.md)
- [Manual (ENG)](docs/manual_EN.md)

## How to use
Run `PTPMonitor.exe` (requires proper configuration in `config.ini`).

## Changelog

### [v1.7.0] - 2026-07-09
#### Fixed
- **PTPv1 (Dante) Parsing Overhaul**:
    - Message classification now uses the `control` field (offset 32) per IEEE 1588-2002; previously unreachable conditions prevented v1 GM extraction, SyncLog updates, and Delay_Resp topology linking from ever running.
    - Corrected wire-format offsets verified against the Wireshark PTP dissector: `grandmasterClockUuid` (54), `syncInterval` (83), `requestingSourceUuid` (50).
- **Web UI Stability**:
    - JSON responses are now properly escaped and locale-independent (fixes dashboard breakage on comma-decimal locales or malformed v1 subdomains).
    - HTTP responses are always closed (`try-finally`), preventing socket leaks and server hangs after client disconnects.
    - Fixed polling-loop multiplication when clicking Clear buttons; fixed v1/v2 follower counting; fixed garbled button icons.
- **Configuration Robustness**: Invalid numeric values in `config.ini` no longer overwrite defaults with 0; parsing is culture-invariant; values may contain `=`.
#### Changed
- Web server now handles requests concurrently and reports startup failures (e.g., port in use).
- Packet receiving uses blocking sockets (no more per-second timeout exceptions); errors are classified and logged.
- Removed external Google Fonts dependency so the dashboard renders fully offline.
- CSV export now escapes quotes and neutralizes spreadsheet formula injection.
#### Added
- BMC info (`Priority1` / `ClockClass` / `Priority2`) display for PTPv2 Leaders.
- Device-count cap (512) as a memory-safety guard.

### [v1.6.8] - 2026-04-29
#### Added
- **Safety & Robustness**:
    - Added confirmation dialogs for destructive actions (Clear All/Offline) in Web UI.
    - Added Live Monitoring indicator and Last Update timestamp to Web UI.
    - Implemented explicit IP binding for sockets to improve multi-NIC stability.
    - Added socket receive timeouts to prevent potential hangs.
- **UI/UX Enhancement**:
    - Created a professional application icon and integrated it into the Windows executable build process.



### [v1.6.7] - 2026-04-06
#### Added
- **Monitoring Threshold Customization (config.ini)**:
    - Added `ExpectedDelayInterval`: Base interval for Delay_Req packets (seconds).
    - Added `DelayAlertThresholdRate`: Multiplier for alerting thresholds.
    - Added `OfflineTimeoutSeconds`: Duration before a device is marked as offline.
- **Dynamic Alert Visualization (Web UI)**:
    - Implemented logic to highlight `Delay Intv` in red based on "Baseline x Multiplier" configuration.

#### Fixed
- Resolved an issue where `Delay Interval` measurements were not displayed on the Web UI.

### [v1.6.6] - 2026-04-06
#### Added
- **Delay Request Interval Measurement**:
    - Implemented logic to measure real-time intervals of `Delay_Req` packets from Followers.
    - Added display of measured intervals next to `Uptime` in the Web UI.

### [v1.6.4] - 2026-04-06
#### Changed
- **PTPv1 UI Optimization**:
    - Hidden unnecessary interface values (e.g., Sync/Announce Logs) for PTPv1 (Dante) nodes to improve dashboard clarity.

### [v1.6.1] - 2026-04-04
#### Fixed
- **Uptime Display Stabilization**:
    - Corrected countdown and display logic for device uptime immediately after startup.
- **Documentation Update**:
    - Refined user manuals and `config.ini` descriptions for the v1.6.x series features.
