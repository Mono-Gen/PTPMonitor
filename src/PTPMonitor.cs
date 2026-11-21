using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using System.Globalization;

/// <summary>
/// Holds the state for one (PTP version, domain) instance observed from a device.
/// A single device may own several instances at once (e.g. a Boundary Clock that is
/// Leader on one domain and Follower on another, using the same PTP version).
/// </summary>
class ProtocolState {
    public string Version { get; set; }
    public string Domain { get; set; }
    public string Role { get; set; }
    public string OwnId { get; set; }
    public string GrandmasterId { get; set; }
    // true only when GrandmasterId was actually read from the wire (v2 Announce / v1 Sync body).
    // A Leader's GrandmasterId is otherwise provisionally set to its own ID for display; that guess
    // must not take part in conflict detection, since a Boundary Clock's own ID is not "a GM".
    public bool GmVerified { get; set; }
    public int? SyncLog { get; set; }
    public int? AnnounceLog { get; set; }

    public long? LastDelayReqSeenTicks { get; set; }
    public double? LastMeasuredIntervalMs { get; set; }

    public int? GmPriority1 { get; set; }
    public int? GmPriority2 { get; set; }
    public int? GmClass { get; set; }
    // Remaining Announce fields (PTPv2). Values are as of the last Announce; null = not (yet) read.
    // GmAccuracy/GmVariance describe the GM's clockQuality; StepsRemoved/TimeSource sit at the end of the
    // 64-byte Announce and are only read when the packet is long enough to contain them.
    public int? GmAccuracy { get; set; }
    public int? GmVariance { get; set; }        // offsetScaledLogVariance (unsigned 16-bit)
    public int? StepsRemoved { get; set; }      // unsigned 16-bit
    public int? TimeSource { get; set; }
    public int? UtcOffset { get; set; }         // currentUtcOffset, SIGNED 16-bit seconds
    public int? TimeFlags { get; set; }         // flagField[1]: bit0 leap61, 1 leap59, 2 utcValid, 3 ptpTimescale, 4 timeTraceable, 5 freqTraceable

    // Freshness/online status for THIS instance specifically, so a stale Leader on one
    // domain/version can't linger just because another instance of the same device is alive.
    public long LastSeenTicks { get; set; }
    public bool IsOnline { get; set; }
    // Set only when a role-defining packet (Sync/Announce/Delay_Req/Delay_Resp) is seen, unlike
    // LastSeenTicks which also advances on Management/Signaling/Pdelay traffic that carries no
    // role information. Used to age out a stale Role instead of letting non-role traffic keep it
    // alive indefinitely (e.g. a Leader that stopped sending Sync/Announce but still emits Management).
    public long LastRoleSeenTicks { get; set; }
    // When an E2E marker (Delay_Req / Delay_Resp) and a P2P marker (Pdelay_Req / Pdelay_Resp /
    // Pdelay_Resp_Follow_Up) were last seen from this instance (PTPv2 only). The delay mechanism is
    // derived from their recency at read time (see Program.DelayMech), so an instance that stops
    // using a mechanism stops being reported as using it.
    public long? LastE2ESeenTicks { get; set; }
    public long? LastP2PSeenTicks { get; set; }
    // Set by MonitorLoop while this instance's (v2, domain) has persistently mixed E2E and P2P devices.
    public bool MechMismatch { get; set; }
    // When the last (well-formed) Announce arrived. Unlike LastRoleSeenTicks it is advanced ONLY by
    // Announce, so a Leader that keeps sending Sync but stopped Announcing is detectable.
    public long? LastAnnounceTicks { get; set; }
    public bool AnnounceStale { get; set; }          // set by MonitorLoop: no Announce for AnnounceReceiptTimeout intervals
    public bool AnnounceStaleLogged { get; set; }    // edge-trigger guard for [ANNOUNCE_TIMEOUT]
    public bool IntervalMismatch { get; set; }       // set by MonitorLoop: this domain's Leaders use different Announce intervals

    public bool IsConflict { get; set; }
    public long? RoleChangedAtTicks { get; set; }
    public long? ConflictStartedAtTicks { get; set; }
    // Edge-trigger guard so [GM_MISMATCH] is logged once per mismatch episode, not on every packet.
    public bool GmMismatchLogged { get; set; }

    public ProtocolState(string version, string domain) {
        Version = version;
        Domain = domain;
        Role = "Unknown";
        OwnId = null;
        GrandmasterId = null;
        IsConflict = false;
        IsOnline = true;
        GmMismatchLogged = false;
        LastRoleSeenTicks = Program.NowTicks();
    }
}

/// <summary>
/// Holds information for each device on the network.
/// Manages multiple (version, domain) protocol instances in a dictionary.
/// </summary>
class DeviceInfo {
    public string IP { get; set; }
    public string Mac { get; set; }
    public Dictionary<string, ProtocolState> Protocols { get; set; }
    public long LastSeenTicks { get; set; }
    public long FirstSeenTicks { get; set; }
    // Reset whenever the device transitions offline -> online, so "Uptime" reflects the
    // current session rather than accumulating across outages.
    public long OnlineSinceTicks { get; set; }
    public bool IsOnline { get; set; }
    public bool HasJoined { get; set; }

    public DeviceInfo(string ip) {
        IP = ip;
        Mac = "Unknown";
        Protocols = new Dictionary<string, ProtocolState>();
        long now = Program.NowTicks();
        LastSeenTicks = now;
        FirstSeenTicks = now;
        OnlineSinceTicks = now;
        IsOnline = true;
        HasJoined = false;
    }
}

/// <summary>
/// A Follower-to-Leader topology edge confirmed via a matched Delay_Resp. Only confirmed
/// edges are persisted; an unconfirmed "best guess" is computed on demand at read time so a
/// stale guess can never outlive the situation that produced it (see FindFallbackLeaderIp).
/// </summary>
class TopologyLink {
    public string Version { get; set; }
    public string Domain { get; set; }
    public string FollowerIp { get; set; }
    public string LeaderIp { get; set; }
    public long ConfirmedAtTicks { get; set; }
}

/// <summary>
/// One observed Grandmaster change within a (version, domain), kept for display in the dashboard.
/// </summary>
class GmChangeEvent {
    public string Time { get; set; }
    public string OldGm { get; set; }
    public string NewGm { get; set; }
}

/// <summary>
/// Per-(version, domain) record of the Grandmaster the domain's online Leaders last agreed on,
/// plus the recent history of changes of that agreed GM. Tracked per domain (not per reporting
/// device) so a handover to a device that was never a GM before is still recorded, and a GM plus
/// the Boundary Clocks below it (all reporting the same GM) collapse into one change.
/// </summary>
class DomainGmState {
    public string Gm { get; set; }
    public long LastSeenTicks { get; set; }
    public List<GmChangeEvent> Events { get; set; }
    public DomainGmState() { Events = new List<GmChangeEvent>(); }
}

/// <summary>
/// Main program class for PTPMonitor.
/// Controls packet capture, monitoring loops, and the web server.
/// </summary>
class Program {
    static readonly string McastGroup = "224.0.1.129";
    // IEEE 1588 Annex D: peer-delay messages (Pdelay_Req / Pdelay_Resp / Pdelay_Resp_Follow_Up) use their
    // own link-local group, NOT the primary group above, so P2P devices are invisible without joining it.
    static readonly string PdelayMcastGroup = "224.0.0.107";
    static readonly int[] Ports = new int[] { 319, 320 };
    static string WebPort = "8080";
    static double OfflineRetentionHours = 24.0;
    static double ExpectedDelayInterval = 2.0;    // Added in v1.6.8
    static double DelayAlertThresholdRate = 1.5;  // Added in v1.6.8
    static double OfflineTimeoutSeconds = 10.0;   // Added in v1.6.8
    // Number of missed Announce intervals after which a Leader that is otherwise still active is reported
    // as having stopped announcing. Same meaning/default/range as linuxptp's announceReceiptTimeout
    // (default 3, min 2, max 255).
    static double AnnounceReceiptTimeout = 3.0;
    // How long two simultaneous Leaders must persist in the same (version, domain) before a
    // [CONFLICT_ALERT] is logged. Not a mutable readonly const so tests can shrink it for speed.
    static double ConflictPersistThresholdSeconds = 10.0;
    static readonly int MaxDevices = 512;         // Cap to prevent unbounded memory growth from spoofed sources
    static readonly int MaxProtocolStatesPerDevice = 64; // Cap (version,domain) instances per device: a single spoofed IP could otherwise send arbitrary domain values to grow memory/scan cost unbounded
    static readonly int MaxRotatedLogFiles = 20;  // Cap rotated log generations to bound disk usage
    // Confirmed topology links are re-validated on the Leader's liveness (see MonitorLoop), but a
    // Leader that never goes offline could otherwise keep an unconfirmed-stale link alive forever;
    // this forces periodic re-confirmation via a fresh Delay_Resp. Not a readonly const so tests can
    // shrink it (see ConflictPersistThresholdSeconds for the same pattern).
    static double LinkStaleAfterSeconds = 300.0;
    static int MaxGmHistoryDomains = 64;            // Cap domains tracked for GM history: domain values come from packets and could be spoofed
    static int MaxGmHistoryPerDomain = 20;          // Recent GM changes kept per (version, domain). Not readonly so tests can shrink it.
    static bool gmHistoryLimitWarned = false;
    static bool deviceLimitWarned = false;
    static bool protocolStateLimitWarned = false;

    // Composite dictionary keys are built as "version" + KeySep + "domain" [+ KeySep + "followerIp"].
    // A control character is used as the separator (rather than e.g. '|') because a v1 "domain" is
    // parsed directly from packet bytes and could otherwise collide with a printable separator.
    const string KeySep = "";

