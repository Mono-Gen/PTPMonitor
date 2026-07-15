function esc(t){ if(t === 0) return '0'; if(!t) return ''; return String(t).replace(/[&<>/"']/g, s=>({'&':'&amp;','<':'&lt;','>':'&gt;','/':'&#47;','"':'&quot;',"'":"&#39;"}[s])); }
function valS(v) { return v === null ? 'N/A' : v; }
function hhmmss(s) { if(s<0)return'00:00:00'; let h=Math.floor(s/3600),m=Math.floor((s%3600)/60),sec=Math.floor(s%60); return String(h).padStart(2,'0')+':'+String(m).padStart(2,'0')+':'+String(sec).padStart(2,'0'); }
function simpleHash(s) { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36); }
// A sanitized-only id can collide (e.g. domains "A-B" and "A_B" both become "A_B"); the raw-string
// hash suffix disambiguates them so an alert's click-to-scroll always lands on the right node.
function nodeElId(ip, ver, dom) { const raw = ip + '|' + ver + '|' + dom; return 'node_' + raw.replace(/[^a-zA-Z0-9_]/g, '_') + '_' + simpleHash(raw); }

let logFilterLevel = 'ALL';
let lastLogs = [];
const LOG_TAG_MAP = { ERROR: '[ERROR]', WARN: '[WARN]', CONFLICT: '[CONFLICT_ALERT]', MISMATCH: '[GM_MISMATCH]' };
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

        ['v1', 'v2'].forEach(v => {
            const instances = [];
            d.devices.forEach(dev => dev.protocols.forEach(p => { if (p.version === v) instances.push({dev: dev, p: p}); }));
            const domains = [...new Set(instances.map(i => i.p.domain))].sort();
            let html = '';

            domains.forEach(dom => {
                html += `<div style="margin-top:1.5rem;margin-bottom:1rem;border-bottom:1px solid var(--border);padding-bottom:5px;color:var(--accent);font-weight:bold;font-size:0.9rem;display:flex;align-items:center;gap:10px;">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M12 8v8M8 12h8"/></svg>
                    Domain ${esc(dom)}
                </div>`;

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
                    let gmLine = `<div class="info-row" style="${gmStyle}margin-top:6px;font-family:monospace;">GM: ${esc(gmIdText)}${gmInferred?' <span class="unconfirmed-note">(inherited, unverified)</span>':''}${isMismatch?' <span style="margin-left:4px;">⚠ GM Mismatch!</span>':''}</div>`;
                    let logLine = (role === 'leader' && v === 'v2') ? `<div class="info-row" style="opacity:0.7">Sync: ${valS(p.syncLog)} / Announce: ${valS(p.announceLog)}</div>` : '';
                    let bmcLine = (role === 'leader' && v === 'v2' && p.gmPriority1 !== null) ? `<div class="info-row" style="opacity:0.7">BMC: P1=${valS(p.gmPriority1)} / Class=${valS(p.gmClass)} / P2=${valS(p.gmPriority2)}</div>` : '';

                    let badgeText = esc(p.role);
                    if (isConflict === 'bmca') badgeText = 'BMCA (Negotiating)';
                    else if (isPersistent) badgeText = 'CONFLICT (Persistent)';

                    if (isPersistent) alerts.push({ nodeId: nodeId, text: `Conflict: ${dev.ip} (${p.version}, domain ${p.domain})` });
                    if (isMismatch) alerts.push({ nodeId: nodeId, text: `GM Mismatch: ${dev.ip} (${p.version}, domain ${p.domain})` });

                    const nodeOnline = dev.online && p.online;
                    const searchBlob = esc((dev.ip + ' ' + dev.mac + ' ' + (p.vendor || '')).toLowerCase());
                    let res = `<div id="${nodeId}" class="node ${role} ${isConflict} ${isMismatch?'conflict':''} ${nodeOnline?'':'offline'} ${linkUnconfirmed?'unconfirmed-link':''}" style="margin-left:${indent}px" data-search="${searchBlob}">
                        <div style="display:flex;justify-content:space-between">
                            <div class="mac" style="color:${color}">${arrow}${esc(dev.ip)}</div>
                            <span>
                                ${isMismatch?'<span class="role-badge conflict">MISMATCH</span>':''}
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
            alerts.map(a => `<div class="alert-item" onclick="document.getElementById('${a.nodeId}') && document.getElementById('${a.nodeId}').scrollIntoView({behavior:'smooth',block:'center'})">${esc(a.text)}</div>`).join('') +
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
        const blob = new Blob([csv], { type: 'text/csv' });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a'); a.style.display = 'none'; a.href = url;
        a.download = `ptp_monitor_export_${new Date().getTime()}.csv`;
        document.body.appendChild(a); a.click(); window.URL.revokeObjectURL(url);
    });
}
pollLoop();
