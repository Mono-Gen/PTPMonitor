# PTPMonitor User Manual (v1.12.0)

PTPMonitor is a specialized protocol analyzer designed to capture PTP (Precision Time Protocol) v1 / v2 traffic, visualize device status, Boundary Clock (BC) operations, and network topology for high-precision diagnostic purposes.

## Requirements
- Windows OS (.NET Framework 4.0 or later)
- Administrator privileges (required for packet capture)

## Setup and Startup
1. Run `PTPMonitor.exe` as Administrator.
2. Select the network adapter index for monitoring.
3. Specify the Web UI port (default: 8080).
4. Open `http://localhost:8080/` in your browser.

## Key Diagnostic Features
### 1. High-Precision Topology Visualization
Based on `Delay_Resp` packet analysis, the tool provides an accurate tree view of which Followers are synchronized to which Leaders.

### 2. Intelligent Conflict Detection (BMCA Grace Period)
A conflict means **multiple distinct Grandmasters in the same domain**: Leaders whose GM ID, read from Announce (v2) / Sync (v1), differs. Several Leaders reporting the *same* GM (a GM plus the Boundary Clocks below it) is normal and is not flagged. A Leader whose GM has not been read yet (shown as `(own ID, awaiting GM report)`) is left out of the check until it is. Severity is determined by duration:
- **BMCA (Negotiating) [Yellow]**: Multiple GMs detected for **less than 10 seconds**. This is shown as a normal negotiation process during master switching.
- **CONFLICT (Persistent) [Red]**: Conflict persisting for **10 seconds or more**. Recorded as a configuration error or dual-master fault in the logs (`[CONFLICT_ALERT]`, which lists the GM IDs involved).

### 2b. GM Change History
Each domain has a collapsible `GM changes (N) — last settled GM: ...` list under its heading showing the 20 most recent Grandmaster changes (time, old GM → new GM). A change is recorded when the domain's online Leaders settle on a single verified GM that differs from the previously settled one, so a handover to a device that was never a GM before is covered, and a GM plus the Boundary Clocks below it (all reporting the same GM) produce one entry. Nothing is recorded while multiple GMs conflict. Detection runs once per second: a change appears within about a second when the old GM is demoted (e.g. starts sending Delay_Req), but if the old GM simply goes silent it takes the offline timeout (`OfflineTimeoutSeconds`, default 10 s) plus up to a second. The displayed GM is the *last settled* one and is kept while the domain has no Leader or the GMs conflict. At most 64 domains are tracked: a new domain only replaces one that is not currently active (if all 64 are active, the new one is skipped). The open/closed state of the list is kept across the dashboard's automatic refresh. Cleared by `↻ Network Clear`. A GM verification is dropped when the device goes offline, so a device that comes back sending only Sync stays out of the conflict check until it announces its GM again.

### 3. GM Mismatch Alert (Diagnostic)
If a Follower receives a Grandmaster ID different from what its parent (Leader/BC) expects, a red alert (`⚠️ GM Mismatch!`) is displayed on the WebUI to notify users of topology issues. This check only runs once the parent link has been confirmed by a matching `Delay_Resp` (dashed border = unconfirmed, GM not yet verifiable); an unconfirmed Follower's inherited GM is labeled `(inherited, unverified)` instead.

### 4. Boundary Clock (BC) Identification
Automatically identifies v1/v2 translation, bridge operations across multiple domains within the same protocol, and relay status via `via Upstream` notation.

### 5. Precision GM Identification
Correctly identifies true GMs by extracting `grandmasterClockUuid` from PTPv2 Announce packets as well as PTPv1 (e.g., Dante) Sync packets.

### 6. BMC (Best Master Clock) Information Display
For PTPv2 Leader nodes, the BMC algorithm decision factors (`Priority1` / `ClockClass` / `Priority2`) taken from Announce packets are displayed as `BMC: P1=... / Class=... / P2=...`, which helps verify GM election priorities.

### 7. Delay Mechanism Detection (E2E / P2P)
For PTPv2 each node shows the delay mechanism it is currently using in a `Delay:` line:
- **E2E (Delay_Req/Resp)**: seen from `Delay_Req` or `Delay_Resp` packets.
- **P2P (peer delay)**: seen from `Pdelay_Req` / `Pdelay_Resp` / `Pdelay_Resp_Follow_Up` packets. These use their own multicast group (`224.0.0.107`), which the tool now joins in addition to the primary group (`224.0.1.129`). If joining fails, a `[WARN]` is logged and E2E monitoring continues unaffected.
- **E2E + P2P ⚠**: both were seen from the same device, which is abnormal.
A mechanism only counts while it was seen within the last `OfflineTimeoutSeconds`.

A node that only sends peer-delay traffic is labeled `P2P node` instead of `Unknown`; the **role is never inferred from Pdelay traffic** (a P2P Leader sends Pdelay_Req too). Such a role-less node stays online while its Pdelay traffic continues, whereas a Leader/Follower is still kept alive only by role-defining packets (Sync/Announce/Delay_Req/Delay_Resp).

**Mixed-mechanism warning**: if devices using E2E and devices using P2P are found together in the same PTPv2 domain for the persistence time (the same 10 s as a Conflict), the affected nodes get a yellow `DELAY MIX` badge, the alert banner lists the domain, and `[DELAY_MECH_MISMATCH]` is logged once per episode (with the number of devices on each side). This is a **warning, not a fault**: it usually indicates a profile/configuration mismatch, but a domain can legitimately contain links that differ as long as both ends of each link agree. PTPv1 (IEEE 1588-2002) has no peer-delay mechanism and is not evaluated.