    static Dictionary<string, DeviceInfo> devices = new Dictionary<string, DeviceInfo>();
    static Dictionary<string, TopologyLink> topologyLinks = new Dictionary<string, TopologyLink>();
    // Guarded by lock(devices), like devices/topologyLinks.
    static Dictionary<string, DomainGmState> domainGms = new Dictionary<string, DomainGmState>();
    // (v2, domain) -> when mixed E2E/P2P was first seen in it. Entries exist only while the domain is
    // currently mixed (removed as soon as it is not), so this cannot grow beyond the active domains.
    // mechMismatchLogged is the edge-trigger guard for [DELAY_MECH_MISMATCH]. Both guarded by lock(devices).
    static Dictionary<string, long> mechMismatchSince = new Dictionary<string, long>();
    static HashSet<string> mechMismatchLogged = new HashSet<string>();
    // Same pattern for differing Announce intervals within a (v2, domain). Guarded by lock(devices).
    static Dictionary<string, long> intervalMismatchSince = new Dictionary<string, long>();
    static HashSet<string> intervalMismatchLogged = new HashSet<string>();
    static CancellationTokenSource cts = new CancellationTokenSource();
    static StreamWriter logWriter = null;
    static string currentLogPath = "ptp_monitor.log";
    static object printLock = new object();
    static List<string> logs = new List<string>();
    static object logLock = new object();
    static SemaphoreSlim httpConcurrency = new SemaphoreSlim(64, 64);

    static Dictionary<string, string> customVendors = new Dictionary<string, string>();
    static IPAddress currentLocalIp = IPAddress.Any;
    static HttpListener httpListener = new HttpListener();

    /// <summary>
    /// Monotonic timestamp (not affected by NTP corrections or manual clock changes) for
    /// duration/elapsed-time math. Absolute wall-clock display (log timestamps) still uses
    /// DateTime.Now separately.
    /// </summary>
    public static long NowTicks() { return Stopwatch.GetTimestamp(); }

    public static double ElapsedSeconds(long fromTicks) {
        return (double)(Stopwatch.GetTimestamp() - fromTicks) / Stopwatch.Frequency;
    }

    static string StateKey(string version, string domain) { return version + KeySep + domain; }
    static string LinkKey(string version, string domain, string followerIp) { return version + KeySep + domain + KeySep + followerIp; }

    /// <summary>
    /// Decodes a PTPv1 subdomain (raw bytes, not guaranteed to be valid UTF-8/text). A naive
    /// Encoding.UTF8.GetString replaces any invalid byte sequence with U+FFFD, so two different
    /// invalid inputs can decode to the identical string and collide as the same domain key. If the
    /// decoded text contains the replacement character, fall back to a hex dump of the raw bytes
    /// (distinct byte sequences stay distinct); valid text keeps its normal readable form.
    /// </summary>
    static string SafeDecodeDomain(byte[] data, int offset, int len) {
        string text = Encoding.UTF8.GetString(data, offset, len).TrimEnd('\0', ' ');
        if (text.IndexOf('�') >= 0) return "raw:" + BitConverter.ToString(data, offset, len).Replace("-", "");
        return text;
    }

