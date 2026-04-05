# PTPMonitor User Manual (v1.6.3)

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
When multiple Leaders exist in the same domain, the tool determines the severity based on duration:
- **BMCA (Negotiating) [Yellow]**: Multiple leaders detected for **less than 10 seconds**. This is shown as a normal negotiation process during master switching.
- **CONFLICT (Persistent) [Red]**: Conflict persisting for **10 seconds or more**. Recorded as a configuration error or dual-master fault in the logs (`[CONFLICT_ALERT]`).

### 3. GM Mismatch Alert (Diagnostic)
If a Follower receives a Grandmaster ID different from what its parent (Leader/BC) expects, a red alert (`⚠️ GM Mismatch!`) is displayed on the WebUI to notify users of topology issues.

### 4. Boundary Clock (BC) Identification
Automatically identifies v1/v2 translation, bridge operations across multiple domains within the same protocol, and relay status via `via Upstream` notation.

### 5. Precision GM Identification
Correctly identifies true GMs by extracting `grandmasterClockId` from PTPv2 Announce packets as well as PTPv1 (e.g., Dante) Sync packets.

## WebUI Operations
- **Uptime / Offline**: Real-time display of uptime for active devices or elapsed time since disconnection for offline devices.
- `🗑 Clear Offline`: Batch delete all offline devices.
- `↻ Network Clear`: Reset all data (memory and logs) and restart monitoring.
- **Statistics Panel**: Counts and displays only currently ""online"" devices in the network.

## Logs and Maintenance
- **Log File Location**:
    - **Current Log**: Recorded in `ptp_monitor.log` in the project root.
    - **Archived Logs**: Automatically stored in the `logs/` folder with timestamps (rotation).
- **Auto-Rotation**: Performed on startup or when the log file size exceeds 10MB.

## Advanced Configuration (config.ini)
- **OfflineRetentionHours**: Duration to retain offline devices (default: 24h).
- **OUI Vendor Mapping**: Vendor name definitions based on MAC address prefix (OUI).
- **Specific Device Mapping**: Manually assign names or hostnames to specific MAC addresses.
