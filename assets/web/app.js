function esc(t){ if(t === 0) return '0'; if(!t) return ''; return String(t).replace(/[&<>/"']/g, s=>({'&':'&amp;','<':'&lt;','>':'&gt;','/':'&#47;','"':'&quot;',"'":"&#39;"}[s])); }
function valS(v) { return v === null ? 'N/A' : v; }
function hhmmss(s) { if(s<0)return'00:00:00'; let h=Math.floor(s/3600),m=Math.floor((s%3600)/60),sec=Math.floor(s%60); return String(h).padStart(2,'0')+':'+String(m).padStart(2,'0')+':'+String(sec).padStart(2,'0'); }
function simpleHash(s) { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36); }
// A sanitized-only id can collide (e.g. domains "A-B" and "A_B" both become "A_B"); the raw-string
// hash suffix disambiguates them so an alert's click-to-scroll always lands on the right node.
function nodeElId(ip, ver, dom) { const raw = ip + '|' + ver + '|' + dom; return 'node_' + raw.replace(/[^a-zA-Z0-9_]/g, '_') + '_' + simpleHash(raw); }

// Labels for Announce enumerations. ONLY values confirmed against Wireshark's PTP dissector tables
// (ptp_v2_clockAccuracy_vals / ptp_v2_timeSource_vals) are named; anything else is shown as its raw
// value marked "unrecognized" rather than guessed. clockClass has no table there, so it stays numeric.
const ACCURACY_LABELS = {
    0x17:'1 ps',0x18:'2.5 ps',0x19:'10 ps',0x1A:'25 ps',0x1B:'100 ps',0x1C:'250 ps',
    0x1D:'1 ns',0x1E:'2.5 ns',0x1F:'10 ns',0x20:'25 ns',0x21:'100 ns',0x22:'250 ns',
    0x23:'1 µs',0x24:'2.5 µs',0x25:'10 µs',0x26:'25 µs',0x27:'100 µs',0x28:'250 µs',
    0x29:'1 ms',0x2A:'2.5 ms',0x2B:'10 ms',0x2C:'25 ms',0x2D:'100 ms',0x2E:'250 ms',
    0x2F:'1 s',0x30:'10 s'
};
const TIMESOURCE_LABELS = { 0x10:'Atomic clock',0x20:'GPS',0x30:'Terrestrial radio',0x39:'Serial time code',0x40:'PTP',0x50:'NTP',0x60:'Hand set',0x90:'Other',0xA0:'Internal oscillator' };
function hex2(v) { return '0x' + v.toString(16).toUpperCase().padStart(2, '0'); }
function accuracyText(v) {
    if (v === null || v === undefined) return 'N/A';
    if (ACCURACY_LABELS[v]) return `≤ ${ACCURACY_LABELS[v]} (${hex2(v)})`;
    if (v === 0x31) return `> 10 s (${hex2(v)})`;
    if (v === 0x80) return `alternate profile (${hex2(v)})`;
    if (v === 0xFE) return `unknown (${hex2(v)})`;
    return `${hex2(v)} (unrecognized)`;
}
function timeSourceText(v) {
    if (v === null || v === undefined) return 'N/A';
    return TIMESOURCE_LABELS[v] ? `${TIMESOURCE_LABELS[v]} (${hex2(v)})` : `${hex2(v)} (unrecognized)`;
}
// The flags are shown positively: a ✗ is "not set", which is not by itself an error.
// A missing field (null, or undefined if the API omits it) must render as N/A and never throw: an
// exception here would abort the whole poll's rendering.
function numText(v) { return (v === null || v === undefined) ? 'N/A' : String(v); }
function varianceText(v) { return Number.isInteger(v) ? '0x' + v.toString(16).toUpperCase().padStart(4, '0') : 'N/A'; }
function timePropsText(tp, utcOffset) {
    const off = Number.isInteger(utcOffset) ? `${utcOffset > 0 ? '+' : ''}${utcOffset} s` : 'N/A';
    const parts = [`UTC offset ${off} (${tp.utcValid ? 'valid' : 'NOT valid'})`,
        tp.ptpTimescale ? 'PTP timescale' : 'ARB timescale',
        `time-traceable ${tp.timeTraceable ? '✓' : '✗'}`,
        `freq-traceable ${tp.freqTraceable ? '✓' : '✗'}`];
    if (tp.leap61) parts.push('leap61');
    if (tp.leap59) parts.push('leap59');
    return parts.join(' / ');
}

// <profileHint>
// A HINT about which PTP profile a domain number matches, never a verdict: the domain number is set by the
// user and several profiles share defaults (0 is common). Only values confirmed from vendor/standards-body
// documentation are listed (SMPTE ST 2059-2 default 127; AES67 default 0; ITU-T G.8275.1 range 24-43,
// G.8275.2 range 44-63). The IEEE 1588 default profile's default domain and Dante's PTPv1 subdomain names
// are NOT confirmed, so they are not interpreted. Returns null when there is nothing safe to say.
function profileHint(version, domain) {
    if (version === 'v1') return "PTPv1 (IEEE 1588-2002): Dante's default clocking";
    if (version !== 'v2' || !/^\d+$/.test(String(domain))) return null;
    const n = Number(domain);
    if (n === 127) return 'SMPTE ST 2059-2 default domain';
    if (n === 0) return 'AES67 default domain (also used by other profiles)';
    if (n >= 24 && n <= 43) return 'within the ITU-T G.8275.1 range (default 24)';
    if (n >= 44 && n <= 63) return 'within the ITU-T G.8275.2 range (default 44)';
    return null;
}
// </profileHint>

let logFilterLevel = 'ALL';
let lastLogs = [];
const LOG_TAG_MAP = { ERROR: '[ERROR]', WARN: '[WARN]', CONFLICT: '[CONFLICT_ALERT]', MISMATCH: '[GM_MISMATCH]', DELAY: '[DELAY_MECH_MISMATCH]', ANNOUNCE: '[ANNOUNCE_TIMEOUT]', INTERVAL: '[INTERVAL_MISMATCH]' };
function setLogFilter(level) {
    logFilterLevel = level;
    document.querySelectorAll('#logFilters button').forEach(b => b.classList.toggle('active', b.dataset.level === level));
    renderLogs(lastLogs);
}
function renderLogs(logsArr) {
    lastLogs = logsArr;
    const tag = LOG_TAG_MAP[logFilterLevel];
    const filtered = !tag ? logsArr : logsArr.filter(l => l.indexOf(tag) !== -1);
    document.getElementById('l').innerHTML = filtered.map(log=>`<div>${esc(log)}</div>`).reverse().join('');
}

// Open/closed state of each domain's "GM changes" <details>, keyed by "version|domain". 'toggle' does
// not bubble, so it is caught in the capture phase on document (the elements themselves are rebuilt
// every poll and cannot hold their own listeners).
const historyOpen = new Set();
document.addEventListener('toggle', e => {
    const t = e.target;
    if (!t || !t.classList || !t.classList.contains('gm-history')) return;
    if (t.open) historyOpen.add(t.dataset.key); else historyOpen.delete(t.dataset.key);
}, true);

function applySearchFilter() {
    const term = document.getElementById('searchBox').value.trim().toLowerCase();
    document.querySelectorAll('.node[data-search]').forEach(el => {
        el.style.display = (!term || el.dataset.search.indexOf(term) !== -1) ? '' : 'none';
    });
}

async function fetchUI() {
    try {
        const r = await fetch('/api/data'); const d = await r.json();
        const counts = { nodes: 0, leaders: 0, bcs: 0, conflicts: 0, v1f: 0, v2f: 0 };

        d.devices.forEach(dev => {
            if(!dev.online) return;
            counts.nodes++;
            let isL = false;
            const domainsByVersion = {};
            const versionsActive = new Set();
            dev.protocols.forEach(p => {
                // A device stays in dev.protocols after one of its instances goes stale (p.online
                // false) until retention removes the whole device, so stats must only count
                // instances that are actually live, not just devices that are live overall.
                if(!p.online) return;
                if(p.role==='Leader') isL=true;
                if(p.role==='Follower') { if(p.version==='v1') counts.v1f++; else counts.v2f++; }
                if(p.isConflict) counts.conflicts++;
                if(p.role !== 'Unknown') {
                    versionsActive.add(p.version);
                    if(!domainsByVersion[p.version]) domainsByVersion[p.version] = new Set();
                    domainsByVersion[p.version].add(p.domain);
                }
            });
            if(isL) counts.leaders++;
            const multiDomain = Object.keys(domainsByVersion).some(v => domainsByVersion[v].size > 1);
            if(versionsActive.size > 1 || multiDomain) { dev.isBc = true; counts.bcs++; }
        });

        const stats = [['Active Nodes', counts.nodes], ['Leaders', counts.leaders], ['v1/v2 Followers', `${counts.v1f}/${counts.v2f}`], ['Boundary Clocks (bc)', counts.bcs], ['Conflicts', counts.conflicts]];
        document.getElementById('dash').innerHTML = stats.map(s => `<div class="dash-item"><div style="color:#aaa">${s[0]}</div><div class="dash-value">${s[1]}</div></div>`).join('');
        document.getElementById('last-update').innerText = 'Last Update: ' + new Date().toLocaleTimeString();

        const alerts = [];
        const mechAlerted = new Set(); // one banner entry per mixed (version, domain), not per node
        const intervalAlerted = new Set(); // likewise for differing Announce intervals

        ['v1', 'v2'].forEach(v => {
            const instances = [];
            d.devices.forEach(dev => dev.protocols.forEach(p => { if (p.version === v) instances.push({dev: dev, p: p}); }));
            const domains = [...new Set(instances.map(i => i.p.domain))].sort();
            let html = '';

            domains.forEach(dom => {
                const hint = profileHint(v, dom);
                const hintHtml = hint ? `<span class="profile-hint" title="A hint from the domain number only, not a detection result">hint: ${esc(hint)}</span>` : '';
                html += `<div style="margin-top:1.5rem;margin-bottom:1rem;border-bottom:1px solid var(--border);padding-bottom:5px;color:var(--accent);font-weight:bold;font-size:0.9rem;display:flex;align-items:center;gap:10px;">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M12 8v8M8 12h8"/></svg>
                    Domain ${esc(dom)}${hintHtml}
                </div>`;

                // Recent GM changes for this (version, domain), newest first. Collapsed by default so a
                // quiet network shows no extra noise; the summary carries the count.
                const hist = (d.gmHistory || []).find(h => h.version === v && h.domain === dom);
                if (hist && hist.events.length > 0) {
                    // The whole domain block is re-rendered on every poll, which would otherwise snap an
                    // opened <details> shut; its open state is kept in historyOpen and re-applied here.
                    const hkey = v + '|' + dom;
                    html += `<details class="gm-history" data-key="${esc(hkey)}"${historyOpen.has(hkey) ? ' open' : ''}><summary>GM changes (${hist.events.length}) — last settled GM: <span style="font-family:monospace">${esc(hist.settledGm)}</span></summary>` +
                        hist.events.slice().reverse().map(e =>
                            `<div class="info-row" style="font-family:monospace">${esc(e.time)} ${esc(e.oldGm)} → ${esc(e.newGm)}</div>`
                        ).join('') + `</details>`;
                }

                const domainNodes = instances.filter(i => i.p.domain === dom);
                const cmap = {}; const roots = []; const rendered = new Set();

                domainNodes.forEach(inst => {
                    const p = inst.p;
                    const parentExists = p.parentIp && domainNodes.some(dn => dn.dev.ip === p.parentIp);
                    if (p.role === 'Leader' || !p.parentIp || !parentExists) roots.push(inst);
                    else { if(!cmap[p.parentIp]) cmap[p.parentIp]=[]; cmap[p.parentIp].push(inst); }
                });

                function render(inst, depth, parentGmId) {
                    const dev = inst.dev, p = inst.p;
                    if (rendered.has(dev.ip)) return ''; // Guard against circular parent links
                    rendered.add(dev.ip);
                    const role = p.role.toLowerCase();
                    // Only flag a mismatch when the parent link is Delay_Resp-confirmed and this
                    // instance is currently live -- comparing against an inferred/unconfirmed fallback
                    // parent, or a stale offline instance, would misrepresent the backend's own
                    // (link-confirmed-only) mismatch semantics.
                    const isMismatch = (role === 'follower' && p.online && p.linkConfirmed && parentGmId && p.gmId && p.gmId !== parentGmId);
                    const isPersistent = p.conflictSeconds >= 10;
                    const isConflict = p.conflictSeconds > 0 ? (isPersistent ? 'conflict' : 'bmca') : '';
                    const color = role === 'leader' ? '#ff7090' : (role === 'follower' ? '#5de8b8' : '#e2e8f0');
                    const indent = depth * 25;
                    const arrow = depth > 0 ? `<span style="color:#5de8b8;margin-right:6px;">↳</span>` : '';
                    const nodeId = nodeElId(dev.ip, p.version, p.domain);
                    const linkUnconfirmed = (role === 'follower' && p.parentIp && !p.linkConfirmed);

                    // A Follower's own GM is only known when it has itself relayed an Announce/Sync
                    // (e.g. acting as a Boundary Clock on another port). For an ordinary Follower that
                    // only sends Delay_Req, p.gmId is never observed, so the value shown is inherited
                    // from the parent and NOT independently confirmed -- label it as such rather than
                    // implying mismatch detection is active.
                    const gmInferred = (role === 'follower' && !p.gmId && !!parentGmId);
                    let gmIdText = p.gmId || (role === 'follower' ? parentGmId : 'N/A');
                    let gmStyle = isMismatch ? 'color:#ff4a4a;font-weight:bold;background:rgba(255,50,50,0.1);padding:2px 4px;border-radius:4px;' : 'opacity:0.9;';
                    let gmLine = `<div class="info-row" style="${gmStyle}margin-top:6px;font-family:monospace;">GM: ${esc(gmIdText)}${gmInferred?' <span class="unconfirmed-note">(inherited, unverified)</span>':''}${(role === 'leader' && !p.gmVerified)?' <span class="unconfirmed-note">(own ID, awaiting GM report)</span>':''}${isMismatch?' <span style="margin-left:4px;">⚠ GM Mismatch!</span>':''}</div>`;
                    let logLine = (role === 'leader' && v === 'v2') ? `<div class="info-row" style="opacity:0.7">Sync: ${valS(p.syncLog)} / Announce: ${valS(p.announceLog)}</div>` : '';
                    let bmcLine = (role === 'leader' && v === 'v2' && p.gmPriority1 !== null) ? `<div class="info-row" style="opacity:0.7">BMC: P1=${valS(p.gmPriority1)} / Class=${valS(p.gmClass)} / P2=${valS(p.gmPriority2)}</div>` : '';

                    // Remaining Announce fields (v2 Leaders that have sent an Announce). Raw numbers are always
                    // shown; ClockClass has no verified meaning table, so it is not labelled.
                    const hasAnnounce = (role === 'leader' && v === 'v2' && p.timeProps);
                    const qualLine = hasAnnounce ? `<div class="info-row" style="opacity:0.7" title="clockAccuracy / offsetScaledLogVariance / timeSource / stepsRemoved from the last Announce">Clock: Accuracy ${esc(accuracyText(p.gmAccuracy))} / Variance ${esc(varianceText(p.gmVariance))} / Source ${esc(timeSourceText(p.timeSource))} / Steps removed ${esc(numText(p.stepsRemoved))}</div>` : '';
                    const timeLine = hasAnnounce ? `<div class="info-row" style="opacity:0.7" title="currentUtcOffset and the Announce time-property flags">Time: ${esc(timePropsText(p.timeProps, p.utcOffset))}</div>` : '';

                    // Delay mechanism (E2E / P2P) is only reported for v2. An instance with no role that only
                    // does peer-delay is a P2P node, not "Unknown".
                    const mechLabels = { 'E2E': 'E2E (Delay_Req/Resp)', 'P2P': 'P2P (peer delay)', 'E2E+P2P': 'E2E + P2P ⚠' };
                    const mechLine = p.delayMech ? `<div class="info-row" style="${p.mechMismatch ? 'color:#ffcc00;opacity:1;' : 'opacity:0.7'}">Delay: ${esc(mechLabels[p.delayMech] || p.delayMech)}${p.mechMismatch ? ' <span style="margin-left:4px;">⚠ E2E/P2P mixed in this domain</span>' : ''}</div>` : '';

                    let badgeText = esc(p.role);
                    if (isConflict === 'bmca') badgeText = 'BMCA (Negotiating)';
                    else if (isPersistent) badgeText = 'CONFLICT (Persistent)';
                    else if (role === 'unknown' && p.delayMech === 'P2P') badgeText = 'P2P node';

                    if (isPersistent) alerts.push({ nodeId: nodeId, text: `Conflict: ${dev.ip} (${p.version}, domain ${p.domain})` });
                    if (isMismatch) alerts.push({ nodeId: nodeId, text: `GM Mismatch: ${dev.ip} (${p.version}, domain ${p.domain})` });
                    if (p.mechMismatch && !mechAlerted.has(v + '|' + p.domain)) {
                        mechAlerted.add(v + '|' + p.domain);
                        alerts.push({ nodeId: nodeId, warn: true, text: `Delay mechanism mismatch: E2E and P2P mixed in domain ${p.domain} (${p.version})` });
                    }
                    if (p.announceStale) alerts.push({ nodeId: nodeId, warn: true, text: `Announce timeout: ${dev.ip} (${p.version}, domain ${p.domain}) is still active but has stopped sending Announce` });
                    if (p.intervalMismatch && !intervalAlerted.has(v + '|' + p.domain)) {
                        intervalAlerted.add(v + '|' + p.domain);
                        alerts.push({ nodeId: nodeId, warn: true, text: `Announce interval mismatch: Leaders in domain ${p.domain} (${p.version}) use different Announce intervals` });
                    }

                    const nodeOnline = dev.online && p.online;
                    const searchBlob = esc((dev.ip + ' ' + dev.mac + ' ' + (p.vendor || '')).toLowerCase());
                    let res = `<div id="${nodeId}" class="node ${role} ${isConflict} ${isMismatch?'conflict':''} ${nodeOnline?'':'offline'} ${linkUnconfirmed?'unconfirmed-link':''} ${(p.mechMismatch || p.announceStale || p.intervalMismatch) ? 'mech-warn' : ''}" style="margin-left:${indent}px" data-search="${searchBlob}">
                        <div style="display:flex;justify-content:space-between">
                            <div class="mac" style="color:${color}">${arrow}${esc(dev.ip)}</div>
                            <span>
                                ${isMismatch?'<span class="role-badge conflict">MISMATCH</span>':''}
                                ${p.mechMismatch?'<span class="role-badge bmca">DELAY MIX</span>':''}
                                ${p.announceStale?'<span class="role-badge bmca">NO ANNOUNCE</span>':''}
                                ${p.intervalMismatch?'<span class="role-badge bmca">INTERVAL</span>':''}
                                <span class="role-badge ${isConflict || role}">${badgeText}${dev.isBc?' (BC)':''}</span>
                            </span>
                        </div>
                        <div class="info-row">MAC: ${esc(dev.mac)} | Vendor: ${esc(p.vendor || 'Unknown')}${linkUnconfirmed?' <span class="unconfirmed-note">(parent unconfirmed)</span>':''}</div>
                        <div class="info-row" style="color:${dev.online?'var(--follower)':'var(--leader)'};opacity:0.8">
                            ${dev.online ? 'Uptime: '+hhmmss(dev.uptimeSeconds) : 'Offline: '+hhmmss(dev.idleSeconds)}
                            ${p.lastMeasuredIntervalMs ? ` | Delay Intv: <span style="${p.lastMeasuredIntervalMs > (d.expectedDelayInterval * d.delayAlertThresholdRate * 1000) ? 'color:#ff4a4a;font-weight:bold' : ''}">${(p.lastMeasuredIntervalMs/1000.0).toFixed(2)} s</span>` : ''}
                        </div>
                        ${gmLine}
                        ${logLine}
                        ${bmcLine}
                        ${qualLine}
                        ${timeLine}
                        ${mechLine}
                    </div>`;
                    (cmap[dev.ip] || []).forEach(c => res += render(c, depth + 1, p.gmId));
                    return res;
                }

                let domainHtml = '';
                roots.forEach(r => domainHtml += render(r, 0, null));
                // Orphans (unreachable via roots, e.g. circular parent refs after a leader loss) still get shown
                domainNodes.forEach(inst => { if (!rendered.has(inst.dev.ip)) domainHtml += render(inst, 0, null); });
                html += domainHtml || '<div style="padding:1rem;color:#666;font-size:0.8rem">No nodes in this domain</div>';
            });
            document.getElementById(v).innerHTML = html || '<div style="padding:2rem;text-align:center;color:#666">No PTP data detected</div>';
        });

        const bannerEl = document.getElementById('alertBanner');
        bannerEl.innerHTML = alerts.length === 0 ? '' :
            `<div class="alert-banner"><strong>⚠ Active Alerts (${alerts.length})</strong>` +
            alerts.map(a => `<div class="alert-item${a.warn ? ' warn' : ''}" onclick="document.getElementById('${a.nodeId}') && document.getElementById('${a.nodeId}').scrollIntoView({behavior:'smooth',block:'center'})">${esc(a.text)}</div>`).join('') +
            `</div>`;

        renderLogs(d.logs);
        applySearchFilter();
    } catch(e) { console.error(e); }
}

// Periodic polling lives ONLY here; button handlers call fetchUI() directly for a
// one-shot refresh so clicking cannot spawn additional polling loops.
async function pollLoop() {
    await fetchUI();
    setTimeout(pollLoop, 2000);
}

function csvEsc(v) {
    let s = String(v === null || v === undefined ? '' : v).replace(/"/g, '""');
    // Neutralize spreadsheet formula injection: check after stripping leading whitespace/control
    // characters, since some spreadsheet apps still evaluate a formula preceded by e.g. a tab.
    if (/^[\s\x00-\x1F]*[=+\-@]/.test(s)) s = "'" + s;
    return '"' + s + '"';
}

function exportCSV(){
    fetch('/api/data').then(r=>r.json()).then(d=>{
        let csv = 'IP,MAC,Vendor,Online,v1_Role,v1_Domain,v2_Role,v2_Domain,Uptime,Idle\n';
        d.devices.forEach(dev => {
            const v1 = dev.protocols.filter(p=>p.version==='v1');
            const v2 = dev.protocols.filter(p=>p.version==='v2');
            const vendor = (v1[0] || v2[0] || {}).vendor || '-';
            const cols = [dev.ip, dev.mac, vendor, dev.online, v1.map(p=>p.role).join('/')||'-', v1.map(p=>p.domain).join('/')||'-', v2.map(p=>p.role).join('/')||'-', v2.map(p=>p.domain).join('/')||'-', hhmmss(dev.uptimeSeconds), hhmmss(dev.idleSeconds)];
            csv += cols.map(csvEsc).join(',') + '\n';
        });
        // Prepend a UTF-8 BOM: without it, older Excel versions mis-detect the encoding and
        // mangle non-ASCII vendor names instead of reading them as UTF-8.
        const blob = new Blob(['﻿' + csv], { type: 'text/csv' });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a'); a.style.display = 'none'; a.href = url;
        a.download = `ptp_monitor_export_${new Date().getTime()}.csv`;
        document.body.appendChild(a); a.click(); window.URL.revokeObjectURL(url);
    });
}
pollLoop();
