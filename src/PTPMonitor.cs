using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Linq;

/// <summary>
/// Holds the state for each PTP domain and version.
/// </summary>
class ProtocolState {
    public string Role { get; set; }
    public string Domain { get; set; }
    public string OwnId { get; set; }
    public string ParentId { get; set; }
    public string GrandmasterId { get; set; }
    public int? SyncLog { get; set; }
    public int? AnnounceLog { get; set; }
    public int? DelayReqLog { get; set; }
    public int? DelayRespLog { get; set; }
    
    public DateTime? LastSyncReceived { get; set; }
    public DateTime? LastDelayReqSeen { get; set; }
    public double? LastMeasuredIntervalMs { get; set; }
    public double? StabilityJitterMs { get; set; }
    
    public DateTime LastSeen { get; set; }
    public bool IsOnline { get; set; }
    
    public int? GmPriority1 { get; set; }
    public int? GmPriority2 { get; set; }
    public int? GmClass { get; set; }
    public string GmClassDesc { get; set; }

    public bool TcTraversed { get; set; }
    public double CorrectionNs { get; set; }
    public string ProfileId { get; set; }
    public bool IsConflict { get; set; }
    public DateTime? RoleChangedAt { get; set; }
    public DateTime? ConflictStartedAt { get; set; }

    public ProtocolState() {
        Role = "Unknown";
        Domain = "0";
        OwnId = null;
        ParentId = null;
        GrandmasterId = null;
        ProfileId = "Standard PTP";
        LastSeen = DateTime.Now;
        IsOnline = true;
        IsConflict = false;
    }
}

