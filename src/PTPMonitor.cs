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
    public int? SyncLog { get; set; }
    public int? AnnounceLog { get; set; }

    public long? LastDelayReqSeenTicks { get; set; }
    public double? LastMeasuredIntervalMs { get; set; }

    public int? GmPriority1 { get; set; }
    public int? GmPriority2 { get; set; }
    public int? GmClass { get; set; }

    // Freshness/online status for THIS instance specifically, so a stale Leader on one
    // domain/version can't linger just because another instance of the same device is alive.
    public long LastSeenTicks { get; set; }
    public bool IsOnline { get; set; }

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
/// Main program class for PTPMonitor.
/// Controls packet capture, monitoring loops, and the web server.
/// </summary>
class Program {
    static readonly string McastGroup = "224.0.1.129";
    static readonly int[] Ports = new int[] { 319, 320 };
    static string WebPort = "8080";
    static double OfflineRetentionHours = 24.0;
    static double ExpectedDelayInterval = 2.0;    // Added in v1.6.8
    static double DelayAlertThresholdRate = 1.5;  // Added in v1.6.8
    static double OfflineTimeoutSeconds = 10.0;   // Added in v1.6.8
    // How long two simultaneous Leaders must persist in the same (version, domain) before a
    // [CONFLICT_ALERT] is logged. Not a mutable readonly const so tests can shrink it for speed.
    static double ConflictPersistThresholdSeconds = 10.0;
    static readonly int MaxDevices = 512;         // Cap to prevent unbounded memory growth from spoofed sources
    static readonly int MaxRotatedLogFiles = 20;  // Cap rotated log generations to bound disk usage
    static bool deviceLimitWarned = false;

    // Composite dictionary keys are built as "version" + KeySep + "domain" [+ KeySep + "followerIp"].
    // A control character is used as the separator (rather than e.g. '|') because a v1 "domain" is
    // parsed directly from packet bytes and could otherwise collide with a printable separator.
    const string KeySep = "";

    static Dictionary<string, DeviceInfo> devices = new Dictionary<string, DeviceInfo>();
    static Dictionary<string, TopologyLink> topologyLinks = new Dictionary<string, TopologyLink>();
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
    /// Entry point. Handles interface selection and starts monitoring tasks.
    /// </summary>
    static void Main(string[] args) {
        LoadConfig();
        Console.WriteLine("=== PTPMonitor v1.8.0 ===");

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
                try {
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(IPAddress.Parse(McastGroup), currentLocalIp));
                    sock.Close();
                } catch (Exception) { /* Best-effort cleanup during shutdown */ }
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
            if (msgType == 0 || msgType == 11 || msgType == 8 || msgType == 9) role = "Leader"; // Sync, Announce, Follow_Up, Delay_Resp
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
            domain = Encoding.UTF8.GetString(data, 4, 16).TrimEnd('\0', ' ');
            ownId = BitConverter.ToString(data, 22, 6).Replace("-","");
            if (control == 0 || control == 2 || control == 3) role = "Leader"; // Sync, Follow_Up, Delay_Resp
            else if (control == 1) role = "Follower"; // Delay_Req
        }

        long now = NowTicks();
        string stateKey = StateKey(protoVer, domain);

        lock(devices) {
            if (!devices.ContainsKey(ip)) {
                if (devices.Count >= MaxDevices) {
                    if (!deviceLimitWarned) {
                        deviceLimitWarned = true;
                        Log(string.Format("[WARN] Device limit ({0}) reached; additional devices are ignored.", MaxDevices));
                    }
                    return;
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

            if (!dev.Protocols.ContainsKey(stateKey)) dev.Protocols[stateKey] = new ProtocolState(protoVer, domain);
            var pState = dev.Protocols[stateKey];
            string oldRole = pState.Role;
            pState.OwnId = ownId;
            pState.LastSeenTicks = now;
            pState.IsOnline = true;
            if (role != "Unknown") pState.Role = role;

            if (protoVer == "v2") {
                sbyte logInt = unchecked((sbyte)data[33]); // v2 header: logMessageInterval
                if (logInt != 0x7F) {
                    if (msgType == 0) pState.SyncLog = logInt;
                    else if (msgType == 11) pState.AnnounceLog = logInt;
                }
            } else if (control == 0) { // v1 Sync message body
                if (len >= 84) {
                    sbyte syncInterval = unchecked((sbyte)data[83]); // v1 Sync body: syncInterval (offset 83)
                    if (syncInterval != 0x7F) pState.SyncLog = syncInterval;
                }
                if (len >= 60) {
                    string v1GmId = BitConverter.ToString(data, 54, 6).Replace("-",""); // grandmasterClockUuid (offset 54)
                    if (pState.GrandmasterId != null && pState.GrandmasterId != v1GmId) Log(string.Format("[GM_CHANGE] {0} v1 GM -> {1}", ip, v1GmId));
                    pState.GrandmasterId = v1GmId;
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
                pState.GmPriority1 = data[47]; pState.GmClass = data[48]; pState.GmPriority2 = data[52];
                string gmId = BitConverter.ToString(data, 53, 8).Replace("-","");
                if (pState.GrandmasterId != null && pState.GrandmasterId != gmId) Log(string.Format("[GM_CHANGE] {0} v2 GM -> {1}", ip, gmId));
                pState.GrandmasterId = gmId;
            }

            // Clear self-assigned/stale flags if the node is now a Follower
            if (role == "Follower") {
                if (pState.GrandmasterId == ownId) pState.GrandmasterId = null;
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
                        if (pState.IsOnline && ElapsedSeconds(pState.LastSeenTicks) >= OfflineTimeoutSeconds) {
                            pState.IsOnline = false;
                            pState.IsConflict = false; pState.ConflictStartedAtTicks = null; pState.GmMismatchLogged = false;
                        }
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
                foreach (var group in leaderGroups.Values) {
                    bool conflicted = group.Count >= 2;
                    foreach (var pState in group) {
                        if (conflicted) {
                            if (!pState.ConflictStartedAtTicks.HasValue) pState.ConflictStartedAtTicks = now;
                            double sec = ElapsedSeconds(pState.ConflictStartedAtTicks.Value);
                            if (sec >= ConflictPersistThresholdSeconds) {
                                if (!pState.IsConflict) Log(string.Format("[CONFLICT_ALERT] Domain {0} ({1}) Persistent conflict detected (10s+).", pState.Domain, pState.Version));
                                pState.IsConflict = true;
                            }
                        } else {
                            pState.ConflictStartedAtTicks = null; pState.IsConflict = false;
                        }
                    }
                }

                // Expire confirmed topology links whose Leader is no longer valid (offline, or no
                // longer Leader in that domain), then check GM-mismatch on the links that remain.
                var staleLinks = new List<string>();
                foreach (var kv in topologyLinks) {
                    var link = kv.Value;
                    string key = StateKey(link.Version, link.Domain);
                    DeviceInfo leaderDev;
                    ProtocolState leaderState;
                    if (!devices.TryGetValue(link.LeaderIp, out leaderDev) || !leaderDev.Protocols.TryGetValue(key, out leaderState) || leaderState.Role != "Leader" || !leaderState.IsOnline) {
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
                lock(devices) { devices.Clear(); topologyLinks.Clear(); }
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
            } else {
                byte[] b = Encoding.UTF8.GetBytes(HtmlContent.Replace("{PORT}", WebPort));
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b,0,b.Length);
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