> [!NOTE]
> P2P detection requires the peer-delay multicast group to reach this PC (IGMP membership on the switch port). It has been verified only against hand-built packets, not on a live network.

### 8. Announce Clock Quality and Time Properties
PTPv2 Leader nodes that have sent an Announce show two more lines under `BMC:`:
- `Clock: Accuracy ... / Variance 0x.... / Source ... / Steps removed N` — `clockAccuracy`, `offsetScaledLogVariance`, `timeSource` and `stepsRemoved` (how many hops the Announce is from the Grandmaster).
- `Time: UTC offset ... s (valid | NOT valid) / PTP timescale | ARB timescale / time-traceable ✓✗ / freq-traceable ✓✗ [/ leap61] [/ leap59]` — `currentUtcOffset` (signed seconds) and the time-property flags of the Announce.

Notes:
- Raw values are always shown. Accuracy and time-source names are only shown for values confirmed against Wireshark's PTP dissector tables; any other value is shown as `0xNN (unrecognized)`. **`clockClass` is shown only as a number** (it appears on the `BMC:` line): its meaning depends on the profile and is not interpreted here.
- A `✗` means "not set", not a fault; for example a non-PTP timescale is normal in some profiles.
- Values are those of the **last Announce** from the node; a field is `N/A` until it has been received (`stepsRemoved` needs an Announce of at least 63 bytes, `timeSource` the full 64 bytes). Hovering a line shows what it contains.
- This is display only: no alert is raised from these values, and no "why this GM was chosen" ranking is shown, because the comparison order of the Best Master Clock algorithm differs between profiles (e.g. default vs. ITU-T G.8275).

### 9. Announce Timeout and Announce-Interval Mismatch
Both are **warnings** (yellow badge, alert-banner entry, one log line per episode, `ANNOUNCE` / `INTERVAL` log filters).
- **Announce timeout** (`[ANNOUNCE_TIMEOUT]`, badge `NO ANNOUNCE`): a PTPv2 Leader that is still active (it keeps sending Sync etc., so the offline timeout does not trigger) but has sent no Announce for `AnnounceReceiptTimeout` × its own Announce interval (`2^logMessageInterval` seconds, taken from its Announce). This is an early sign of a Grandmaster change. The limit never goes below 1 s (so sub-second intervals do not trip on network jitter), a Leader whose Announce carries no interval (0x7F) is never judged, and a Leader that goes fully offline is reported as offline, not as timed out. It clears when an Announce arrives again.
- **Announce-interval mismatch** (`[INTERVAL_MISMATCH]`, badge `INTERVAL`): Leaders of the same PTPv2 domain announce at different intervals for the persistence time (the same 10 s as a Conflict). The log lists the intervals found. This follows linuxptp's guidance that the Announce interval "should be the same in the whole domain"; it is guidance, not a hard rule (a Boundary Clock may differ by design), hence only a warning. **Sync intervals are not compared**, because no source states that they must match.

> [!NOTE]
> With a long Announce interval (e.g. 16 s) the limit is 3 × 16 = 48 s. If the Leader stops all role-defining traffic (Sync, Announce, ...) it is shown as offline after the offline timeout, long before that; if it keeps sending Sync, it is reported once 48 s pass without an Announce.

## WebUI Operations
- **Uptime / Offline**: Real-time display of uptime for active devices or elapsed time since disconnection for offline devices. Uptime resets (and a `[REJOIN]` log entry is added) when a device comes back online after being offline.
- **Search / Filter**: The search box at the top filters visible nodes by IP, MAC, or vendor as you type.
- **Alert Banner**: A banner above the topology view lists all active Conflict/GM Mismatch alerts; clicking an entry scrolls to and highlights the corresponding node.
- **Log Level Filters**: The Event Logs panel can be filtered to `ALL` / `ERROR` / `WARN` / `CONFLICT` / `MISMATCH` (GM mismatch) / `DELAY` (E2E/P2P mixing) / `ANNOUNCE` (Announce timeout) / `INTERVAL` (Announce-interval mismatch) entries only.
- `🗑 Clear Offline`: Batch delete all offline devices.
- `↻ Network Clear`: Reset all data (memory and logs) and restart monitoring.
- **Statistics Panel**: Counts and displays only currently ""online"" devices in the network.

## Logs and Maintenance
- **Log File Location**:
    - **Current Log**: Recorded in `ptp_monitor.log` in the project root.
    - **Archived Logs**: Automatically stored in the `logs/` folder with timestamps (rotation).
- **Auto-Rotation**: Performed on startup or when the log file size exceeds 10MB.

## Advanced Configuration (config.ini)
- **OfflineRetentionHours**: Duration to retain offline devices in the list (default: 24h).
- **ExpectedDelayInterval**: Expected interval for Follower `Delay_Req` packets (seconds). Used as a baseline for alerting.
- **DelayAlertThresholdRate**: Tolerance multiplier for alerting (default 1.5). UI highlights red if actual interval > (Expected * Rate).
- **OfflineTimeoutSeconds**: Seconds of inactivity before a node is marked as offline (default 10.0s).
- **AnnounceReceiptTimeout**: Number of missed Announce intervals before an otherwise active Leader is reported as not announcing (default 3, range 2–255; same meaning and default as linuxptp's `announceReceiptTimeout`). Out-of-range values are ignored.
- **OUI Vendor Mapping**: Vendor name definitions based on MAC address prefix (OUI).
- **Specific Device Mapping**: Manually assign names or hostnames to specific MAC addresses.

> [!NOTE]
> If a numeric setting contains an invalid value (e.g., a non-numeric string), that entry is ignored and the default value is kept (a `[WARN]` message is printed to the console on startup). Always use a period (`.`) as the decimal separator regardless of your OS locale.