/// <summary>
/// Holds information for each device on the network.
/// Manages multiple protocol states in a dictionary.
/// </summary>
class DeviceInfo {
    public string IP { get; set; }
    public string Mac { get; set; }
    public Dictionary<string, ProtocolState> Protocols { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime FirstSeen { get; set; }
    public bool IsOnline { get; set; }
    public bool HasJoined { get; set; }

    public DeviceInfo(string ip) {
        IP = ip;
        Mac = "Unknown";
        Protocols = new Dictionary<string, ProtocolState>();
        LastSeen = DateTime.Now;
        FirstSeen = DateTime.Now;
        IsOnline = true;
        HasJoined = false;
    }
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
    
    static Dictionary<string, DeviceInfo> devices = new Dictionary<string, DeviceInfo>();
    static Dictionary<string, string> followerToLeaderV1 = new Dictionary<string, string>();
    static Dictionary<string, string> followerToLeaderV2 = new Dictionary<string, string>();
    static CancellationTokenSource cts = new CancellationTokenSource();
    static StreamWriter logWriter = null;
    static string currentLogPath = "ptp_monitor.log";
    static object printLock = new object();
    static List<string> logs = new List<string>();
    static object logLock = new object();
    
    static Dictionary<string, string> customVendors = new Dictionary<string, string>();
    static IPAddress currentLocalIp = IPAddress.Any;
    static HttpListener httpListener = new HttpListener();

    /// <summary>
    /// Entry point. Handles interface selection and starts monitoring tasks.
    /// </summary>
    static void Main(string[] args) {
        LoadConfig();
        Console.WriteLine("=== PTPMonitor v1.6.8 (Final Version) ===");
        
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
        if(!string.IsNullOrEmpty(pIn)) WebPort = pIn;

        RotateLogFile();
        Task.Factory.StartNew(WebServerLoop);
        Task.Factory.StartNew(MonitorLoop);

        var sockets = new List<Socket>();
        foreach (int port in Ports) {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.ReceiveTimeout = 1000;
            sock.Bind(new IPEndPoint(currentLocalIp, port));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse(McastGroup), currentLocalIp));
            sockets.Add(sock);
            Task.Factory.StartNew(() => {
                byte[] buffer = new byte[2048];
                while (!cts.Token.IsCancellationRequested) {
                    try {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int received = sock.ReceiveFrom(buffer, ref ep);
                        if (received > 0) ParsePacket(((IPEndPoint)ep).Address.ToString(), buffer, received, port);
                    } catch { }
                }
            });
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
                } catch {}
            }
            if(logWriter != null) logWriter.Close();
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
                    var parts = line.Split('=');
                    if (parts.Length == 2) {
                        string key = parts[0].Trim();
                        string val = parts[1].Trim();
                        if (key.Equals("OfflineRetentionHours", StringComparison.OrdinalIgnoreCase)) {
                            double.TryParse(val, out OfflineRetentionHours);
                        } else if (key.Equals("ExpectedDelayInterval", StringComparison.OrdinalIgnoreCase)) {
                            double.TryParse(val, out ExpectedDelayInterval);
                        } else if (key.Equals("DelayAlertThresholdRate", StringComparison.OrdinalIgnoreCase)) {
                            double.TryParse(val, out DelayAlertThresholdRate);
                        } else if (key.Equals("OfflineTimeoutSeconds", StringComparison.OrdinalIgnoreCase)) {
                            double.TryParse(val, out OfflineTimeoutSeconds);
                        } else {
                            customVendors[key.ToUpper()] = val;
                        }
                    }
                }
            }
        } catch {}
    }

    /// <summary>
    /// Rotates log files on startup and when size exceeds the limit.
    /// </summary>
    static void RotateLogFile() {
        if (logWriter != null) { logWriter.Close(); logWriter = null; }
        string logDir = "logs";
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        string old = currentLogPath;
        string rotated = Path.Combine(logDir, string.Format("ptp_monitor_{0}.log", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
        bool moved = false;
        try { if(File.Exists(old)) { File.Move(old, rotated); moved = true; } } catch {}
        if (!moved && File.Exists(old)) { try { File.WriteAllText(old, ""); } catch {} }
        try { logWriter = new StreamWriter(new FileStream(currentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; } catch {}
        if (logWriter != null) logWriter.WriteLine(string.Format("[{0:yyyy-MM-dd HH:mm:ss}] --- Log Session Started ---", DateTime.Now));
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
                try { logWriter.WriteLine(clean); if (new FileInfo(currentLogPath).Length > 10 * 1024 * 1024) RotateLogFile(); } catch {}
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

        if (protoVer == "v2") {
            msgType = (byte)(data[0] & 0x0F);
            domain = data[4].ToString();
            if (len >= 28) ownId = BitConverter.ToString(data, 20, 8).Replace("-","");
            role = "Unknown";
            if (msgType == 0 || msgType == 11 || msgType == 8 || msgType == 9) role = "Leader"; // Sync, Announce, Follow_Up, Delay_Resp
            else if (msgType == 1 || msgType == 2) role = "Follower"; // Delay_Req, Pdelay_Req
            else if (msgType == 9) role = "Leader"; // Delay_Resp
        } else {
            if (len < 40) return;
            msgType = data[20]; // PTPv1 messageType is at offset 20
            domain = Encoding.UTF8.GetString(data, 4, 16).TrimEnd('\0', ' ');
            ownId = BitConverter.ToString(data, 22, 6).Replace("-","");
            role = "Unknown";
            byte control = data[32];
            if (msgType == 0 || control == 0 || control == 2 || control == 3) role = "Leader";
            else if (msgType == 1 || control == 1) role = "Follower";
        }

        lock(devices) {
            if (!devices.ContainsKey(ip)) devices[ip] = new DeviceInfo(ip);
            var dev = devices[ip];
            dev.LastSeen = DateTime.Now; dev.IsOnline = true;
            if (dev.Mac == "Unknown" && ownId.Length >= 12) {
                // Try to derive MAC from OwnId (UI purpose)
                if (protoVer == "v2" && ownId.Length == 16) dev.Mac = ownId.Substring(0,2)+":"+ownId.Substring(2,2)+":"+ownId.Substring(4,2)+":"+ownId.Substring(10,2)+":"+ownId.Substring(12,2)+":"+ownId.Substring(14,2);
                else if (protoVer == "v1" && ownId.Length == 12) dev.Mac = ownId.Substring(0,2)+":"+ownId.Substring(2,2)+":"+ownId.Substring(4,2)+":"+ownId.Substring(6,2)+":"+ownId.Substring(8,2)+":"+ownId.Substring(10,2);
            }

            if (!dev.Protocols.ContainsKey(protoVer)) dev.Protocols[protoVer] = new ProtocolState();
            var pState = dev.Protocols[protoVer];
            string oldRole = pState.Role;
            pState.Domain = domain; pState.OwnId = ownId; pState.IsOnline = true;
            if (role != "Unknown") pState.Role = role;

            if (len >= 34) {
                sbyte logInt = unchecked((sbyte)data[33]);
                if (logInt != 0x7F) {
                    if (protoVer == "v2") {
                        if (msgType == 0) pState.SyncLog = logInt;
                        else if (msgType == 11) pState.AnnounceLog = logInt;
                    } else if (protoVer == "v1") {
                        if (msgType == 0) {
                            pState.SyncLog = logInt;
                            if (len >= 61) {
                                string v1GmId = BitConverter.ToString(data, 55, 6).Replace("-","");
                                if (pState.GrandmasterId != null && pState.GrandmasterId != v1GmId) Log(string.Format("[GM_CHANGE] {0} v1 GM -> {1}", ip, v1GmId));
                                pState.GrandmasterId = v1GmId;
                            }
                        }
                    }
                }
            }

            // Accurate Topology Linking via Delay_Resp
            if (protoVer == "v2" && msgType == 9 && len >= 52) { // v2 Delay_Resp
                string reqId = BitConverter.ToString(data, 44, 8).Replace("-","");
                foreach(var d in devices.Values) { 
                    if (d.Protocols.ContainsKey("v2") && d.Protocols["v2"].OwnId == reqId) followerToLeaderV2[d.IP] = ip; 
                }
            } else if (protoVer == "v1" && msgType == 3 && len >= 54) { // v1 Delay_Resp
                string reqId = BitConverter.ToString(data, 48, 6).Replace("-","");
                foreach(var d in devices.Values) { 
                    if (d.Protocols.ContainsKey("v1") && d.Protocols["v1"].OwnId == reqId) followerToLeaderV1[d.IP] = ip; 
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
                pState.IsConflict = false; pState.ConflictStartedAt = null; 
            }

            // Auto-initialize GM ID for Leaders
            if (role == "Leader" && string.IsNullOrEmpty(pState.GrandmasterId)) {
                pState.GrandmasterId = ownId;
            }

            if (role == "Leader") {
                bool foundOther = false;
                foreach (var other in devices.Values) {
                    if (other.IP == ip) continue;
                    if (other.IsOnline && other.Protocols.ContainsKey(protoVer)) {
                        var op = other.Protocols[protoVer];
                        if (op.Role == "Leader" && op.Domain == domain) foundOther = true;
                    }
                }
                
                if (foundOther) {
                    if (!pState.ConflictStartedAt.HasValue) pState.ConflictStartedAt = DateTime.Now;
                    double sec = (DateTime.Now - pState.ConflictStartedAt.Value).TotalSeconds;
                    if (sec >= 10.0) {
                        if (!pState.IsConflict) Log(string.Format("[CONFLICT_ALERT] Domain {0} ({1}) Persistent conflict detected (10s+).", domain, protoVer));
                        pState.IsConflict = true; 
                    }
                } else {
                    pState.ConflictStartedAt = null; pState.IsConflict = false;
                }
            }

            if (pState.Role == "Follower") {
                var dict = (protoVer == "v1") ? followerToLeaderV1 : followerToLeaderV2;
                if (!dict.ContainsKey(ip)) {
                    var leader = devices.Values.FirstOrDefault(d => d.IsOnline && d.Protocols.ContainsKey(protoVer) && d.Protocols[protoVer].Role == "Leader" && d.Protocols[protoVer].Domain == domain);
                    if (leader != null) dict[ip] = leader.IP;
                }
                
                // GM Mismatch Detection (Troubleshooting)
                if (dict.ContainsKey(ip) && devices.ContainsKey(dict[ip])) {
                    var parentNode = devices[dict[ip]];
                    if (parentNode.Protocols.ContainsKey(protoVer)) {
                        var pP = parentNode.Protocols[protoVer];
                        if (pP.GrandmasterId != null && pState.GrandmasterId != null && pState.GrandmasterId != pP.GrandmasterId) {
                            Log(string.Format("[GM_MISMATCH] {0} ({1}) is following GM {2}, but its parent {3} follows GM {4}!", ip, protoVer, pState.GrandmasterId, dict[ip], pP.GrandmasterId));
                        }
                    }
                }
            }


            if (role == "Follower" && (msgType == 1 || (protoVer == "v1" && data[20] == 1))) {
                if (pState.LastDelayReqSeen.HasValue) {
                    pState.LastMeasuredIntervalMs = (DateTime.Now - pState.LastDelayReqSeen.Value).TotalMilliseconds;
                }
                pState.LastDelayReqSeen = DateTime.Now;
            }

            if (!dev.HasJoined) { dev.HasJoined = true; Log(string.Format("[JOIN] {0} ({1}) joined as {2}", ip, dev.Mac, role)); }
            if (oldRole != role && role != "Unknown") { pState.RoleChangedAt = DateTime.Now; Log(string.Format("[ROLE_CHANGE] {0} ({1}) {2} -> {3}", ip, protoVer, oldRole, role)); }
        }
    }

    /// <summary>
    /// Background loop for device online status monitoring and data retention management.
    /// </summary>
    static void MonitorLoop() {
        while(!cts.Token.IsCancellationRequested) {
            lock(devices) {
                var toRemove = new List<string>();
                foreach(var kvp in devices) {
                    var dev = kvp.Value;
                    double idle = (DateTime.Now - dev.LastSeen).TotalSeconds;
                    if (idle >= OfflineTimeoutSeconds && dev.IsOnline) { dev.IsOnline = false; Log(string.Format("[OFFLINE] {0} ({1}) stopped responding.", GetVendorSafe(dev.Mac), dev.IP)); }
                    if (OfflineRetentionHours > 0 && idle >= (OfflineRetentionHours * 3600.0)) toRemove.Add(kvp.Key);
                }
                foreach(var k in toRemove) {
                    Log("[LEAVE] " + devices[k].IP + " (Retention expired)");
                    followerToLeaderV1.Remove(k);
                    followerToLeaderV2.Remove(k);
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
        httpListener.Prefixes.Add("http://localhost:" + WebPort + "/");
        httpListener.Prefixes.Add("http://127.0.0.1:" + WebPort + "/");
        try { httpListener.Start(); } catch { return; }
        while(httpListener.IsListening) {
            try { var context = httpListener.GetContext(); ProcessRequest(context); } catch { }
        }
    }

    /// <summary>
    /// Processes HTTP requests and returns API data (JSON) or HTML content.
    /// </summary>
    static void ProcessRequest(HttpListenerContext context) {
        var res = context.Response; string path = context.Request.Url.AbsolutePath;
        
        if (path == "/api/clear_offline" && context.Request.HttpMethod == "POST") {
            int count = 0;
            lock(devices) {
                var toRemove = devices.Where(x => !x.Value.IsOnline).Select(x => x.Key).ToList();
                count = toRemove.Count;
                foreach(var k in toRemove) {
                    followerToLeaderV1.Remove(k);
                    followerToLeaderV2.Remove(k);
                    devices.Remove(k);
                }
            }
            Log(string.Format("[SYSTEM] Offline devices cleared by WebUI ({0} nodes).", count));
            res.StatusCode = 200; res.Close(); return;
        } else if (path == "/api/clear_all" && context.Request.HttpMethod == "POST") {
            lock(devices) { devices.Clear(); followerToLeaderV1.Clear(); followerToLeaderV2.Clear(); }
            lock(logLock) { logs.Clear(); }
            Log("[SYSTEM] Network state and logs cleared by WebUI.");
            res.StatusCode = 200; res.Close(); return;
        } else if (path == "/api/data") {
            var sb = new StringBuilder(); sb.Append("{\"expectedDelayInterval\":"+ExpectedDelayInterval+",\"delayAlertThresholdRate\":"+DelayAlertThresholdRate+",");
            lock(devices) {
                sb.Append("\"devices\":["); bool f = true;
                foreach(var dev in devices.Values) {
                    if(!f) sb.Append(","); f = false;
                    sb.Append("{\"ip\":\""+dev.IP+"\",\"mac\":\""+dev.Mac+"\",\"online\":"+(dev.IsOnline?"true":"false")+",");
                    sb.Append("\"idleSeconds\":"+(int)(DateTime.Now-dev.LastSeen).TotalSeconds+",");
                    sb.Append("\"uptimeSeconds\":"+(int)(DateTime.Now-dev.FirstSeen).TotalSeconds+",");
                    sb.Append("\"protocols\":{"); bool fp = true;
                    foreach(var p in dev.Protocols) {
                        if(!fp) sb.Append(","); fp = false;
                        string pip = "";
                        if (p.Key == "v1" && followerToLeaderV1.ContainsKey(dev.IP)) pip = followerToLeaderV1[dev.IP];
                        if (p.Key == "v2" && followerToLeaderV2.ContainsKey(dev.IP)) pip = followerToLeaderV2[dev.IP];
                        sb.Append("\""+p.Key+"\":{\"role\":\""+p.Value.Role+"\",\"domain\":\""+p.Value.Domain+"\",\"ownId\":\""+(p.Value.OwnId??"")+"\",");
                        sb.Append("\"syncLog\":"+(p.Value.SyncLog.HasValue ? p.Value.SyncLog.Value.ToString() : "null")+",\"announceLog\":"+(p.Value.AnnounceLog.HasValue ? p.Value.AnnounceLog.Value.ToString() : "null")+",");
                        sb.Append("\"gmId\":\""+(p.Value.GrandmasterId??"")+"\",\"vendor\":\""+GetVendorSafe(dev.Mac)+"\",");
                         sb.Append("\"isConflict\":"+(p.Value.IsConflict?"true":"false")+",");
                         sb.Append("\"conflictSeconds\":"+(p.Value.ConflictStartedAt.HasValue?(int)(DateTime.Now-p.Value.ConflictStartedAt.Value).TotalSeconds:0)+",");
                         sb.Append("\"lastMeasuredIntervalMs\":"+(p.Value.LastMeasuredIntervalMs.HasValue?((int)p.Value.LastMeasuredIntervalMs.Value).ToString():"null")+",");
                         sb.Append("\"parentIp\":\""+pip+"\",");
                        sb.Append("\"roleElapsedSeconds\":"+(p.Value.RoleChangedAt.HasValue?(int)(DateTime.Now-p.Value.RoleChangedAt.Value).TotalSeconds:-1)+"}");
                    }
                    sb.Append("}}");
                }
                sb.Append("],\"logs\":["); lock(logLock) {
                    for(int i=0; i<logs.Count; i++) sb.Append((i>0?",":"")+"\""+logs[i].Replace("\"","\\\"")+"\"");
                } sb.Append("]}");
            }
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString()); res.ContentType="application/json"; res.ContentLength64=b.Length; res.OutputStream.Write(b,0,b.Length);
        } else {
            byte[] b = Encoding.UTF8.GetBytes(HtmlContent.Replace("{PORT}", WebPort)); res.OutputStream.Write(b,0,b.Length);
        }
        res.Close();
    }

    static readonly string HtmlHeader = @"<!DOCTYPE html><html><head><meta charset=""UTF-8""><title>PTPMonitor v1.6.8</title>
<link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;600&family=Outfit:wght@700&display=swap"" rel=""stylesheet"">";

    static readonly string HtmlStyle = @"<style>
:root{--bg:#0b0f19;--glass:rgba(255,255,255,0.03);--border:rgba(255,255,255,0.08);--accent:#00d2ff;--leader:#ff7090;--follower:#5de8b8;--offline:#4a5568;}
body{margin:0;padding:1.5rem;background:var(--bg);color:#e2e8f0;font-family:'Inter',sans-serif;}
.container{max-width:1400px;margin:0 auto;}
header{display:flex;justify-content:space-between;align-items:center;margin-bottom:1.5rem;}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:1.5rem;margin-bottom:1.5rem;}
.glass{background:var(--glass);backdrop-filter:blur(20px);border:1px solid var(--border);border-radius:16px;padding:1.5rem;}
.node{background:rgba(255,255,255,0.02);border:1px solid var(--border);border-radius:12px;padding:1rem;margin-bottom:0.8rem;border-left:4px solid var(--offline);}
.node.leader{border-left-color:var(--leader);} .node.follower{border-left-color:var(--follower);} .node.offline{opacity:0.5;}
.mac{font-family:monospace;font-weight:bold;font-size:1.1rem;}
.role-badge{font-size:0.7rem;font-weight:600;padding:2px 6px;border-radius:6px;text-transform:none;margin-left:5px;}
.role-badge.leader{background:rgba(255,112,144,0.15);color:var(--leader);}
.role-badge.follower{background:rgba(93,232,184,0.15);color:var(--follower);}
.info-row{font-size:0.75rem;color:#aaa;margin-top:4px;}
.logs{height:200px;overflow-y:auto;font-family:monospace;font-size:0.8rem;color:#888;}
.role-badge.conflict{background:rgba(255,50,50,0.3);color:#ff4a4a;border:1px solid #ff4a4a;}
.role-badge.bmca{background:rgba(255,200,0,0.2);color:#ffcc00;border:1px solid #ffcc00;}
.node.conflict{border:1px solid #ff4a4a;box-shadow:0 0 10px rgba(255,50,50,0.2);}
.node.bmca{border:1px solid #ffcc00;box-shadow:0 0 10px rgba(255,200,0,0.2);}
button{background:var(--glass);border:1px solid var(--border);color:#fff;padding:6px 12px;border-radius:6px;cursor:pointer;font-size:0.75rem;}
button:hover{border-color:var(--accent);}
.header-actions{display:flex;gap:8px;}
.dashboard{display:grid;grid-template-columns:repeat(5,1fr);gap:1.5rem;margin-bottom:1.5rem;}
.dash-item{background:var(--glass);padding:1rem;border-radius:12px;border:1px solid var(--border);font-size:0.8rem;}
.dash-value{font-size:1.8rem;font-weight:bold;margin-top:4px;}
.live-indicator{display:inline-flex;align-items:center;gap:6px;font-size:0.75rem;color:#aaa;background:rgba(255,255,255,0.05);padding:4px 10px;border-radius:20px;border:1px solid var(--border);}
.dot{width:8px;height:8px;background:var(--follower);border-radius:50%;box-shadow:0 0 8px var(--follower);animation:pulse 2s infinite;}
@keyframes pulse{0%{opacity:1;transform:scale(1);}50%{opacity:0.3;transform:scale(1.2);}100%{opacity:1;transform:scale(1);}}
</style>";

    static readonly string HtmlBody = @"</head><body><div class=""container"">
<header>
    <div>
        <h1 style=""margin:0"">PTP Monitor <small style=""font-size:0.5em;opacity:0.5"">v1.6.8</small></h1>
        <div class=""live-indicator"" style=""margin-top:8px"">
            <span class=""dot""></span>
            <span>LIVE MONITORING</span>
            <span style=""margin-left:8px;opacity:0.6"">|</span>
            <span id=""last-update"" style=""margin-left:8px"">Waiting for data...</span>
        </div>
    </div>
    <div class=""header-actions"">
        <button onclick=""if(confirm('Reset all network data and logs?')) { fetch('/api/clear_all',{method:'POST'}); fetchUI(); }"">\u21BB Network Clear</button>
        <button onclick=""if(confirm('Clear all offline devices from the list?')) { fetch('/api/clear_offline',{method:'POST'}); fetchUI(); }"">\U0001F5D1 Clear Offline</button>
        <button onclick=""exportCSV()"">" + "\u2B07" + @" Export CSV</button>
    </div>
</header>
<div class=""dashboard"" id=""dash""></div>
<div class=""grid"">
<div class=""glass""><h3>" + "\U0001F4E1" + @" v1 Topology</h3><div id=""v1""></div></div>
<div class=""glass""><h3>" + "\U0001F4E1" + @" v2 Topology</h3><div id=""v2""></div></div>
</div>
<div class=""glass""><h3>Event Logs</h3><div id=""l"" class=""logs""></div></div>
</div>";

    static readonly string HtmlScripts = @"<script>
function esc(t){ if(t === 0) return '0'; if(!t) return ''; return String(t).replace(/[&<>/""']/g, s=>({'&':'&amp;','<':'&lt;','>':'&gt;','/':'&#47;','""':'&quot;',""'"":""&#39;""}[s])); }
function valS(v) { return v === null ? 'N/A' : v; }
function hhmmss(s) { if(s<0)return'00:00:00'; let h=Math.floor(s/3600),m=Math.floor((s%3600)/60),sec=Math.floor(s%60); return String(h).padStart(2,'0')+':'+String(m).padStart(2,'0')+':'+String(sec).padStart(2,'0'); }

async function fetchUI() {
    try {
        const r = await fetch('/api/data'); const d = await r.json();
        const counts = { nodes: 0, leaders: 0, bcs: 0, followers: 0, conflicts: 0, v1f: 0, v2f: 0 };
        
        d.devices.forEach(dev => {
            if(!dev.online) return;
            counts.nodes++;
            let isL = false; let isF = false;
            const domains = new Set();
            Object.values(dev.protocols).forEach(p => { 
                if(p.role==='Leader') isL=true; 
                if(p.role==='Follower') { isF=true; if(dev.protocols.v1) counts.v1f++; else counts.v2f++; }
                if(p.isConflict) counts.conflicts++; 
                if(p.role !== 'Unknown') domains.add(p.domain);
            });
            if(isL) counts.leaders++;
            if((dev.protocols.v1 && dev.protocols.v2) || domains.size > 1) { dev.isBc = true; counts.bcs++; }
            if(isF) counts.followers++;
        });

        const stats = [['Active Nodes', counts.nodes], ['Leaders', counts.leaders], ['v1/v2 Followers', `${counts.v1f}/${counts.v2f}`], ['Boundary Clocks (bc)', counts.bcs], ['Conflicts', counts.conflicts]];
        document.getElementById('dash').innerHTML = stats.map(s => `<div class=""dash-item""><div style=""color:#aaa"">${s[0]}</div><div class=""dash-value"">${s[1]}</div></div>`).join('');
        document.getElementById('last-update').innerText = 'Last Update: ' + new Date().toLocaleTimeString();

        ['v1', 'v2'].forEach(v => {
            const domains = [...new Set(d.devices.map(dev => dev.protocols[v]?.domain).filter(x => x !== undefined))].sort();
            let html = '';
            
            domains.forEach(dom => {
                html += `<div style=""margin-top:1.5rem;margin-bottom:1rem;border-bottom:1px solid var(--border);padding-bottom:5px;color:var(--accent);font-weight:bold;font-size:0.9rem;display:flex;align-items:center;gap:10px;"">
                    <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""10""/><path d=""M12 8v8M8 12h8""/></svg>
                    Domain ${esc(dom)}
                </div>`;

                const domainNodes = d.devices.filter(dev => dev.protocols[v] && dev.protocols[v].domain === dom);
                const cmap = {}; const roots = [];

                domainNodes.forEach(dev => {
                    const p = dev.protocols[v];
                    const parentExists = p.parentIp && domainNodes.some(dn => dn.ip === p.parentIp);
                    if (p.role === 'Leader' || !p.parentIp || !parentExists) roots.push(dev);
                    else { if(!cmap[p.parentIp]) cmap[p.parentIp]=[]; cmap[p.parentIp].push(dev); }
                });

                function render(dev, depth, parentGmId) {
                    const p = dev.protocols[v];
                    const role = p.role.toLowerCase();
                    const isMismatch = (role === 'follower' && parentGmId && p.gmId && p.gmId !== parentGmId);
                    const isPersistent = p.conflictSeconds >= 10;
                    const isConflict = p.conflictSeconds > 0 ? (isPersistent ? 'conflict' : 'bmca') : '';
                    const color = role === 'leader' ? '#ff7090' : (role === 'follower' ? '#5de8b8' : '#e2e8f0');
                    const indent = depth * 25;
                    const arrow = depth > 0 ? `<span style=""color:#5de8b8;margin-right:6px;"">\u21B3</span>` : '';
                    
                    let gmIdText = p.gmId || (role === 'follower' ? parentGmId : 'N/A');
                    let gmStyle = isMismatch ? 'color:#ff4a4a;font-weight:bold;background:rgba(255,50,50,0.1);padding:2px 4px;border-radius:4px;' : 'opacity:0.9;';
                    let gmLine = `<div class=""info-row"" style=""${gmStyle}margin-top:6px;font-family:monospace;"">GM: ${esc(gmIdText)}${isMismatch?' <span style=""margin-left:4px;"">\u26A0 GM Mismatch!</span>':''}</div>`;
                    let logLine = (role === 'leader' && v === 'v2') ? `<div class=""info-row"" style=""opacity:0.7"">Sync: ${valS(p.syncLog)} / Announce: ${valS(p.announceLog)}</div>` : '';
                    
                    let badgeText = esc(p.role);
                    if (isConflict === 'bmca') badgeText = 'BMCA (Negotiating)';
                    else if (isPersistent) badgeText = 'CONFLICT (Persistent)';

                    let res = `<div class=""node ${role} ${isConflict} ${isMismatch?'conflict':''} ${dev.online?'':'offline'}"" style=""margin-left:${indent}px"">
                        <div style=""display:flex;justify-content:space-between"">
                            <div class=""mac"" style=""color:${color}"">${arrow}${esc(dev.ip)}</div>
                            <span>
                                ${isMismatch?'<span class=""role-badge conflict"">MISMATCH</span>':''}
                                <span class=""role-badge ${isConflict || role}"">${badgeText}${dev.isBc?' (BC)':''}</span>
                            </span>
                        </div>
                        <div class=""info-row"">MAC: ${esc(dev.mac)} | Vendor: ${esc(p.vendor || 'Unknown')}</div>
                        <div class=""info-row"" style=""color:${dev.online?'var(--follower)':'var(--leader)'};opacity:0.8"">
                            ${dev.online ? 'Uptime: '+hhmmss(dev.uptimeSeconds) : 'Offline: '+hhmmss(dev.idleSeconds)}
                            ${p.lastMeasuredIntervalMs ? ` | Delay Intv: <span style=""${p.lastMeasuredIntervalMs > (d.expectedDelayInterval * d.delayAlertThresholdRate * 1000) ? 'color:#ff4a4a;font-weight:bold' : ''}"">${(p.lastMeasuredIntervalMs/1000.0).toFixed(2)} s</span>` : ''}
                        </div>
                        ${gmLine}
                        ${logLine}
                    </div>`;
                    (cmap[dev.ip] || []).forEach(c => res += render(c, depth + 1, p.gmId));
                    return res;
                }

                let domainHtml = '';
                roots.forEach(r => domainHtml += render(r, 0, null));
                html += domainHtml || '<div style=""padding:1rem;color:#666;font-size:0.8rem"">No nodes in this domain</div>';
            });
            document.getElementById(v).innerHTML = html || '<div style=""padding:2rem;text-align:center;color:#666"">No PTP data detected</div>';
        });
        document.getElementById('l').innerHTML = d.logs.map(log=>`<div>${esc(log)}</div>`).reverse().join('');
    } catch(e) { console.error(e); }
    setTimeout(fetchUI, 2000);
}

function exportCSV(){
    fetch('/api/data').then(r=>r.json()).then(d=>{
        let csv = 'IP,MAC,Vendor,Online,v1_Role,v1_Domain,v2_Role,v2_Domain,Uptime,Idle\n';
        d.devices.forEach(dev => {
            const v1 = dev.protocols.v1 || {}; const v2 = dev.protocols.v2 || {};
            csv += `""${dev.ip}"",""${dev.mac}"",""${v1.vendor||v2.vendor||'-'}"",${dev.online},""${v1.role||'-'}"",""${v1.domain||'-'}"",""${v2.role||'-'}"",""${v2.domain||'-'}"",${hhmmss(dev.uptimeSeconds)},${hhmmss(dev.idleSeconds)}\n`;
        });
        const blob = new Blob([csv], { type: 'text/csv' });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a'); a.style.display = 'none'; a.href = url;
        a.download = `ptp_monitor_export_${new Date().getTime()}.csv`;
        document.body.appendChild(a); a.click(); window.URL.revokeObjectURL(url);
    });
}
fetchUI();
</script></body></html>";

    static readonly string HtmlContent = HtmlHeader + HtmlStyle + HtmlBody + HtmlScripts;
}
