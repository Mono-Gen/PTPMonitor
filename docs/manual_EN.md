# PTPMonitor User Manual (v1.17.0)

PTPMonitor is a specialized protocol analyzer designed to capture PTP (Precision Time Protocol) v1 / v2 traffic, visualize device status, Boundary Clock (BC) operations, and network topology for high-precision diagnostic purposes.

## Requirements
- Windows OS (.NET Framework 4.0 or later)
- Administrator privileges (required for packet capture)

## Setup and Startup
1. Run `PTPMonitor.exe` as Administrator.
2. Select the network adapter index for monitoring.
3. Specify the Web UI port (default: 8080).
4. Open `http://localhost:8080/` in your browser.

### Unattended start (no prompts)
For a scheduled task or any start with nobody at the keyboard, give the interface up front. Nothing is asked then, and a wrong setting stops the program with an error instead of a silent default:

```
PTPMonitor.exe --nic 192.168.1.10 [--port 8080]   # --nic: exact IPv4 address, the number from the list, or text matching ONE adapter description
PTPMonitor.exe --no-prompt                        # never prompt; the interface must come from --nic or config.ini (Nic)
```
`Nic`, `WebPort` and `WebBind` in `config.ini` do the same; the command line wins. Without `--nic`/`Nic` the program still asks, but if no console input exists at all (end of input) it exits with an error instead of silently choosing interface 1. Exit codes: `0` ok, `1` runtime failure (no interface, dashboard could not start, capture unreadable), `2` bad arguments or settings. `--version` and `--help` are available.

`config.ini` is looked up in the working directory first, then next to the exe (so a task whose working directory is elsewhere still finds it). The log files are always written next to the exe. Packet capture still needs administrator rights; a Task Scheduler entry "run with highest privileges" is the expected setup (this has not been tested here).

### Read-only remote view (`--bind`)
`--bind <IP>` (or `WebBind`) makes the dashboard **also** listen on one extra local address, e.g. `--bind 192.168.1.10`, so other machines can watch it. Because that dashboard has **no authentication**, every instance started with `--bind` is **read-only**: the clear buttons are hidden and the clear endpoints answer 403, even for local requests. Use it only on a trusted network. Wildcards (`0.0.0.0`, `::`, `*`) and host names are refused. Listening on a non-loopback address may need administrator rights or a URL reservation (`netsh http add urlacl`); if it cannot start, the program says so and exits with `1` (the exact requirement was not verified).

### Prometheus metrics (`/metrics`)
`GET /metrics` returns the current state in the Prometheus text format (0.0.4): device counts (online/offline), Leader instances per PTP version, active conflicts / GM mismatches / Announce timeouts / delay-mechanism mixes / Announce-interval mismatches, the last agreed Grandmaster per domain (`ptpmonitor_domain_grandmaster_info`), packet counters, uptime and version. Only a small fixed set of series is produced (no per-device series). Point a scraper at `http://<host>:<port>/metrics` (with `--bind` for remote scraping).

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

### 10. Profile Hint on Domain Headings
Each domain heading may carry a small `hint:` showing which PTP profile the domain **number** matches. It is a hint, not a detection result: the domain number is set by the user and several profiles share defaults (0 in particular), so nothing is concluded and no alert is raised.
- PTPv2 domain `127`: SMPTE ST 2059-2 default domain
- PTPv2 domain `0`: AES67 default domain (also used by other profiles)
- PTPv2 domain `24`–`43` / `44`–`63`: within the ITU-T G.8275.1 / G.8275.2 range
- PTPv1: noted as Dante's default clocking (subdomain names are not interpreted)
- Any other number: no hint.

The values come from vendor and standards-body documentation, not from the standards' full text. The IEEE 1588 default profile's default domain and Dante's PTPv1 subdomain names could not be confirmed, so they are deliberately not shown.

### 11. Offline Analysis of Captures (pcap / pcapng)
A capture taken with Wireshark/tcpdump can be analyzed later with the same parser and the same diagnoses as live monitoring (no administrator rights, no network access):

```
PTPMonitor.exe --pcap capture.pcapng [--port 8080]    # analyze, then open the dashboard
PTPMonitor.exe --pcap capture.pcap --summary          # no dashboard: print events and a device table, then exit
```
Exit codes: `0` success (a truncated or partly damaged file still gives `0` with a warning), `1` the capture could not be read, `2` bad arguments.

- **Formats**: classic pcap (microsecond or nanosecond, either byte order) and pcapng (any byte order, several sections, `if_tsresol` / `if_tsoffset`). Compressed files (`.gz`) must be decompressed first.
- **What is analyzed**: Ethernet (also with one or two VLAN tags), raw IP and raw IPv4 captures, PTP over UDP/IPv4 on ports 319/320. Everything else is **counted by reason and reported, never guessed**: IPv6, Layer-2 PTP (EtherType 0x88F7), IPv4 fragments, non-UDP, other ports, UDP payloads over 2048 bytes (the live receiver drops them too), UDP datagrams the capture recorded only partly (snap length; the live receiver would have seen them whole), UDP lengths that do not fit their IPv4 packet, and link types other than the above (e.g. Linux cooked capture, whose header layout was not verified).
- **Time**: all timers run on the capture's own timestamps, not the real clock. Log lines carry the capture time, an idle gap (e.g. hours) is fast-forwarded without losing the events inside it (timers that fire there, such as offline or Announce timeout, and retention removals are logged at their true capture time), and out-of-order timestamps never move the clock backwards. A capture without timestamps (pcapng *Simple Packet Blocks* only) is still read, but timer-based diagnoses (offline, persistence, Announce timeout) cannot be evaluated and a warning says so.
- **What the dashboard shows**: the state at the **end of the capture**, frozen. A problem that came and went during the capture (for example a conflict that had resolved) is no longer in the node state; **look in the event log** (or the `--summary` output) for it. A banner shows the file, the time range and the skipped-packet counts; the clear buttons are disabled.
- **Safety**: the file is treated as untrusted input and opened read-only. Oversized records or blocks are refused rather than allocated, a damaged file yields what could be read plus a warning, and no log file is written.

