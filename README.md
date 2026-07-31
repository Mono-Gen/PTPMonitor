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

### [v1.8.1] - 2026-08-01
#### Fixed
- **Resource Exhaustion Guards**: Per-device (PTP version, domain) instance count is now capped (`MaxProtocolStatesPerDevice`), preventing a single spoofed source from growing memory/scan cost unbounded via arbitrary PTPv1 subdomains. The 512-device table now evicts the least-recently-seen device (LRU) instead of rejecting new ones once full, so spoofed IP churn can no longer permanently lock out real devices.
- **Spoofed Leader/Role Hardening**: Leader-role assignment from Announce/Delay_Resp packets (v1 and v2) is now gated on the minimum packet length required to carry real content for those message types, closing a cheap fake-Leader spoof from undersized packets.
- **Stale Role/Link Detection**: A Leader/Follower role no longer stays "online" purely because of Management/Signaling/Pdelay traffic; only role-defining packets (Sync/Announce/Delay_Req/Delay_Resp) keep it alive. A confirmed topology link now also expires if it isn't reconfirmed by a fresh Delay_Resp within a bounded window, even while its Leader stays otherwise valid.
- **PTPv1 Subdomain Decoding**: Invalid (non-UTF-8) subdomain byte sequences now fall back to a raw hex key instead of colliding on the same replacement-character string.
- **Web Server**: `/api/*` responses now send `Cache-Control: no-store`; unmapped paths return `404` instead of always serving the dashboard HTML.
- **CSV Export**: Output now includes a UTF-8 BOM so older Excel versions don't mangle non-ASCII vendor names.
- **Build Script**: `csc.exe` is now auto-detected under `Framework(64)\v*` instead of a hardcoded path/version.

### [v1.8.0] - 2026-07-15
#### Fixed
- **GM Mismatch Detection**: This check previously relied on a value a pure Follower never sets, so it silently never fired; it now runs only once the parent link is `Delay_Resp`-confirmed, and shows `(inherited, unverified)` when the Follower's own GM cannot be independently verified.
- **Boundary Clock Topology**: Protocol state is now tracked per (PTP version, domain) instead of per version, so a Boundary Clock acting as Leader/Follower on multiple domains of the same PTP version is tracked correctly instead of one role overwriting the other.
- **Socket Error Handling**: `SocketException`s are classified by `SocketErrorCode` (transient vs. persistent) instead of being retried unconditionally; explicit `SO_RCVBUF` sizing; fixed a socket handle leak on Bind/AddMembership failure.
- **Input Validation**: `config.ini` numeric values reject NaN/Infinity/out-of-range inputs; PTPv1/v2 packets are validated for message-type/port/control consistency and rejected otherwise; Web UI port input is strictly parsed.
- **CSV Export**: Formula-injection guard now also strips leading whitespace/control characters before checking for `=+-@`.
- **Web Server**: `HttpListener` startup failures (e.g., port already bound) no longer silently kill the server; added a 30s response timeout and a simple Origin-based CSRF check on the data-clearing endpoints.
#### Changed
- Conflict and GM-mismatch detection moved from a per-packet full scan to a 1Hz periodic evaluation with edge-triggered logging; log rotation is capped at 20 generations.
- Elapsed-time tracking uses a monotonic `Stopwatch` tick instead of wall-clock time, so it's unaffected by NTP corrections or manual clock changes.
- Concurrent HTTP requests are capped (64) to bound resource usage under load.
- Dashboard HTML/CSS/JS moved from inline C# string constants to `assets/web/*`, embedded via `csc /resource` (keeps the .exe dependency-free while allowing the JS to be lint/syntax-checked independently of the C# build).
#### Added
- **Web UI**: device search/filter box, an active-alerts banner (click to scroll to the node), log-level filters (`ERROR`/`WARN`/`CONFLICT`/`MISMATCH`), and a dashed border to visually distinguish unconfirmed topology links.

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