    /// <summary>
    /// Entry point. Handles interface selection and starts monitoring tasks.
    /// </summary>
    static void Main(string[] args) {
        LoadConfig();
        Console.WriteLine("=== PTPMonitor v1.12.0 ===");

        NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
        List<IPAddress> validIps = new List<IPAddress>();

        Console.WriteLine("\n[Available IPv4 Interfaces]");
        int index = 1;
        foreach (var nic in nics) {
            if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) {
                foreach (var ip in nic.GetIPProperties().UnicastAddresses) {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork) {
                        Console.WriteLine(string.Format("  {0}: {1} - {2}", index, ip.Address, nic.Description));
                        validIps.Add(ip.Address);
                        index++;
                    }
                }
            }
        }

        if (validIps.Count == 0) { Console.WriteLine("Error: No valid IPv4 interfaces found."); return; }
        Console.Write(string.Format("\nSelect NIC Number (1-{0}) [Default: 1]: ", validIps.Count));
        string input = Console.ReadLine();
        int selIndex = 1;
        if (!string.IsNullOrEmpty(input)) int.TryParse(input, out selIndex);
        if (selIndex < 1 || selIndex > validIps.Count) selIndex = 1;
        currentLocalIp = validIps[selIndex-1];

        Console.Write("Enter Web UI Port [Default: 8080]: ");
        string pIn = Console.ReadLine();
        if (!string.IsNullOrEmpty(pIn)) {
            int parsedPort;
            if (int.TryParse(pIn, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort) && parsedPort >= 1 && parsedPort <= 65535) {
                WebPort = parsedPort.ToString(CultureInfo.InvariantCulture);
            } else {
                Console.WriteLine(string.Format("[WARN] Invalid port '{0}', keeping default {1}.", pIn, WebPort));
            }
        }

        RotateLogFile();
        Task.Factory.StartNew(WebServerLoop, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task.Factory.StartNew(MonitorLoop, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var sockets = new List<Socket>();
        foreach (int port in Ports) {
            Socket sock = null;
            try {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1024 * 1024);
                sock.Bind(new IPEndPoint(currentLocalIp, port));
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse(McastGroup), currentLocalIp));
            } catch (SocketException ex) {
                Log(string.Format("[ERROR] Cannot listen on UDP port {0} ({1}). PTP traffic on this port will not be monitored.", port, ex.Message));
                if (sock != null) { try { sock.Close(); } catch (Exception) { /* Best-effort cleanup */ } }
                continue;
            }
            // The peer-delay group is optional: if joining it fails, E2E/primary-group monitoring must
            // keep working, so this is a warning and the socket stays in use.
            try {
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse(PdelayMcastGroup), currentLocalIp));
            } catch (SocketException ex) {
                Log(string.Format("[WARN] Cannot join peer-delay group {0} on UDP port {1} ({2}). P2P (peer delay) devices will not be detected.", PdelayMcastGroup, port, ex.Message));
            }
            sockets.Add(sock);
            Task.Factory.StartNew(() => {
                byte[] buffer = new byte[2048];
                int consecutiveErrors = 0;
                while (!cts.Token.IsCancellationRequested) {
                    try {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int received = sock.ReceiveFrom(buffer, ref ep);
                        if (received > 0) ParsePacket(((IPEndPoint)ep).Address.ToString(), buffer, received, port);
                        consecutiveErrors = 0;
                    } catch (SocketException ex) {
                        // Blocking receive is aborted by sock.Close() on shutdown. ConnectionReset (ICMP
                        // port-unreachable) and MessageSize (a datagram larger than our buffer, whose
                        // excess is simply discarded by the OS) are expected/transient and never impair
                        // the socket. Anything else (e.g. the NIC going down) counts toward a threshold
                        // so a truly persistent failure stops the loop instead of busy-looping forever.
                        if (cts.Token.IsCancellationRequested) break;
                        if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) continue;
                        consecutiveErrors++;
                        if (consecutiveErrors >= 5) {
                            Log(string.Format("[ERROR] UDP port {0} receive loop stopped after repeated errors: {1}", port, ex.Message));
                            break;
                        }
                        Thread.Sleep(200);
                    } catch (ObjectDisposedException) {
                        break; // Socket closed during shutdown
                    } catch (Exception ex) {
                        Log("[ERROR] Packet processing failed: " + ex.Message);
                    }
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        if (sockets.Count == 0) {
            Log("[ERROR] No PTP ports could be opened. Exiting.");
            return;
        }

        Console.WriteLine("\nStarting monitoring on {0}...", currentLocalIp);
        Console.WriteLine("[INFO] Web Server -> http://localhost:{0}/\n", WebPort);
        Console.WriteLine("Press Ctrl+C to stop.");

        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[Quitting] Cleaning up and performing IGMP Leave...");
            foreach(var sock in sockets) {
                // Each Drop is attempted independently: the peer-delay join may have failed (or not
                // happened), and that must not stop the primary-group Leave or the socket close.
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(IPAddress.Parse(PdelayMcastGroup), currentLocalIp)); } catch (Exception) { /* Best-effort cleanup during shutdown */ }
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(IPAddress.Parse(McastGroup), currentLocalIp)); } catch (Exception) { /* Best-effort cleanup during shutdown */ }
                try { sock.Close(); } catch (Exception) { /* Best-effort cleanup during shutdown */ }
            }
            try { if (httpListener.IsListening) httpListener.Stop(); httpListener.Close(); } catch (Exception) { /* Best-effort cleanup during shutdown */ }
            lock(printLock) {
                if(logWriter != null) { logWriter.Close(); logWriter = null; }
            }
            Environment.Exit(0);
        };

        while (!cts.Token.IsCancellationRequested) Thread.Sleep(100);
    }

    /// <summary>
    /// Loads settings (retention, thresholds, OUI maps, etc.) from config.ini.
    /// </summary>
    static void LoadConfig() {
        try {
            string path = "config.ini";
            if (!File.Exists(path)) {
                string[] defaults = {
                    "[Settings]",
                    "OfflineRetentionHours = 24",
                    "",
                    "# OUI Vendor Mapping (OUI=VendorName)",
                    "00:1D:C1=Audinate (Dante)", "00:01:E1=Yamaha", "00:A0:DE=Yamaha", "EC:22:80=Yamaha", "00:07:CF=Yamaha", "00:0E:DD=Shure", "00:14:96=Shure"
                };
                File.WriteAllLines(path, defaults);
            }
            lock(customVendors) {
                customVendors.Clear();
                foreach (var line in File.ReadAllLines(path)) {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) continue;
                    var parts = line.Split(new char[] { '=' }, 2); // Limit to 2 so values may contain '='
                    if (parts.Length == 2) {
                        string key = parts[0].Trim();
                        string val = parts[1].Trim();
                        if (key.Equals("OfflineRetentionHours", StringComparison.OrdinalIgnoreCase)) {
                            ApplyConfigDouble(key, val, ref OfflineRetentionHours, 0.0, double.MaxValue);
                        } else if (key.Equals("ExpectedDelayInterval", StringComparison.OrdinalIgnoreCase)) {
                            ApplyConfigDouble(key, val, ref ExpectedDelayInterval, 0.001, double.MaxValue);
                        } else if (key.Equals("DelayAlertThresholdRate", StringComparison.OrdinalIgnoreCase)) {
                            ApplyConfigDouble(key, val, ref DelayAlertThresholdRate, 0.001, double.MaxValue);
                        } else if (key.Equals("OfflineTimeoutSeconds", StringComparison.OrdinalIgnoreCase)) {
                            ApplyConfigDouble(key, val, ref OfflineTimeoutSeconds, 0.001, double.MaxValue);
                        } else if (key.Equals("AnnounceReceiptTimeout", StringComparison.OrdinalIgnoreCase)) {
                            ApplyConfigDouble(key, val, ref AnnounceReceiptTimeout, 2.0, 255.0);
                        } else {
                            customVendors[key.ToUpper()] = val;
                        }
                    }
                }
            }
        } catch (Exception ex) {
            Console.WriteLine("[WARN] Failed to load config.ini, using default settings: " + ex.Message);
        }
    }

    /// <summary>
    /// Parses a config double invariantly; keeps the current (default) value when the input is
    /// invalid, non-finite (NaN/Infinity), or outside [min, max].
    /// </summary>
    static void ApplyConfigDouble(string key, string val, ref double target, double min, double max) {
        double parsed;
        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= min && parsed <= max) {
            target = parsed;
        } else {
            Console.WriteLine(string.Format("[WARN] config.ini: invalid value for {0} ('{1}'), keeping {2}", key, val, target.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Rotates log files on startup and when size exceeds the limit.
    /// </summary>
    static void RotateLogFile() {
        try {
            var oldWriter = logWriter;
            logWriter = null;
            if (oldWriter != null) oldWriter.Close();
            string logDir = "logs";
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            string old = currentLogPath;
            string rotated = Path.Combine(logDir, string.Format("ptp_monitor_{0}.log", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            bool moved = false;
            try { if(File.Exists(old)) { File.Move(old, rotated); moved = true; } } catch (Exception) { /* Fall through to truncate below */ }
            if (!moved && File.Exists(old)) { try { File.WriteAllText(old, ""); } catch (Exception) { /* Log file locked; appending continues */ } }
            logWriter = new StreamWriter(new FileStream(currentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            logWriter.WriteLine(string.Format("[{0:yyyy-MM-dd HH:mm:ss}] --- Log Session Started ---", DateTime.Now));
            PruneOldLogFiles(logDir);
        } catch (Exception ex) {
            // File logging is optional: never let rotation failures (permissions, disk full) kill monitoring.
            logWriter = null;
            Console.WriteLine("[WARN] Cannot prepare log file '" + currentLogPath + "': " + ex.Message + " (console/Web UI logging only)");
        }
    }

    /// <summary>
    /// Keeps at most MaxRotatedLogFiles rotated logs (oldest deleted first, ordered by the
    /// sortable timestamp embedded in the filename) so log rotation cannot fill the disk.
    /// </summary>
    static void PruneOldLogFiles(string logDir) {
        try {
            var files = Directory.GetFiles(logDir, "ptp_monitor_*.log");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - MaxRotatedLogFiles; i++) {
                try { File.Delete(files[i]); } catch (Exception) { /* Best-effort; a locked file is skipped */ }
            }
        } catch (Exception) { /* Best-effort cleanup; never fail startup over this */ }
    }

    /// <summary>
    /// Outputs message to console and log file. Keeps a short history in memory.
    /// </summary>
    static void Log(string message) {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string clean = string.Format("[{0}] {1}", timestamp, System.Text.RegularExpressions.Regex.Replace(message, @"\x1B\[[^m]*m", ""));
        lock(printLock) {
            Console.WriteLine(string.Format("[{0}] {1}", timestamp, message));
            if(logWriter != null) {
                // Swallowed intentionally: logging must never crash monitoring, and
                // reporting a log-write failure via Log() would recurse.
                try { logWriter.WriteLine(clean); if (new FileInfo(currentLogPath).Length > 10 * 1024 * 1024) RotateLogFile(); } catch (Exception) {}
            }
        }
        lock(logLock) { logs.Add(clean); if(logs.Count > 500) logs.RemoveAt(0); }
    }

    /// <summary>
    /// Safely retrieves vendor name from MAC address or OUI.
    /// </summary>
    static string GetVendorSafe(string mac) {
        if(string.IsNullOrEmpty(mac) || mac == "Unknown") return "-";
        string upperMac = mac.ToUpper();
        lock(customVendors) {
            if (customVendors.ContainsKey(upperMac)) return customVendors[upperMac];
            if (upperMac.Length >= 8) {
                string oui = upperMac.Substring(0, 8);
                if (customVendors.ContainsKey(oui)) return customVendors[oui];
            }
        }
        return "Unknown";
    }

    /// <summary>
    /// Removes a device's confirmed topology links, in either direction (as follower or as leader).
    /// Must be called while holding lock(devices).
    /// </summary>
    static void RemoveDeviceLinks(string ip) {
        var toRemove = topologyLinks.Where(kv => kv.Value.LeaderIp == ip || kv.Value.FollowerIp == ip).Select(kv => kv.Key).ToList();
        foreach (var k in toRemove) topologyLinks.Remove(k);
    }

    /// <summary>
    /// Feeds one (version, domain)'s current Grandmaster into the history. Called once per
    /// MonitorLoop sweep with singleGm = the one verified GM ID all of the domain's online Leaders
    /// agree on, or null when there is none or several (a conflict is in progress: neither is a
    /// "settled" GM, so nothing is recorded and the last settled GM is kept for comparison).
    /// A change is recorded when the settled GM differs from the previously settled one, regardless
    /// of which device reports it. When the table is full a new domain may only take the slot of one
    /// that was NOT refreshed in this same sweep (LastSeenTicks &lt; now), so spoofed/abandoned
    /// domains cannot hold slots forever, while domains that are active right now are never evicted:
    /// otherwise, with more active domains than slots, they would keep evicting each other and
    /// wipe each other's history every second. If every slot is active the newcomer is skipped.
    /// Callers must refresh existing domains before offering new ones within one sweep.
    /// Must hold lock(devices).
    /// </summary>
    static void TrackDomainGm(string version, string domain, string singleGm, long now) {
        string key = StateKey(version, domain);
        DomainGmState st;
        if (!domainGms.TryGetValue(key, out st)) {
            if (singleGm == null) return;
            if (domainGms.Count >= MaxGmHistoryDomains) {
                if (!gmHistoryLimitWarned) {
                    gmHistoryLimitWarned = true;
                    Log(string.Format("[WARN] GM-history domain limit ({0}) reached; inactive domains are evicted for new ones, and new domains are skipped while all tracked domains are active.", MaxGmHistoryDomains));
                }
                string oldestKey = null; long oldestTicks = long.MaxValue;
                foreach (var kv in domainGms) {
                    if (kv.Value.LastSeenTicks < now && kv.Value.LastSeenTicks < oldestTicks) { oldestTicks = kv.Value.LastSeenTicks; oldestKey = kv.Key; }
                }
                if (oldestKey == null) return; // all tracked domains are active this sweep
                domainGms.Remove(oldestKey);
            }
            domainGms[key] = new DomainGmState { Gm = singleGm, LastSeenTicks = now };
            return;
        }
        st.LastSeenTicks = now;
        if (singleGm == null || singleGm == st.Gm) return;
        st.Events.Add(new GmChangeEvent { Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), OldGm = st.Gm, NewGm = singleGm });
        if (st.Events.Count > MaxGmHistoryPerDomain) st.Events.RemoveAt(0);
        st.Gm = singleGm;
    }

    /// <summary>
    /// The timestamp that decides whether an instance is still alive: its last role-defining packet, or,
    /// for a role-less instance (only peer-delay traffic so far), its latest Pdelay packet, since such an
    /// instance never sends role-defining packets. A Leader/Follower is NOT kept alive by Pdelay traffic.
    /// </summary>
    static long LastLiveTicks(ProtocolState p) {
        long last = p.LastRoleSeenTicks;
        if (p.Role == "Unknown" && p.LastP2PSeenTicks.HasValue && p.LastP2PSeenTicks.Value > last) last = p.LastP2PSeenTicks.Value;
        return last;
    }

    /// <summary>
    /// The delay mechanism an instance is CURRENTLY using, from marker recency: "E2E", "P2P",
    /// "E2E+P2P" (both recently seen from one instance, itself abnormal) or "" (none recently seen).
    /// A marker older than OfflineTimeoutSeconds no longer counts.
    /// </summary>
    static string DelayMech(ProtocolState p) {
        bool e2e = p.LastE2ESeenTicks.HasValue && ElapsedSeconds(p.LastE2ESeenTicks.Value) < OfflineTimeoutSeconds;
        bool p2p = p.LastP2PSeenTicks.HasValue && ElapsedSeconds(p.LastP2PSeenTicks.Value) < OfflineTimeoutSeconds;
        if (e2e && p2p) return "E2E+P2P";
        return e2e ? "E2E" : (p2p ? "P2P" : "");
    }

    /// <summary>
    /// Finds an online Leader for (version, domain) to use as a display-only "best guess" parent
    /// when no Delay_Resp-confirmed link exists yet. Never cached: recomputed on every read so a
    /// bad guess (e.g. picked while two Leaders briefly coexisted) can't outlive the situation that
    /// produced it. Must be called while holding lock(devices).
    /// </summary>
    static string FindFallbackLeaderIp(string version, string domain, string excludeIp) {
        string key = StateKey(version, domain);
        foreach (var d in devices.Values) {
            if (d.IP == excludeIp) continue;
            ProtocolState ps;
            if (d.Protocols.TryGetValue(key, out ps) && ps.Role == "Leader" && ps.IsOnline) return d.IP;
        }
        return null;
    }

    /// <summary>
    /// Parses received PTP packets and updates device state. Supports PTPv1 and v2.
    /// </summary>
    static void ParsePacket(string ip, byte[] data, int len, int port) {
        if (len < 34) return;
        int versionField = data[1] & 0x0F;
        string protoVer = versionField == 2 ? "v2" : (versionField == 1 ? "v1" : "Unknown");
        if (protoVer == "Unknown") return;

        string domain = "0";
        string ownId = "";
        string role = "Unknown";
        byte msgType = 0;
        byte control = 0;
        bool seenE2E = false, seenP2P = false; // delay-mechanism markers (PTPv2 only; v1 has no P2P)

        if (protoVer == "v2") {
            msgType = (byte)(data[0] & 0x0F);
            domain = data[4].ToString();
            if (len >= 28) ownId = BitConverter.ToString(data, 20, 8).Replace("-","");
            // IEEE 1588-2008 Table 41: event messages (Sync=0, Delay_Req=1, Pdelay_Req=2,
            // Pdelay_Resp=3) go on UDP port 319; general messages (Follow_Up=8, Delay_Resp=9,
            // Pdelay_Resp_Follow_Up=10, Announce=11, Signaling=12, Management=13) go on port 320.
            // This is a fixed, undisputed part of the spec (unlike PTPv1 byte offsets) -- traffic
            // that violates it is spoofed or malformed and is dropped rather than trusted.
            bool isEventMessage = msgType <= 3;
            if (isEventMessage && port != 319) return;
            if (!isEventMessage && port != 320) return;
            // Announce/Delay_Resp are gated on the same length already required below to read their
            // GM/reqId fields (61/52 bytes): a packet too short to carry real Announce/Delay_Resp
            // content is not trusted to claim Leader role either, closing a cheap fake-Leader spoof.
            // Sync/Follow_Up have no independently-verified minimum in this file (see
            // ai-reviewer-wire-format-verification: PTP byte-level claims are not trusted without a
            // primary-source check), so they intentionally keep only the base 34-byte length check.
            // Delay-mechanism markers. E2E: Delay_Req(1) / Delay_Resp(9, same length gate as its Leader
            // role below). P2P: Pdelay_Req(2) / Pdelay_Resp(3) / Pdelay_Resp_Follow_Up(10); the port
            // checks above already confirmed they arrived on the spec's port. Pdelay messages keep only
            // the base length check (no independently verified minimum in this file); the effect of a
            // spoofed one is limited to a persistence-gated warning, never a role.
            if (msgType == 1 || (msgType == 9 && len >= 52)) seenE2E = true;
            else if (msgType == 2 || msgType == 3 || msgType == 10) seenP2P = true;
            if (msgType == 0 || msgType == 8) role = "Leader"; // Sync, Follow_Up
            else if (msgType == 11 && len >= 61) role = "Leader"; // Announce
            else if (msgType == 9 && len >= 52) role = "Leader"; // Delay_Resp
            else if (msgType == 1) role = "Follower"; // Delay_Req (Pdelay_Req excluded: P2P leaders send it too)
        } else {
            if (len < 40) return;
            // PTPv1 (IEEE 1588-2002): messageType (offset 20) only distinguishes Event(1, port 319)
            // from General(2, port 320). The actual message kind is in the control field (offset 32):
            // 0=Sync, 1=Delay_Req, 2=Follow_Up, 3=Delay_Resp, 4=Management.
            msgType = data[20];
            if (msgType == 1 && port != 319) return;
            if (msgType == 2 && port != 320) return;
            if (msgType != 1 && msgType != 2) return; // Neither Event nor General: not a valid v1 header
            control = data[32];
            // messageType (Event/General) and control (the actual message kind) must agree: Sync(0) and
            // Delay_Req(1) are Event messages, Follow_Up(2)/Delay_Resp(3)/Management(4) are General. A
            // packet claiming e.g. General(2)/port 320 while control says Delay_Req(1) is inconsistent
            // with the spec and is dropped rather than trusted (closes a port-check bypass).
            bool controlIsEvent = control == 0 || control == 1;
            if (msgType == 1 && !controlIsEvent) return;
            if (msgType == 2 && controlIsEvent) return;
            domain = SafeDecodeDomain(data, 4, 16);
            ownId = BitConverter.ToString(data, 22, 6).Replace("-","");
            // Sync/Delay_Resp are gated on the same length already required below to read their
            // syncInterval/GM/reqId fields (84/56 bytes): a packet too short to carry real Sync/
            // Delay_Resp content is not trusted to claim Leader role either. Follow_Up has no
            // independently-verified minimum in this file, so it keeps only the base 40-byte check
            // (see ai-reviewer-wire-format-verification memory: PTPv1 offsets are not guessed).
            if (control == 0 && len >= 84) role = "Leader"; // Sync
            else if (control == 3 && len >= 56) role = "Leader"; // Delay_Resp
            else if (control == 2) role = "Leader"; // Follow_Up
            else if (control == 1) role = "Follower"; // Delay_Req
        }

        long now = NowTicks();
        string stateKey = StateKey(protoVer, domain);

        lock(devices) {
            if (!devices.ContainsKey(ip)) {
                if (devices.Count >= MaxDevices) {
                    if (!deviceLimitWarned) {
                        deviceLimitWarned = true;
                        Log(string.Format("[WARN] Device limit ({0}) reached; least-recently-seen devices are evicted for new ones.", MaxDevices));
                    }
                    // Evict the least-recently-seen device instead of rejecting the new one outright:
                    // a device that keeps sending real traffic keeps refreshing LastSeenTicks and won't
                    // be evicted, so spoofed churn can't permanently lock the table against it, unlike a
                    // blanket "ignore new devices" policy which a burst of spoofed IPs could hold forever.
                    string oldestIp = null; long oldestTicks = long.MaxValue;
                    foreach (var d in devices.Values) {
                        if (d.LastSeenTicks < oldestTicks) { oldestTicks = d.LastSeenTicks; oldestIp = d.IP; }
                    }
                    if (oldestIp != null) { RemoveDeviceLinks(oldestIp); devices.Remove(oldestIp); }
                }
                devices[ip] = new DeviceInfo(ip);
            }
            var dev = devices[ip];
            dev.LastSeenTicks = now;
            if (!dev.IsOnline) {
                dev.IsOnline = true;
                dev.OnlineSinceTicks = now;
                Log(string.Format("[REJOIN] {0} ({1}) responded again after being offline.", GetVendorSafe(dev.Mac), ip));
            }
            if (dev.Mac == "Unknown" && ownId.Length >= 12) {
                // Try to derive MAC from OwnId (UI purpose)
                if (protoVer == "v2" && ownId.Length == 16) dev.Mac = ownId.Substring(0,2)+":"+ownId.Substring(2,2)+":"+ownId.Substring(4,2)+":"+ownId.Substring(10,2)+":"+ownId.Substring(12,2)+":"+ownId.Substring(14,2);
                else if (protoVer == "v1" && ownId.Length == 12) dev.Mac = ownId.Substring(0,2)+":"+ownId.Substring(2,2)+":"+ownId.Substring(4,2)+":"+ownId.Substring(6,2)+":"+ownId.Substring(8,2)+":"+ownId.Substring(10,2);
            }

            if (!dev.Protocols.ContainsKey(stateKey)) {
                if (dev.Protocols.Count >= MaxProtocolStatesPerDevice) {
                    if (!protocolStateLimitWarned) {
                        protocolStateLimitWarned = true;
                        Log(string.Format("[WARN] Per-device protocol-instance limit ({0}) reached for {1}; additional (version,domain) instances are ignored.", MaxProtocolStatesPerDevice, ip));
                    }
                    return;
                }
                dev.Protocols[stateKey] = new ProtocolState(protoVer, domain);
            }
            var pState = dev.Protocols[stateKey];
            string oldRole = pState.Role;
            // A different source clock identity behind the same IP (device swapped/re-IDed): whatever
            // GM was verified for the previous identity says nothing about this one.
            if (pState.OwnId != null && pState.OwnId != ownId) {
                pState.GmVerified = false;
                // Announce timing belongs to the previous identity: the new one must announce for itself
                // before it can be judged (applies even while the old identity still looked online).
                pState.LastAnnounceTicks = null; pState.AnnounceLog = null;
                pState.AnnounceStale = false; pState.AnnounceStaleLogged = false;
                // If the previous identity had already gone offline, the new one is a replacement device, not
                // the old one speaking again: it must not inherit the old Role / GM / mechanism markers (a
                // stale Leader role would otherwise also stop a role-less P2P replacement from being kept
                // online). While the old identity is still online the change is left alone, so a single IP
                // that fronts several clocks does not make its role flap on every alternating packet.
                // "Already offline" is judged by elapsed time too, not just the IsOnline flag: that flag is
                // only cleared by the once-per-second MonitorLoop sweep, so right after the timeout a
                // replacement packet can arrive before the sweep has run and would inherit the old values.
                if (!pState.IsOnline || ElapsedSeconds(LastLiveTicks(pState)) >= OfflineTimeoutSeconds) {
                    pState.Role = "Unknown"; oldRole = "Unknown";
                    pState.GrandmasterId = null;
                    pState.LastE2ESeenTicks = null; pState.LastP2PSeenTicks = null;
                    // Everything learned from the previous identity's Announces is void for the new one.
                    pState.GmPriority1 = null; pState.GmPriority2 = null; pState.GmClass = null;
                    pState.GmAccuracy = null; pState.GmVariance = null; pState.StepsRemoved = null;
                    pState.TimeSource = null; pState.UtcOffset = null; pState.TimeFlags = null;
                    pState.SyncLog = null; pState.AnnounceLog = null; pState.LastAnnounceTicks = null;
                    pState.AnnounceStale = false; pState.AnnounceStaleLogged = false;
                    pState.RoleChangedAtTicks = null;
                }
            }
            pState.OwnId = ownId;
            pState.LastSeenTicks = now;
            // IsOnline is only (re)asserted alongside a role-defining packet, not on every packet:
            // otherwise non-role traffic (Management/Signaling/Pdelay) would keep re-flipping IsOnline
            // back to true right after MonitorLoop ages it out via LastRoleSeenTicks, defeating that
            // staleness check entirely (a stale Role must actually go offline, not just "flicker").
            if (role != "Unknown") {
                // If this instance already outlived the offline timeout, its verified GM is stale even if
                // MonitorLoop has not run since (it only sweeps once per second): drop it BEFORE the
                // timestamp below is refreshed, or a returning Sync-only packet would keep it alive.
                if (ElapsedSeconds(pState.LastRoleSeenTicks) >= OfflineTimeoutSeconds) pState.GmVerified = false;
                pState.Role = role; pState.LastRoleSeenTicks = now; pState.IsOnline = true;
            }
            if (seenE2E) pState.LastE2ESeenTicks = now;
            if (seenP2P) {
                pState.LastP2PSeenTicks = now;
                // An instance with no role (so far only peer-delay traffic) has nothing else to prove it is
                // alive, so Pdelay traffic is its liveness. A Leader/Follower is NOT kept alive this way
                // (see above): its role must keep being refreshed by role-defining packets.
                if (pState.Role == "Unknown") pState.IsOnline = true;
            }

            if (protoVer == "v2") {
                sbyte logInt = unchecked((sbyte)data[33]); // v2 header: logMessageInterval
                if (logInt != 0x7F) {
                    if (msgType == 0) pState.SyncLog = logInt;
                    // AnnounceLog is NOT set here: it must change together with LastAnnounceTicks, only from a
                    // well-formed Announce (see the len >= 61 block below), or a short/spoofed Announce could
                    // rewrite the interval the timeout is judged against.
                }
            } else if (control == 0) { // v1 Sync message body
                if (len >= 84) {
                    sbyte syncInterval = unchecked((sbyte)data[83]); // v1 Sync body: syncInterval (offset 83)
                    if (syncInterval != 0x7F) pState.SyncLog = syncInterval;
                }
                if (len >= 60) {
                    string v1GmId = BitConverter.ToString(data, 54, 6).Replace("-",""); // grandmasterClockUuid (offset 54)
                    // Only a previously wire-verified GM counts as a "change"; the provisional self-ID
                    // a Leader gets before its first GM report is not a real earlier GM.
                    if (pState.GmVerified && pState.GrandmasterId != v1GmId) {
                        Log(string.Format("[GM_CHANGE] {0} v1 GM {1} -> {2}", ip, pState.GrandmasterId, v1GmId));
                    }
                    pState.GrandmasterId = v1GmId;
                    pState.GmVerified = true;
                }
            }

            // Accurate Topology Linking via Delay_Resp: record a confirmed edge for the follower
            // instance whose OwnId matches AND whose domain matches this Delay_Resp's own domain
            // (a device present in multiple domains under the same OwnId must not have a Delay_Resp
            // for one domain confirm a link in a different domain it happens to also be in).
            if (protoVer == "v2" && msgType == 9 && len >= 52) { // v2 Delay_Resp
                string reqId = BitConverter.ToString(data, 44, 8).Replace("-","");
                foreach (var d in devices.Values) {
                    foreach (var kv in d.Protocols) {
                        if (kv.Value.Version == "v2" && kv.Value.Domain == domain && kv.Value.OwnId == reqId) {
                            string linkKey = LinkKey("v2", domain, d.IP);
                            topologyLinks[linkKey] = new TopologyLink { Version = "v2", Domain = domain, FollowerIp = d.IP, LeaderIp = ip, ConfirmedAtTicks = now };
                        }
                    }
                }
            } else if (protoVer == "v1" && control == 3 && len >= 56) { // v1 Delay_Resp
                string reqId = BitConverter.ToString(data, 50, 6).Replace("-",""); // requestingSourceUuid (offset 50)
                foreach (var d in devices.Values) {
                    foreach (var kv in d.Protocols) {
                        if (kv.Value.Version == "v1" && kv.Value.Domain == domain && kv.Value.OwnId == reqId) {
                            string linkKey = LinkKey("v1", domain, d.IP);
                            topologyLinks[linkKey] = new TopologyLink { Version = "v1", Domain = domain, FollowerIp = d.IP, LeaderIp = ip, ConfirmedAtTicks = now };
                        }
                    }
                }
            }

            // GM Info from Announce (v2)
            if (protoVer == "v2" && msgType == 11 && len >= 61) {
                pState.LastAnnounceTicks = now; // same length gate as the Leader role this Announce grants
                // The interval is taken from this same Announce. 0x7F means "no interval given", which must
                // REPLACE any earlier value with null (otherwise an old interval keeps being enforced).
                sbyte annLog = unchecked((sbyte)data[33]);
                pState.AnnounceLog = annLog != 0x7F ? (int?)annLog : null;
                pState.GmPriority1 = data[47]; pState.GmClass = data[48]; pState.GmPriority2 = data[52];
                // Announce body layout (IEEE 1588-2008; checked against linuxptp msg.h and Wireshark
                // packet-ptp.c): currentUtcOffset 44-45 (signed, big-endian), clockAccuracy 49,
                // offsetScaledLogVariance 50-51, stepsRemoved 61-62, timeSource 63 (64 bytes total, no
                // TLV). The time-property flags are in the common header, flagField[1] = byte 7.
                // Bytes up to 60 are covered by the len >= 61 gate above; stepsRemoved/timeSource each
                // need their own length so a short packet never yields a misread value.
                pState.GmAccuracy = data[49];
                pState.GmVariance = (data[50] << 8) | data[51];
                pState.UtcOffset = unchecked((short)((data[44] << 8) | data[45]));
                pState.TimeFlags = data[7];
                pState.StepsRemoved = len >= 63 ? (int?)((data[61] << 8) | data[62]) : null;
                pState.TimeSource = len >= 64 ? (int?)data[63] : null;
                string gmId = BitConverter.ToString(data, 53, 8).Replace("-","");
                if (pState.GmVerified && pState.GrandmasterId != gmId) {
                    Log(string.Format("[GM_CHANGE] {0} v2 GM {1} -> {2}", ip, pState.GrandmasterId, gmId));
                }
                pState.GrandmasterId = gmId;
                pState.GmVerified = true;
            }

            // Clear self-assigned/stale flags if the node is now a Follower
            if (role == "Follower") {
                if (pState.GrandmasterId == ownId) { pState.GrandmasterId = null; pState.GmVerified = false; }
                pState.IsConflict = false; pState.ConflictStartedAtTicks = null;
            }

            // Auto-initialize GM ID for Leaders
            if (role == "Leader" && string.IsNullOrEmpty(pState.GrandmasterId)) {
                pState.GrandmasterId = ownId;
            }

            // Conflict detection (multiple Leaders in the same domain) and GM-mismatch detection
            // are handled centrally in MonitorLoop's periodic sweep, not per-packet here: that
            // keeps this per-packet path O(1) instead of an O(N) device scan on every Leader
            // packet, and makes conflict duration advance on wall-clock time instead of only
            // when packets happen to arrive.

            if (role == "Follower" && ((protoVer == "v2" && msgType == 1) || (protoVer == "v1" && control == 1))) { // Delay_Req
                if (pState.LastDelayReqSeenTicks.HasValue) {
                    pState.LastMeasuredIntervalMs = ElapsedSeconds(pState.LastDelayReqSeenTicks.Value) * 1000.0;
                }
                pState.LastDelayReqSeenTicks = now;
            }

            if (!dev.HasJoined) { dev.HasJoined = true; Log(string.Format("[JOIN] {0} ({1}) joined as {2}", ip, dev.Mac, role)); }
            if (oldRole != role && role != "Unknown") { pState.RoleChangedAtTicks = now; Log(string.Format("[ROLE_CHANGE] {0} ({1}) {2} -> {3}", ip, protoVer, oldRole, role)); }
        }
    }

    /// <summary>
    /// Background loop: device/instance online status, data retention, conflict detection
    /// (multiple Leaders per domain), GM-mismatch detection, and confirmed-link expiry. Centralizing
    /// these here (instead of per-packet) bounds their cost to one O(N) pass per second regardless
    /// of traffic volume, and makes duration-based conditions (10s+ conflict) advance on wall-clock
    /// time rather than only when a matching packet happens to arrive.
    /// </summary>
    static void MonitorLoop() {
        while(!cts.Token.IsCancellationRequested) {
            lock(devices) {
                long now = NowTicks();
                var toRemove = new List<string>();

                foreach (var dev in devices.Values) {
                    foreach (var pState in dev.Protocols.Values) {
                        // Uses LastRoleSeenTicks (only advanced by role-defining packets), not
                        // LastSeenTicks (advanced by any packet including Management/Signaling/Pdelay),
                        // so a Leader/Follower that stopped sending Sync/Announce/Delay_Req/Delay_Resp
                        // ages out even if other non-role traffic from the same instance keeps arriving.
                        // A role-less instance (only peer-delay traffic seen) is judged by its latest
                        // Pdelay packet instead, since it never sends role-defining packets.
                        if (pState.IsOnline && ElapsedSeconds(LastLiveTicks(pState)) >= OfflineTimeoutSeconds) {
                            pState.IsOnline = false;
                            pState.IsConflict = false; pState.ConflictStartedAtTicks = null; pState.GmMismatchLogged = false;
                            // A stale verification must not survive an outage: if this instance comes back
                            // sending only Sync (e.g. now under a different GM) it counts as unverified
                            // until a fresh Announce / v1 Sync body confirms its GM again.
                            pState.GmVerified = false;
                        }

                        // Announce timeout: an ACTIVE v2 Leader (still sending Sync etc., so not offline) whose
                        // Announces have stopped for AnnounceReceiptTimeout x its own Announce interval. This is
                        // the early sign of a GM change that the offline timeout cannot see, since Sync keeps the
                        // Leader "alive". The limit has a 1 s floor so sub-second intervals do not trip on jitter,
                        // and an absurd interval (log up to +127) simply yields a limit that is never reached.
                        bool announceStale = false;
                        if (pState.IsOnline && pState.Version == "v2" && pState.Role == "Leader" && pState.LastAnnounceTicks.HasValue && pState.AnnounceLog.HasValue) {
                            double annInterval = Math.Pow(2.0, pState.AnnounceLog.Value);
                            double annLimit = Math.Max(AnnounceReceiptTimeout * annInterval, 1.0);
                            // Count from the later of the last Announce and the moment this instance became a
                            // Leader (again): a Follower that returns to Leader must not be judged against an
                            // Announce from before it stopped being one.
                            long annRef = pState.LastAnnounceTicks.Value;
                            if (pState.RoleChangedAtTicks.HasValue && pState.RoleChangedAtTicks.Value > annRef) annRef = pState.RoleChangedAtTicks.Value;
                            double sinceAnnounce = ElapsedSeconds(annRef);
                            announceStale = sinceAnnounce > annLimit;
                            if (announceStale && !pState.AnnounceStaleLogged) {
                                pState.AnnounceStaleLogged = true;
                                Log(string.Format("[ANNOUNCE_TIMEOUT] {0} (v2, domain {1}) Leader has sent no Announce for {2}s (limit {3}s = {4} x 2^{5}s) but is still active.", dev.IP, pState.Domain, sinceAnnounce.ToString("0.#", CultureInfo.InvariantCulture), annLimit.ToString("0.##", CultureInfo.InvariantCulture), AnnounceReceiptTimeout.ToString("0.##", CultureInfo.InvariantCulture), pState.AnnounceLog.Value));
                            }
                        }
                        pState.AnnounceStale = announceStale;
                        if (!announceStale) pState.AnnounceStaleLogged = false;
                    }
                    double idle = ElapsedSeconds(dev.LastSeenTicks);
                    if (idle >= OfflineTimeoutSeconds && dev.IsOnline) { dev.IsOnline = false; Log(string.Format("[OFFLINE] {0} ({1}) stopped responding.", GetVendorSafe(dev.Mac), dev.IP)); }
                    if (OfflineRetentionHours > 0 && idle >= (OfflineRetentionHours * 3600.0)) toRemove.Add(dev.IP);
                }

                // Group all currently-online Leader instances by (Version, Domain); 2+ in the same
                // group means those Leaders are in conflict with each other.
                var leaderGroups = new Dictionary<string, List<ProtocolState>>();
                foreach (var dev in devices.Values) {
                    foreach (var pState in dev.Protocols.Values) {
                        if (pState.IsOnline && pState.Role == "Leader") {
                            string key = StateKey(pState.Version, pState.Domain);
                            List<ProtocolState> list;
                            if (!leaderGroups.TryGetValue(key, out list)) { list = new List<ProtocolState>(); leaderGroups[key] = list; }
                            list.Add(pState);
                        }
                    }
                }
                var gmObservations = new List<KeyValuePair<ProtocolState, string>>();
                foreach (var group in leaderGroups.Values) {
                    // Several Leaders in one domain is NORMAL with Boundary Clocks (the GM and each BC
                    // below it all send Sync/Announce). A real conflict is several distinct
                    // Grandmasters: Leaders whose wire-verified GM IDs differ. Leaders whose GM is not
                    // yet verified (no Announce / Sync body seen) are left out until it is.
                    var gmIds = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var pState in group) {
                        if (pState.GmVerified && !string.IsNullOrEmpty(pState.GrandmasterId)) gmIds.Add(pState.GrandmasterId);
                    }
                    bool conflicted = gmIds.Count >= 2;
                    gmObservations.Add(new KeyValuePair<ProtocolState, string>(group[0], gmIds.Count == 1 ? gmIds.Min : null));
                    foreach (var pState in group) {
                        if (conflicted && pState.GmVerified) {
                            if (!pState.ConflictStartedAtTicks.HasValue) pState.ConflictStartedAtTicks = now;
                            double sec = ElapsedSeconds(pState.ConflictStartedAtTicks.Value);
                            if (sec >= ConflictPersistThresholdSeconds) {
                                if (!pState.IsConflict) Log(string.Format("[CONFLICT_ALERT] Domain {0} ({1}) Persistent conflict detected ({2}s+): multiple Grandmasters {3}.", pState.Domain, pState.Version, ConflictPersistThresholdSeconds.ToString("0.##", CultureInfo.InvariantCulture), string.Join(", ", gmIds.ToArray())));
                                pState.IsConflict = true;
                            }
                        } else {
                            pState.ConflictStartedAtTicks = null; pState.IsConflict = false;
                        }
                    }
                }

                // GM history: refresh every already-tracked domain first, then offer new ones, so a full
                // table only ever evicts domains that were genuinely not active this sweep.
                foreach (var obs in gmObservations) {
                    if (domainGms.ContainsKey(StateKey(obs.Key.Version, obs.Key.Domain))) TrackDomainGm(obs.Key.Version, obs.Key.Domain, obs.Value, now);
                }
                foreach (var obs in gmObservations) {
                    if (!domainGms.ContainsKey(StateKey(obs.Key.Version, obs.Key.Domain))) TrackDomainGm(obs.Key.Version, obs.Key.Domain, obs.Value, now);
                }

                // Delay-mechanism mixing: within one PTPv2 domain, devices using E2E (Delay_Req/Resp) and
                // devices using P2P (Pdelay_*) together usually mean a profile/config mismatch. It is only a
                // WARNING (a link whose two ends agree can legitimately differ from another link), and it
                // must persist as long as a Conflict before it is reported, to ride out reconfiguration.
                // Mechanism recency (not IsOnline) decides who counts, so a stale marker drops out by itself.
                var mechGroups = new Dictionary<string, List<ProtocolState>>();
                foreach (var dev in devices.Values) {
                    foreach (var pState in dev.Protocols.Values) {
                        pState.MechMismatch = false;
                        if (pState.Version != "v2" || DelayMech(pState).Length == 0) continue;
                        string mkey = StateKey(pState.Version, pState.Domain);
                        List<ProtocolState> mlist;
                        if (!mechGroups.TryGetValue(mkey, out mlist)) { mlist = new List<ProtocolState>(); mechGroups[mkey] = mlist; }
                        mlist.Add(pState);
                    }
                }
                var mixedKeys = new HashSet<string>();
                foreach (var mg in mechGroups) {
                    int e2eCount = 0, p2pCount = 0;
                    foreach (var pState in mg.Value) {
                        string m = DelayMech(pState);
                        if (m.IndexOf("E2E", StringComparison.Ordinal) >= 0) e2eCount++;
                        if (m.IndexOf("P2P", StringComparison.Ordinal) >= 0) p2pCount++;
                    }
                    if (e2eCount == 0 || p2pCount == 0) continue;
                    mixedKeys.Add(mg.Key);
                    long since;
                    if (!mechMismatchSince.TryGetValue(mg.Key, out since)) { since = now; mechMismatchSince[mg.Key] = since; }
                    if (ElapsedSeconds(since) >= ConflictPersistThresholdSeconds) {
                        foreach (var pState in mg.Value) pState.MechMismatch = true;
                        if (mechMismatchLogged.Add(mg.Key)) Log(string.Format("[DELAY_MECH_MISMATCH] Domain {0} ({1}): both E2E ({2} devices) and P2P ({3} devices) delay mechanisms observed ({4}s+).", mg.Value[0].Domain, mg.Value[0].Version, e2eCount, p2pCount, ConflictPersistThresholdSeconds.ToString("0.##", CultureInfo.InvariantCulture)));
                    }
                }
                foreach (var k in mechMismatchSince.Keys.Where(k => !mixedKeys.Contains(k)).ToList()) { mechMismatchSince.Remove(k); mechMismatchLogged.Remove(k); }

                // Announce-interval mismatch: the Leaders of one v2 domain announcing at different intervals.
                // Only a WARNING: linuxptp's documentation says the Announce interval "should be the same in
                // the whole domain", which is guidance, not a hard rule (a Boundary Clock can differ by design),
                // so it must persist like a Conflict first. Sync intervals are NOT compared (no source states
                // they must match). Leaders already reported as not announcing are left out (stale value).
                var annGroups = new Dictionary<string, List<ProtocolState>>();
                foreach (var dev in devices.Values) {
                    foreach (var pState in dev.Protocols.Values) {
                        pState.IntervalMismatch = false;
                        if (pState.Version != "v2" || !pState.IsOnline || pState.Role != "Leader" || !pState.AnnounceLog.HasValue || pState.AnnounceStale) continue;
                        string akey = StateKey(pState.Version, pState.Domain);
                        List<ProtocolState> alist;
                        if (!annGroups.TryGetValue(akey, out alist)) { alist = new List<ProtocolState>(); annGroups[akey] = alist; }
                        alist.Add(pState);
                    }
                }
                var intervalKeys = new HashSet<string>();
                foreach (var ag in annGroups) {
                    var logs = new SortedSet<int>();
                    foreach (var pState in ag.Value) logs.Add(pState.AnnounceLog.Value);
                    if (logs.Count < 2) continue;
                    intervalKeys.Add(ag.Key);
                    long sinceI;
                    if (!intervalMismatchSince.TryGetValue(ag.Key, out sinceI)) { sinceI = now; intervalMismatchSince[ag.Key] = sinceI; }
                    if (ElapsedSeconds(sinceI) >= ConflictPersistThresholdSeconds) {
                        foreach (var pState in ag.Value) pState.IntervalMismatch = true;
                        if (intervalMismatchLogged.Add(ag.Key)) Log(string.Format("[INTERVAL_MISMATCH] Domain {0} ({1}): Leaders announce at different intervals ({2}); the Announce interval should be the same across a domain.", ag.Value[0].Domain, ag.Value[0].Version, string.Join(", ", logs.Select(l => "2^" + l.ToString(CultureInfo.InvariantCulture) + " s").ToArray())));
                    }
                }
                foreach (var k in intervalMismatchSince.Keys.Where(k => !intervalKeys.Contains(k)).ToList()) { intervalMismatchSince.Remove(k); intervalMismatchLogged.Remove(k); }

                // Expire confirmed topology links whose Leader is no longer valid (offline, or no
                // longer Leader in that domain), or that haven't been reconfirmed by a fresh
                // Delay_Resp within LinkStaleAfterSeconds, then check GM-mismatch on the links that
                // remain. The staleness window matters because a Leader that never goes offline could
                // otherwise let a link outlive the Follower's actual (possibly since-changed) parent
                // indefinitely, since ConfirmedAtTicks would otherwise never be consulted again.
                var staleLinks = new List<string>();
                foreach (var kv in topologyLinks) {
                    var link = kv.Value;
                    string key = StateKey(link.Version, link.Domain);
                    DeviceInfo leaderDev = null;
                    ProtocolState leaderState = null;
                    bool leaderValid = devices.TryGetValue(link.LeaderIp, out leaderDev) && leaderDev.Protocols.TryGetValue(key, out leaderState) && leaderState.Role == "Leader" && leaderState.IsOnline;
                    if (!leaderValid || ElapsedSeconds(link.ConfirmedAtTicks) >= LinkStaleAfterSeconds) {
                        staleLinks.Add(kv.Key);
                        // Reset the guard so a genuinely new mismatch (after this follower is later
                        // confirmed against a different Leader) is logged fresh instead of being
                        // silently suppressed by a guard left over from this now-defunct link.
                        DeviceInfo staleFollowerDev;
                        ProtocolState staleFollowerState;
                        if (devices.TryGetValue(link.FollowerIp, out staleFollowerDev) && staleFollowerDev.Protocols.TryGetValue(key, out staleFollowerState)) staleFollowerState.GmMismatchLogged = false;
                        continue;
                    }

                    DeviceInfo followerDev;
                    ProtocolState followerState;
                    if (!devices.TryGetValue(link.FollowerIp, out followerDev) || !followerDev.Protocols.TryGetValue(key, out followerState) || !followerState.IsOnline) continue;
                    // A confirmed link only describes a Follower relationship; if this instance has
                    // since become a Leader itself (e.g. after a BMCA re-election) it is no longer
                    // "following" anyone, so evaluating a mismatch against the old parent is meaningless.
                    if (followerState.Role != "Follower") { followerState.GmMismatchLogged = false; continue; }

                    bool mismatch = followerState.GrandmasterId != null && leaderState.GrandmasterId != null && followerState.GrandmasterId != leaderState.GrandmasterId;
                    if (mismatch) {
                        if (!followerState.GmMismatchLogged) {
                            Log(string.Format("[GM_MISMATCH] {0} ({1}) is following GM {2}, but its parent {3} follows GM {4}!", link.FollowerIp, link.Version, followerState.GrandmasterId, link.LeaderIp, leaderState.GrandmasterId));
                            followerState.GmMismatchLogged = true;
                        }
                    } else {
                        followerState.GmMismatchLogged = false;
                    }
                }
                foreach (var k in staleLinks) topologyLinks.Remove(k);

                foreach(var k in toRemove) {
                    Log("[LEAVE] " + devices[k].IP + " (Retention expired)");
                    RemoveDeviceLinks(k);
                    devices.Remove(k);
                }
            }
            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// Loop for starting the Web UI HTTP server and waiting for requests.
    /// </summary>
    static void WebServerLoop() {
        try {
            httpListener.Prefixes.Add("http://localhost:" + WebPort + "/");
            httpListener.Prefixes.Add("http://127.0.0.1:" + WebPort + "/");
            httpListener.Start();
            // Bound idle/slow connections so a stalled client cannot pin a thread-pool thread forever.
            var timeout = TimeSpan.FromSeconds(30);
            httpListener.TimeoutManager.IdleConnection = timeout;
            httpListener.TimeoutManager.HeaderWait = timeout;
            httpListener.TimeoutManager.EntityBody = timeout;
        } catch (Exception ex) {
            Log(string.Format("[ERROR] Web server failed to start on port {0}: {1} (Is the port already in use?)", WebPort, ex.Message));
            return;
        }
        int consecutiveFailures = 0;
        while(httpListener.IsListening && !cts.Token.IsCancellationRequested) {
            HttpListenerContext context;
            try {
                context = httpListener.GetContext();
                consecutiveFailures = 0;
            } catch (Exception ex) {
                if (!httpListener.IsListening || cts.Token.IsCancellationRequested) break;
                if (++consecutiveFailures >= 5) {
                    Log("[ERROR] Web server stopped after repeated accept failures: " + ex.Message);
                    break;
                }
                continue;
            }
            // Reject immediately on the accept thread, before spawning a Task, so a flood of slow
            // clients cannot queue an unbounded number of pending work items on the thread pool --
            // this is the actual admission-control point; the Wait(0) below is just the corresponding
            // release-side bookkeeping once a request has been admitted.
            if (!httpConcurrency.Wait(0)) {
                try { context.Response.StatusCode = 503; context.Response.Close(); } catch (Exception) { /* Best-effort */ }
                continue;
            }
            var ctxCopy = context;
            // Dispatch to the thread pool so one slow client cannot block other requests.
            Task.Factory.StartNew(() => {
                try { ProcessRequest(ctxCopy); } finally { httpConcurrency.Release(); }
            });
        }
    }

    /// <summary>
    /// Escapes a string as a JSON string literal (quotes, backslashes, and control characters).
    /// </summary>
    static string JsonStr(string s) {
        if (s == null) return "\"\"";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s) {
            switch (c) {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Formats a double as a JSON number, independent of the OS locale (decimal point is always '.').
    /// </summary>
    static string JsonInt(int? v) {
        return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null";
    }

    /// <summary>
    /// Decodes the Announce time-property flag byte (flagField[1]) as a JSON object, or null when no
    /// Announce has been read. Bit meanings: 0 leap61, 1 leap59, 2 currentUtcOffsetValid,
    /// 3 ptpTimescale, 4 timeTraceable, 5 frequencyTraceable.
    /// </summary>
    static string JsonTimeProps(int? flags) {
        if (!flags.HasValue) return "null";
        int f = flags.Value;
        Func<int, string> b = bit => (f & (1 << bit)) != 0 ? "true" : "false";
        return "{\"leap61\":" + b(0) + ",\"leap59\":" + b(1) + ",\"utcValid\":" + b(2) + ",\"ptpTimescale\":" + b(3) + ",\"timeTraceable\":" + b(4) + ",\"freqTraceable\":" + b(5) + "}";
    }

    static string JsonNum(double d) {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects cross-site requests to state-mutating endpoints. A request with no Origin header
    /// (typical for same-page same-origin fetch in most browsers) is allowed; a request with an
    /// Origin that doesn't match this server's own localhost/127.0.0.1 origin is rejected. This is
    /// a lightweight mitigation, not a full CSRF-token scheme, appropriate for a localhost-only tool.
    /// </summary>
    static bool IsSameOriginRequest(HttpListenerRequest req) {
        string origin = req.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;
        Uri originUri;
        // Parse rather than string-compare: a browser omits the port from the Origin header when it
        // is the scheme's default (e.g. "http://localhost" for port 80), so a literal ":{WebPort}"
        // suffix comparison would wrongly reject legitimate same-origin requests on port 80.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out originUri)) return false;
        int expectedPort;
        if (!int.TryParse(WebPort, NumberStyles.None, CultureInfo.InvariantCulture, out expectedPort)) return false;
        bool hostMatches = originUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || originUri.Host == "127.0.0.1";
        return originUri.Scheme == "http" && hostMatches && originUri.Port == expectedPort;
    }

    /// <summary>
    /// Processes HTTP requests and returns API data (JSON) or HTML content.
    /// </summary>
    static void ProcessRequest(HttpListenerContext context) {
        var res = context.Response;
        try {
            string path = context.Request.Url.AbsolutePath;
            bool isMutatingApi = context.Request.HttpMethod == "POST" && (path == "/api/clear_offline" || path == "/api/clear_all");
            if (isMutatingApi && !IsSameOriginRequest(context.Request)) {
                res.StatusCode = 403;
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal)) res.Headers.Add("Cache-Control", "no-store");

            if (path == "/api/clear_offline" && context.Request.HttpMethod == "POST") {
                int count = 0;
                lock(devices) {
                    var toRemove = devices.Where(x => !x.Value.IsOnline).Select(x => x.Key).ToList();
                    count = toRemove.Count;
                    foreach(var k in toRemove) {
                        RemoveDeviceLinks(k);
                        devices.Remove(k);
                    }
                }
                Log(string.Format("[SYSTEM] Offline devices cleared by WebUI ({0} nodes).", count));
                res.StatusCode = 200;
            } else if (path == "/api/clear_all" && context.Request.HttpMethod == "POST") {
                lock(devices) { devices.Clear(); topologyLinks.Clear(); domainGms.Clear(); gmHistoryLimitWarned = false; mechMismatchSince.Clear(); mechMismatchLogged.Clear(); intervalMismatchSince.Clear(); intervalMismatchLogged.Clear(); }
                lock(logLock) { logs.Clear(); }
                Log("[SYSTEM] Network state and logs cleared by WebUI.");
                res.StatusCode = 200;
            } else if (path == "/api/data") {
                var sb = new StringBuilder();
                sb.Append("{\"expectedDelayInterval\":").Append(JsonNum(ExpectedDelayInterval));
                sb.Append(",\"delayAlertThresholdRate\":").Append(JsonNum(DelayAlertThresholdRate)).Append(",");
                lock(devices) {
                    sb.Append("\"devices\":["); bool f = true;
                    foreach(var dev in devices.Values) {
                        if(!f) sb.Append(","); f = false;
                        sb.Append("{\"ip\":").Append(JsonStr(dev.IP)).Append(",\"mac\":").Append(JsonStr(dev.Mac));
                        sb.Append(",\"online\":").Append(dev.IsOnline?"true":"false").Append(",");
                        sb.Append("\"idleSeconds\":").Append((int)ElapsedSeconds(dev.LastSeenTicks)).Append(",");
                        sb.Append("\"uptimeSeconds\":").Append((int)ElapsedSeconds(dev.OnlineSinceTicks)).Append(",");
                        sb.Append("\"protocols\":["); bool fp = true;
                        foreach(var kv in dev.Protocols) {
                            var pState = kv.Value;
                            if(!fp) sb.Append(","); fp = false;
                            string parentIp = ""; bool linkConfirmed = false;
                            TopologyLink link;
                            if (topologyLinks.TryGetValue(LinkKey(pState.Version, pState.Domain, dev.IP), out link)) {
                                parentIp = link.LeaderIp; linkConfirmed = true;
                            } else if (pState.Role == "Follower") {
                                string fb = FindFallbackLeaderIp(pState.Version, pState.Domain, dev.IP);
                                if (fb != null) parentIp = fb;
                            }
                            sb.Append("{\"version\":").Append(JsonStr(pState.Version));
                            sb.Append(",\"domain\":").Append(JsonStr(pState.Domain));
                            sb.Append(",\"role\":").Append(JsonStr(pState.Role));
                            sb.Append(",\"online\":").Append(pState.IsOnline?"true":"false").Append(",");
                            sb.Append("\"ownId\":").Append(JsonStr(pState.OwnId ?? "")).Append(",");
                            sb.Append("\"syncLog\":").Append(pState.SyncLog.HasValue ? pState.SyncLog.Value.ToString() : "null");
                            sb.Append(",\"announceLog\":").Append(pState.AnnounceLog.HasValue ? pState.AnnounceLog.Value.ToString() : "null").Append(",");
                            sb.Append("\"gmId\":").Append(JsonStr(pState.GrandmasterId ?? ""));
                            sb.Append(",\"gmVerified\":").Append(pState.GmVerified?"true":"false");
                            sb.Append(",\"gmAccuracy\":").Append(JsonInt(pState.GmAccuracy));
                            sb.Append(",\"gmVariance\":").Append(JsonInt(pState.GmVariance));
                            sb.Append(",\"stepsRemoved\":").Append(JsonInt(pState.StepsRemoved));
                            sb.Append(",\"timeSource\":").Append(JsonInt(pState.TimeSource));
                            sb.Append(",\"utcOffset\":").Append(JsonInt(pState.UtcOffset));
                            sb.Append(",\"timeProps\":").Append(JsonTimeProps(pState.TimeFlags));
                            sb.Append(",\"delayMech\":").Append(JsonStr(DelayMech(pState)));
                            sb.Append(",\"mechMismatch\":").Append(pState.MechMismatch?"true":"false");
                            sb.Append(",\"announceStale\":").Append(pState.AnnounceStale?"true":"false");
                            sb.Append(",\"intervalMismatch\":").Append(pState.IntervalMismatch?"true":"false");
                            sb.Append(",\"vendor\":").Append(JsonStr(GetVendorSafe(dev.Mac))).Append(",");
                            sb.Append("\"gmPriority1\":").Append(pState.GmPriority1.HasValue ? pState.GmPriority1.Value.ToString() : "null").Append(",");
                            sb.Append("\"gmClass\":").Append(pState.GmClass.HasValue ? pState.GmClass.Value.ToString() : "null").Append(",");
                            sb.Append("\"gmPriority2\":").Append(pState.GmPriority2.HasValue ? pState.GmPriority2.Value.ToString() : "null").Append(",");
                            sb.Append("\"isConflict\":").Append(pState.IsConflict?"true":"false").Append(",");
                            sb.Append("\"conflictSeconds\":").Append(pState.ConflictStartedAtTicks.HasValue?((int)ElapsedSeconds(pState.ConflictStartedAtTicks.Value)).ToString():"0").Append(",");
                            sb.Append("\"lastMeasuredIntervalMs\":").Append(pState.LastMeasuredIntervalMs.HasValue?((int)pState.LastMeasuredIntervalMs.Value).ToString():"null").Append(",");
                            sb.Append("\"parentIp\":").Append(JsonStr(parentIp)).Append(",");
                            sb.Append("\"linkConfirmed\":").Append(linkConfirmed?"true":"false").Append(",");
                            sb.Append("\"roleElapsedSeconds\":").Append(pState.RoleChangedAtTicks.HasValue?((int)ElapsedSeconds(pState.RoleChangedAtTicks.Value)).ToString():"-1").Append("}");
                        }
                        sb.Append("]}");
                    }
                    sb.Append("],\"gmHistory\":[");
                    bool fh = true;
                    foreach (var kv in domainGms) {
                        var evs = kv.Value.Events;
                        if (evs.Count == 0) continue;
                        if (!fh) sb.Append(","); fh = false;
                        // The dictionary key is the internal version+KeySep+domain composite; the version
                        // part never contains KeySep, so splitting at the first one is unambiguous.
                        string[] parts = kv.Key.Split(new string[] { KeySep }, 2, StringSplitOptions.None);
                        sb.Append("{\"version\":").Append(JsonStr(parts[0])).Append(",\"domain\":").Append(JsonStr(parts.Length > 1 ? parts[1] : "")).Append(",\"settledGm\":").Append(JsonStr(kv.Value.Gm)).Append(",\"events\":[");
                        for (int i = 0; i < evs.Count; i++) {
                            var ev = evs[i];
                            if (i > 0) sb.Append(",");
                            sb.Append("{\"time\":").Append(JsonStr(ev.Time)).Append(",\"oldGm\":").Append(JsonStr(ev.OldGm)).Append(",\"newGm\":").Append(JsonStr(ev.NewGm)).Append("}");
                        }
                        sb.Append("]}");
                    }
                    sb.Append("],\"logs\":[");
                    lock(logLock) {
                        for(int i=0; i<logs.Count; i++) { if(i>0) sb.Append(","); sb.Append(JsonStr(logs[i])); }
                    }
                    sb.Append("]}");
                }
                byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
                res.ContentType = "application/json";
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b,0,b.Length);
            } else if (path == "/" || path == "/index.html") {
                byte[] b = Encoding.UTF8.GetBytes(HtmlContent.Replace("{PORT}", WebPort));
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b,0,b.Length);
            } else {
                // Only "/", the dashboard's own API paths, and the two POST endpoints above are ever
                // requested by assets/web/*; anything else is unexpected and returns a real 404 rather
                // than silently serving the dashboard HTML (which could mask a client-side typo or be
                // cached as if it were a valid resource).
                res.StatusCode = 404;
            }
        } catch (HttpListenerException) {
            // Client disconnected mid-response (reload / tab closed): abort quietly.
        } catch (IOException) {
            // Broken connection while writing: abort quietly.
        } catch (Exception ex) {
            Log("[ERROR] Web request handling failed: " + ex.Message);
        } finally {
            try { res.Close(); } catch (Exception) { /* Response already aborted */ }
        }
    }

    // No external font/CDN references: the dashboard must render fully offline (production networks are often isolated).
    // The HTML/CSS/JS live in assets/web/*, embedded as manifest resources at compile time (csc /resource) so
    // the JS can be syntax-checked (e.g. `node --check`) outside the C# compiler while the exe stays dependency-free.
    static string LoadEmbeddedResource(string logicalName) {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream(logicalName)) {
            if (stream == null) throw new InvalidOperationException("Missing embedded resource: " + logicalName);
            using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                return reader.ReadToEnd();
            }
        }
    }

    static readonly string HtmlContent =
        LoadEmbeddedResource("PTPMonitor.web.index.html")
            .Replace("__STYLE_PLACEHOLDER__", LoadEmbeddedResource("PTPMonitor.web.style.css"))
            .Replace("__SCRIPT_PLACEHOLDER__", LoadEmbeddedResource("PTPMonitor.web.app.js"));
}