> [!NOTE]
> This has been verified against hand-built capture files only; please check it with your own captures. Every packet in a capture goes through exactly the same processing as a live packet, so any difference from what you saw live is worth reporting.

### 12. Event Timeline and Export
The **Timeline** panel (above Event Logs) is a structured history of what started, changed and **ended**, newest first. The plain text event log is unchanged; the Timeline is a separate record that also remembers when a problem was *resolved*, so you can see how long it lasted.

| Group | Events |
|---|---|
| Alerts | `CONFLICT_START` / `CONFLICT_END`, `GM_MISMATCH_START` / `GM_MISMATCH_END` |
| Roles & GM | `ROLE_CHANGE`, `GM_CHANGE` |
| Presence | `JOIN`, `REJOIN`, `OFFLINE`, `LEAVE`, `SYSTEM` (the Clear buttons) |
| Timing | `DELAY_MIX_START/END`, `ANNOUNCE_TIMEOUT_START/END`, `INTERVAL_MISMATCH_START/END` |

- **One event per episode**: a Conflict, a mixed delay mechanism or differing Announce intervals produce one `START` for the domain however many devices are involved (the text log still prints one line per Leader), and one `END` when it is over, with its duration counted from the `START` (`... after 24s`). A problem that comes back is a new episode. An episode also ends when the devices involved go offline or are removed from the list; a GM mismatch and an Announce timeout say why in the message, and an interval mismatch says whether the intervals agree again or there are no longer two Leaders to compare.
- **Severity** is shown with an icon and a word (`⛔ ALERT`, `⚠ WARN`, `ℹ INFO`, `✔ RESOLVED`), not by colour alone.
- **Filter** by group or search IP / domain / text. **Click a row** to jump to the node it is about (domain-level events jump to the first node of that domain).
- **Export**: `⬇ JSON` and `⬇ CSV` download the whole retained history (up to 100,000 events per request). The CSV has a UTF-8 BOM, and a field that could be read as a spreadsheet formula is neutralized with a leading `'`. Times are ISO 8601 with the UTC offset, e.g. `2023-11-15T07:13:36+09:00`.
- **Limits**: the last 5,000 events are kept in memory (50,000 when analyzing a capture). When older events had to be discarded, the panel says how many; nothing is dropped silently. The history is **not saved to disk** and is lost when the program stops; `↻ Network Clear` empties it (the Clear itself is recorded as a `SYSTEM` event).
- **Capture analysis**: events carry the capture's own time. The Clear buttons are disabled, so the history of the capture stays.

API: `GET /api/events?since=<seq>&type=A,B&ip=<ip>&domain=<d>&limit=<n>&format=json|csv`. `since` returns only newer events (this is how the page polls), `limit` is 1–100000 (default 1000), and a malformed parameter is answered with `400`. The JSON carries `session` (changes when the program restarts, so the page re-syncs), `epoch` (changes on Network Clear), `firstSeq`, `lastSeq`, `total`, `dropped` and `capacity`.

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
    - **Current Log**: Recorded in `ptp_monitor.log` next to the exe.
    - **Archived Logs**: Automatically stored in the `logs/` folder next to the exe with timestamps (rotation).
- **Auto-Rotation**: Performed on startup or when the log file size exceeds 10MB.

## Advanced Configuration (config.ini)
- **OfflineRetentionHours**: Duration to retain offline devices in the list (default: 24h).
- **ExpectedDelayInterval**: Expected interval for Follower `Delay_Req` packets (seconds). Used as a baseline for alerting.
- **DelayAlertThresholdRate**: Tolerance multiplier for alerting (default 1.5). UI highlights red if actual interval > (Expected * Rate).
- **OfflineTimeoutSeconds**: Seconds of inactivity before a node is marked as offline (default 10.0s).
- **AnnounceReceiptTimeout**: Number of missed Announce intervals before an otherwise active Leader is reported as not announcing (default 3, range 2–255; same meaning and default as linuxptp's `announceReceiptTimeout`). Out-of-range values are ignored.
- **Nic** / **WebPort** / **WebBind**: Same as `--nic` / `--port` / `--bind` (see "Unattended start"). An invalid value is an error (exit code 2), not ignored.
- **OUI Vendor Mapping**: Vendor name definitions based on MAC address prefix (OUI).
- **Specific Device Mapping**: Manually assign names or hostnames to specific MAC addresses.

> [!NOTE]
> If a threshold setting (the numeric ones above, not Nic/WebPort/WebBind) contains an invalid value (e.g., a non-numeric string), that entry is ignored and the default value is kept (a `[WARN]` message is printed to the console on startup). Always use a period (`.`) as the decimal separator regardless of your OS locale.
