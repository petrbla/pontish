const post = (obj) => window.chrome.webview.postMessage(obj);

const state = {
  lang: 'en',
  theme: localStorage.getItem('pontish_theme') || 'light',
  langs: [],
  i18n: {},
  i18nFallback: {},
  hosts: [],
  sshConfigExists: true,
  persistPasswords: false,
  appVersion: '0.0.1',
  updateStatus: null,
  autoUpdateChecked: false,
  activeMainTab: 'home',
  activeSideTab: 'sessions',
  sessions: [], // { id, title, mode, hostAlias, term, fit, paneEl, sftpState }
  activeTermId: null,
  seq: 0,
  treeConn: null,
  treeRoot: '~',
  treeSelectedNode: null,
  suppressCwdSyncUntil: 0,
  waitingTerminalSudo: false,
  pendingRequests: new Map(),
  reqSeq: 0,
  modalResolve: null,
  bridgeManualExpanded: new Map(),
  bridgeLastActive: new Map(),
  cpuHistory: new Map()
};

const XTERM_THEME = {
  background: '#1e2430',
  foreground: '#e2e8f0',
  cursor: '#38bdf8',
  cursorAccent: '#1e2430',
  selectionBackground: 'rgba(92, 156, 255, 0.35)',
  black: '#283040',
  red: '#ff6b6b',
  green: '#69db7c',
  yellow: '#ffd43b',
  blue: '#5c9cff',
  magenta: '#c792ea',
  cyan: '#56d4dd',
  white: '#e2e8f0',
  brightBlack: '#64748b',
  brightRed: '#ff8787',
  brightGreen: '#8ce99a',
  brightYellow: '#ffe066',
  brightBlue: '#74c0fc',
  brightMagenta: '#da77f2',
  brightCyan: '#66d9e8',
  brightWhite: '#ffffff'
};

const ICON_SUN = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/></svg>';
const ICON_MOON = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>';

const applyTheme = (theme) => {
  state.theme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', state.theme);
  localStorage.setItem('pontish_theme', state.theme);
  const btn = document.getElementById('btnTheme');
  if (btn) btn.innerHTML = state.theme === 'light' ? ICON_MOON : ICON_SUN;
  post({ cmd: 'setTheme', theme: state.theme });
};

const t = (key, ...args) => {
  let s = state.i18n[key] ?? state.i18nFallback[key] ?? key;
  args.forEach((a, i) => { s = s.replace(`{${i}}`, a); });
  return s;
};

const appendLog = (msg) => {
  const el = document.getElementById('logOutput');
  const ts = new Date().toLocaleTimeString('cs-CZ', { hour12: false });
  el.textContent += `[${ts}] ${msg}\n`;
  el.parentElement.scrollTop = el.parentElement.scrollHeight;
};

const sendReq = (cmd, payload = {}) => new Promise((resolve) => {
  const reqId = `r-${++state.reqSeq}`;
  state.pendingRequests.set(reqId, resolve);
  post({ cmd, reqId, ...payload });
});

const openModal = ({ title, desc, isPassword = true, showRemember = true, okText = 'OK', hideInput = false }) => {
  return new Promise((resolve) => {
    state.modalResolve = resolve;
    document.getElementById('modalTitle').textContent = title;
    document.getElementById('modalDesc').textContent = desc;
    const inp = document.getElementById('modalInput');
    inp.style.display = hideInput ? 'none' : '';
    inp.type = isPassword ? 'password' : 'text';
    inp.value = '';
    document.getElementById('modalRememberWrap').style.display = showRemember ? 'inline-flex' : 'none';
    document.getElementById('modalOk').textContent = okText;
    document.getElementById('modalBackdrop').classList.add('open');
    if (!hideInput) setTimeout(() => inp.focus(), 50);
  });
};

const closeModal = (val) => {
  document.getElementById('modalBackdrop').classList.remove('open');
  if (state.modalResolve) {
    state.modalResolve(val);
    state.modalResolve = null;
  }
  const cur = state.sessions.find((x) => x.id === state.activeTermId);
  if (cur?.term) setTimeout(() => cur.term.focus(), 10);
};

document.getElementById('modalCancel').onclick = () => closeModal(null);
document.getElementById('modalOk').onclick = () => {
  closeModal({
    value: document.getElementById('modalInput').value,
    remember: document.getElementById('modalRemember').checked
  });
};
document.getElementById('modalInput').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') document.getElementById('modalOk').click();
  if (e.key === 'Escape') document.getElementById('modalCancel').click();
});

const applyI18n = () => {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    const k = el.getAttribute('data-i18n');
    if (state.i18n[k]) el.textContent = state.i18n[k];
  });

  const tunnels = state.hosts.filter((h) => h.hasForwards);
  const activeCount = tunnels.filter((h) => h.forwards.length > 0 && h.forwards.every((f) => f.isListening)).length;
  document.getElementById('lblTabBridges').textContent = t('TabBridges', activeCount);
  document.getElementById('lblTabTerminals').textContent = t('TabTerminals', state.sessions.length);
  document.getElementById('headerSummary').textContent = t('HeaderSummary', activeCount, tunnels.length);
  document.getElementById('shieldOrb').classList.toggle('active', activeCount > 0);
};

const renderLangSwitch = () => {
  const wrap = document.getElementById('langSwitch');
  wrap.innerHTML = '';
  const sel = document.createElement('select');
  sel.className = 'lang-select';
  sel.setAttribute('aria-label', 'Language');
  state.langs.forEach((l) => {
    const opt = document.createElement('option');
    opt.value = l.code;
    opt.textContent = `${l.code.toLowerCase()} - ${l.name.replace(/\s*\([^)]+\)\s*$/, '')}`;
    if (l.code === state.lang) opt.selected = true;
    sel.appendChild(opt);
  });
  sel.onchange = () => post({ cmd: 'setLang', lang: sel.value });
  wrap.appendChild(sel);
};

const renderBridges = () => {
  const grid = document.getElementById('bridgesGrid');
  grid.innerHTML = '';
  const tunnels = state.hosts.filter((h) => h.hasForwards);

  if (tunnels.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'home-card';
    empty.style.gridColumn = '1 / -1';
    empty.innerHTML = `
      <div class="home-card-title">${ICON_BRIDGE} ${t('BridgesEmpty')}</div>
      <div class="home-card-sub" style="margin-top:4px;">${!state.sshConfigExists ? t('CfgMissingBanner') : t('HomeBridgesSub')}</div>
      <div class="home-card-footer" style="margin-top:12px;display:flex;gap:8px;flex-wrap:wrap;">
        ${!state.sshConfigExists ? `<button class="btn btn-emerald" id="bridgesBtnCreateCfg" type="button">${t('CfgCreateBtn')}</button>` : ''}
        <button class="btn btn-blue" id="bridgesBtnOpenCfg" type="button">⚙ ${t('BtnEdit')} ~/.ssh/config</button>
      </div>
    `;
    grid.appendChild(empty);
    const crBtn = document.getElementById('bridgesBtnCreateCfg');
    if (crBtn) crBtn.onclick = () => post({ cmd: 'createSshConfig' });
    document.getElementById('bridgesBtnOpenCfg').onclick = () => openCfgEditor();
    return;
  }

  tunnels.forEach((h) => {
    const total = h.forwards.length;
    const listenCount = h.forwards.filter((f) => f.isListening).length;
    const isFull = total > 0 && listenCount === total;
    const isPartial = listenCount > 0 && listenCount < total;
    const isActive = h.userRequestedRun || h.isConnecting || isFull;

    if (!state.bridgeLastActive.has(h.hostAlias)) {
      state.bridgeLastActive.set(h.hostAlias, isActive);
      state.bridgeManualExpanded.set(h.hostAlias, false);
    } else if (state.bridgeLastActive.get(h.hostAlias) !== isActive) {
      state.bridgeLastActive.set(h.hostAlias, isActive);
      state.bridgeManualExpanded.set(h.hostAlias, isActive);
    }
    const isExpanded = !!state.bridgeManualExpanded.get(h.hostAlias);

    const card = document.createElement('div');
    card.className = `bridge-card ${isFull ? 'running' : ''} ${isExpanded ? '' : 'collapsed'}`.trim();

    let statusText = t('BadgeStopped');
    let statusCls = '';
    let btnText = `▶ ${t('BtnStartBridge')}`;
    let btnCls = 'btn-emerald';

    if (isFull) {
      statusText = t('BadgeRunning', listenCount, total);
      statusCls = 'running';
      btnText = `■ ${t('BtnStopBridge')}`;
      btnCls = 'btn-coral';
    } else if (h.isConnecting) {
      statusText = t('BadgeConnecting');
      statusCls = 'connecting';
      btnText = `✕ ${t('BtnCancel')}`;
      btnCls = 'btn-coral';
    } else if (isPartial) {
      statusText = t('BadgePartial', listenCount, total);
      statusCls = 'connecting';
      btnText = `■ ${t('BtnStopBridge')}`;
      btnCls = 'btn-coral';
    }

    const hdr = document.createElement('div');
    hdr.className = 'bridge-header';
    hdr.innerHTML = `
      <div class="bridge-left">
        <span class="bridge-chevron">${isExpanded ? '▾' : '▸'}</span>
        <div>
          <div class="bridge-title">${h.hostAlias} <span class="bridge-count">(${total})</span></div>
          <div class="bridge-meta">${h.user}@${h.hostName}:${h.port}${h.groupComment ? ' &nbsp;|&nbsp; ' + h.groupComment : ''}</div>
        </div>
      </div>
      <div class="bridge-right">
        <span class="status-pill ${statusCls}">${statusText}</span>
        <button class="btn btn-sm btn-ghost" data-act="edit-cfg" title="${t('BtnEdit')}">✎</button>
        <button class="btn ${btnCls}" data-act="toggle">${btnText}</button>
      </div>
    `;
    hdr.onclick = (e) => {
      if (e.target.closest('button')) return;
      state.bridgeManualExpanded.set(h.hostAlias, !isExpanded);
      renderBridges();
    };
    hdr.querySelector('[data-act="edit-cfg"]').onclick = (e) => {
      e.stopPropagation();
      openCfgEditor(h.hostAlias);
    };
    hdr.querySelector('[data-act="toggle"]').onclick = (e) => {
      e.stopPropagation();
      post({ cmd: 'toggleBridge', hostAlias: h.hostAlias });
    };
    card.appendChild(hdr);

    const list = document.createElement('div');
    list.className = 'forwards-list';
    h.forwards.forEach((fw) => {
      const row = document.createElement('div');
      row.className = 'forward-row';
      row.innerHTML = `
        <div class="fw-left">
          <span class="fw-dot ${fw.isListening ? 'on' : ''}"></span>
          <span class="fw-name">${fw.name}</span>
        </div>
        <div class="fw-map">localhost:${fw.localPort} &rarr; ${fw.remoteHost}:${fw.remotePort}</div>
        <button class="btn btn-sm ${fw.isDb ? 'btn-ghost' : 'btn-blue'}">${fw.isDb ? t('BtnDbCopy') : t('BtnOpenBrowser') + ' ↗'}</button>
      `;
      row.querySelector('button').onclick = () => {
        if (fw.isDb) post({ cmd: 'copyText', text: `127.0.0.1:${fw.localPort}` });
        else post({ cmd: 'openUrl', url: `http://localhost:${fw.localPort}` });
      };
      list.appendChild(row);
    });
    card.appendChild(list);
    grid.appendChild(card);
  });
};

const ICON_BRIDGE = '<svg class="tab-bridge-ico" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 16h20"/><path d="M4 16v-3a8 8 0 0 1 16 0v3"/><path d="M6 16v3"/><path d="M18 16v3"/><path d="M9 11v5"/><path d="M15 11v5"/></svg>';
const ICON_SERVER = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="7" rx="2"/><rect x="2" y="14" width="20" height="7" rx="2"/><line x1="6" y1="6.5" x2="6.01" y2="6.5"/><line x1="6" y1="17.5" x2="6.01" y2="17.5"/></svg>';
const ICON_SERVER_SM = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="7" rx="2"/><rect x="2" y="14" width="20" height="7" rx="2"/><line x1="6" y1="6.5" x2="6.01" y2="6.5"/><line x1="6" y1="17.5" x2="6.01" y2="17.5"/></svg>';

const renderSessionsList = () => {
  const list = document.getElementById('sessionsList');
  list.innerHTML = '';

  const pureHosts = state.hosts.filter((h) => !h.hasForwards);
  const displayHosts = pureHosts.length > 0 ? pureHosts : state.hosts;

  let activeServers = 0;
  let connectingServers = 0;
  let inactiveServers = 0;

  displayHosts.forEach((h) => {
    const hostSessions = state.sessions.filter((s) => s.hostAlias === h.hostAlias && (s.mode === 'ssh' || s.mode === 'sftp'));
    const activeCount = hostSessions.filter((s) => s.authenticated && !s.exited).length;
    const connectingCount = hostSessions.filter((s) => !s.authenticated && !s.exited).length;
    const errorCount = hostSessions.filter((s) => s.failed).length;
    const isConnected = activeCount > 0;
    const isConnecting = !isConnected && connectingCount > 0;
    const isError = !isConnected && !isConnecting && errorCount > 0;

    if (isConnected) activeServers++;
    else if (isConnecting) connectingServers++;
    else inactiveServers++;

    const stateCls = isConnected ? 'connected' : (isConnecting ? 'connecting' : (isError ? 'error' : 'idle'));
    const icoCls = isConnected ? 'active' : (isConnecting ? 'connecting' : (isError ? 'error' : 'idle'));
    const icoTitle = isConnected
      ? `${t('SessConnectedTip')} (${activeCount})`
      : (isConnecting ? `${t('SessConnectingTip')} (${connectingCount})` : (isError ? `${t('SessErrorTip')} (${errorCount})` : t('SessDisconnectedTip')));

    let badges = '';
    if (isConnected) {
      badges += `<span class="session-count-badge">${activeCount}×</span>`;
      if (connectingCount > 0) badges += `<span class="session-count-badge connecting" title="${t('SessConnectingTip')} (${connectingCount})">+${connectingCount}…</span>`;
    } else if (isConnecting) {
      badges += `<span class="session-count-badge connecting">${connectingCount > 1 ? connectingCount + '× ' : ''}${t('SessConnectingShort')}</span>`;
    } else if (isError) {
      badges += `<span class="session-count-badge error">${errorCount > 1 ? errorCount + '× ' : ''}${t('SessErrorShort')}</span>`;
    }

    const item = document.createElement('div');
    item.className = `session-item ${stateCls}`;
    item.innerHTML = `
      <div class="session-main">
        <span class="session-srv-ico ${icoCls}" title="${icoTitle}">${ICON_SERVER}</span>
        <div>
          <div class="session-name">${h.hostAlias}${badges}</div>
          <div class="session-sub">${h.user}@${h.hostName}:${h.port}</div>
        </div>
      </div>
      <div class="session-btns">
        <button class="mini-btn" data-act="sftp">SFTP</button>
        <button class="mini-btn" data-act="ext">↗</button>
      </div>
    `;
    item.onclick = (e) => {
      const act = e.target?.getAttribute?.('data-act');
      if (act === 'sftp') openSession('sftp', h.hostAlias, h);
      else if (act === 'ext') post({ cmd: 'openExternal', hostAlias: h.hostAlias });
      else openSession('ssh', h.hostAlias, h);
    };
    list.appendChild(item);
  });

  const sumEl = document.getElementById('sessionsSummary');
  if (sumEl) {
    sumEl.innerHTML = `
      <span class="sess-stat-pill active" title="${t('SessConnectedTip')}">${ICON_SERVER_SM} ${t('SessActive', activeServers)}</span>
      ${connectingServers > 0 ? `<span class="sess-stat-pill connecting" title="${t('SessConnectingTip')}">${ICON_SERVER_SM} ${t('SessConnecting', connectingServers)}</span>` : ''}
      <span class="sess-stat-pill idle" title="${t('SessDisconnectedTip')}">${ICON_SERVER_SM} ${t('SessInactive', inactiveServers)}</span>
    `;
  }

  const grp = document.createElement('div');
  grp.className = 'session-group-title';
  grp.textContent = t('SidebarLocal');
  list.appendChild(grp);

  [
    { mode: 'powershell', name: 'PowerShell', sub: 'Windows PowerShell', cls: 'local-ps' },
    { mode: 'cmd', name: 'CMD', sub: 'Command Prompt', cls: 'local-cmd' }
  ].forEach((loc) => {
    const item = document.createElement('div');
    item.className = `session-item ${loc.cls}`;
    item.innerHTML = `
      <div>
        <div class="session-name">&gt;_ ${loc.name}</div>
        <div class="session-sub">${loc.sub}</div>
      </div>
    `;
    item.onclick = () => openSession(loc.mode, loc.name, null);
    list.appendChild(item);
  });
};

const switchMainTab = (tab) => {
  state.activeMainTab = tab;
  document.getElementById('tabHome')?.classList.toggle('active', tab === 'home');
  document.getElementById('tabBridges').classList.toggle('active', tab === 'bridges');
  document.getElementById('tabTerminals').classList.toggle('active', tab === 'terminals');
  document.getElementById('viewHome')?.classList.toggle('active', tab === 'home');
  document.getElementById('viewBridges').classList.toggle('active', tab === 'bridges');
  document.getElementById('viewTerminals').classList.toggle('active', tab === 'terminals');
  if (tab === 'home') renderHome();
  if (tab === 'terminals') {
    const active = state.sessions.find((s) => s.id === state.activeTermId);
    if (active?.fit) setTimeout(() => { active.fit.fit(); active.term.focus(); }, 40);
  }
};

document.getElementById('tabHome').onclick = () => switchMainTab('home');
document.getElementById('tabBridges').onclick = () => switchMainTab('bridges');
document.getElementById('tabTerminals').onclick = () => switchMainTab('terminals');

const syncSftpSidebar = (shouldRefresh = false) => {
  const hasRemote = !!getTreeTargetAlias();
  state.activeSideTab = hasRemote ? 'files' : 'sessions';
  const pane = document.getElementById('paneFiles');
  if (pane) pane.classList.toggle('active', hasRemote);
  if (hasRemote && shouldRefresh) refreshTree();
};

const renderTermTabs = () => {
  const bar = document.getElementById('termTabbar');
  bar.innerHTML = '';
  document.getElementById('emptyWorkspace').style.display = state.sessions.length === 0 ? 'flex' : 'none';
  document.getElementById('lblTabTerminals').textContent = t('TabTerminals', state.sessions.length);

  state.sessions.forEach((s) => {
    const tab = document.createElement('div');
    tab.className = `term-tab ${s.id === state.activeTermId ? 'active' : ''}`;
    const isRemote = s.mode === 'ssh' || s.mode === 'sftp';
    const dotCls = s.failed ? 'error' : (s.authenticated ? 'active' : 'connecting');
    const dotTitle = s.failed ? t('SessErrorTip') : (s.authenticated ? t('SessConnectedTip') : t('SessConnectingTip'));
    const dot = isRemote
      ? `<span class="term-tab-dot ${dotCls}" title="${dotTitle}"></span>`
      : '';
    tab.innerHTML = `${dot}<span>${s.title}</span><button class="term-tab-close">✕</button>`;
    tab.onclick = (e) => {
      if (e.target.classList.contains('term-tab-close')) closeSession(s.id);
      else activateSession(s.id);
    };
    bar.appendChild(tab);
    s.paneEl.classList.toggle('active', s.id === state.activeTermId);
  });
};

const activateSession = (id) => {
  state.activeTermId = id;
  renderTermTabs();
  const s = state.sessions.find((x) => x.id === id);
  if (s?.fit) {
    setTimeout(() => {
      s.fit.fit();
      s.term.focus();
      post({ cmd: 'termResize', id: s.id, cols: s.term.cols, rows: s.term.rows });
    }, 30);
  }
  const shouldRefresh = s && (s.mode === 'sftp' || (s.mode === 'ssh' && s.authenticated)) && state.treeConn !== s.hostAlias;
  syncSftpSidebar(shouldRefresh);
  updateSysbarVisibility();
};

const closeSession = (id) => {
  const idx = state.sessions.findIndex((s) => s.id === id);
  if (idx === -1) return;
  const s = state.sessions[idx];
  if (s.mode !== 'sftp') post({ cmd: 'termClose', id });
  s.term?.dispose();
  s.paneEl.remove();
  state.sessions.splice(idx, 1);
  if (state.activeTermId === id) {
    state.activeTermId = state.sessions.length > 0 ? state.sessions[state.sessions.length - 1].id : null;
  }
  renderTermTabs();
  renderSessionsList();
  if (state.activeTermId) activateSession(state.activeTermId);
  else {
    syncSftpSidebar(false);
    updateSysbarVisibility();
  }
};

const listRemoteWithAuth = async (hostAlias, path, asRoot) => {
  let res = await sendReq('sftpListRemote', { hostAlias, path, asRoot });
  if (!res.ok && res.needPassword) {
    const dlg = await openModal({
      title: t('PwdTitle', hostAlias),
      desc: t('PwdPrompt', hostAlias),
      isPassword: true,
      showRemember: false,
      okText: t('PwdConnect')
    });
    if (dlg && dlg.value) {
      res = await sendReq('sftpListRemote', { hostAlias, path, asRoot, password: dlg.value });
    }
  }
  return res;
};

const ensureSudoWithModal = async (hostAlias) => {
  const r1 = await sendReq('sftpCheckSudo', { hostAlias });
  if (r1.ok) return true;
  if (!r1.needPassword) return false;
  const dlg = await openModal({
    title: t('AdminRootMode'),
    desc: t('SudoPwdPrompt', hostAlias),
    isPassword: true,
    showRemember: false,
    okText: 'OK'
  });
  if (!dlg || !dlg.value) return false;
  const r2 = await sendReq('sftpCheckSudo', { hostAlias, password: dlg.value });
  return !!r2.ok;
};

const openSession = async (mode, hostAlias, hostObj) => {
  const id = `sess-${++state.seq}`;
  const label = mode === 'sftp' ? 'SFTP' : (mode === 'powershell' || mode === 'cmd' ? 'Local' : 'SSH');
  const same = state.sessions.filter((s) => s.hostAlias === hostAlias && s.mode === mode).length;
  const title = same > 0 ? `${hostAlias} [${label}] (${same + 1})` : `${hostAlias} [${label}]`;

  const paneEl = document.createElement('div');
  paneEl.className = 'tab-pane';
  document.getElementById('workspaceBody').appendChild(paneEl);

  if (mode === 'sftp') {
    const asRootInit = document.getElementById('chkTreeAdmin').checked;
    const sess = { id, title, mode, hostAlias, hostObj, paneEl, asRoot: asRootInit, authenticated: false };
    state.sessions.push(sess);
    switchMainTab('terminals');
    activateSession(id);
    renderSessionsList();
    await buildDualSftpUI(sess);
    return;
  }

  const wrap = document.createElement('div');
  wrap.className = 'xterm-wrap';
  const inner = document.createElement('div');
  inner.className = 'xterm-inner';
  wrap.appendChild(inner);
  paneEl.appendChild(wrap);

  const term = new Terminal({
    cursorBlink: true,
    fontFamily: '"Cascadia Mono", "Consolas", monospace',
    fontSize: 13.5,
    theme: XTERM_THEME
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(inner);

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
    const key = (e.key || '').toLowerCase();

    if (ctrl && key === 'c') {
      if (term.hasSelection() || e.shiftKey) {
        const sel = term.getSelection();
        if (sel) {
          post({ cmd: 'copyText', text: sel });
          navigator.clipboard?.writeText(sel).catch(() => {});
          term.clearSelection();
        }
        e.preventDefault();
        return false;
      }
      return true;
    }

    if (ctrl && key === 'v') {
      e.preventDefault();
      post({ cmd: 'termPaste', id });
      return false;
    }

    return true;
  });

  wrap.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (term.hasSelection()) {
      const sel = term.getSelection();
      if (sel) {
        post({ cmd: 'copyText', text: sel });
        navigator.clipboard?.writeText(sel).catch(() => {});
        term.clearSelection();
      }
    } else {
      post({ cmd: 'termPaste', id });
    }
    term.focus();
  });

  const sess = { id, title, mode, hostAlias, hostObj, term, fit, paneEl, authenticated: false, exited: false, failed: false };
  state.sessions.push(sess);
  switchMainTab('terminals');
  activateSession(id);
  renderSessionsList();
  if (mode === 'ssh') {
    syncSftpSidebar(false);
    document.getElementById('treeStatus').textContent = `${hostAlias}: ...`;
    document.getElementById('treeContainer').innerHTML = '';
  }

  setTimeout(() => {
    fit.fit();
    const asRoot = mode === 'ssh' && document.getElementById('chkTreeAdmin').checked;
    post({ cmd: 'termStart', id, mode, hostAlias, asRoot, cols: term.cols, rows: term.rows });
  }, 50);

  term.onData((data) => post({ cmd: 'termInput', id, data }));
  new ResizeObserver(() => {
    if (state.activeTermId === id && state.activeMainTab === 'terminals') {
      fit.fit();
      post({ cmd: 'termResize', id, cols: term.cols, rows: term.rows });
    }
  }).observe(inner);
};

/* DUAL-PANE SFTP UI */
const buildDualSftpUI = async (sess) => {
  const sftp = { localPath: '', remotePath: '~', selLocal: null, selRemote: null };
  sess.paneEl.innerHTML = `
    <div class="sftp-dual">
      <div class="sftp-bar">
        <button class="btn btn-sm btn-emerald" data-act="upload">${t('SftpUpload')}</button>
        <button class="btn btn-sm btn-blue" data-act="download">${t('SftpDownload')}</button>
        <button class="btn btn-sm btn-ghost" data-act="mkdir">${t('SftpNewFolder')}</button>
        <button class="btn btn-sm btn-coral" data-act="delete">${t('SftpDelete')}</button>
        <button class="btn btn-sm btn-ghost" data-act="refresh">↻ ${t('SftpRefresh')}</button>
        <label class="toggle-row">
          <input type="checkbox" data-act="root" ${sess.asRoot ? 'checked' : ''} />
          <span class="toggle-slider"></span>
          <span class="toggle-label amber">${t('AdminRootMode')}</span>
        </label>
      </div>
      <div class="sftp-panes">
        <div class="sftp-pane">
          <div class="sftp-pane-hdr">${t('SftpLocal')}</div>
          <div class="sftp-nav">
            <button class="icon-btn" data-nav="lup">..</button>
            <input class="sftp-path" data-inp="local" />
            <button class="icon-btn" data-nav="lhome">~</button>
          </div>
          <div class="sftp-table-wrap"><table class="sftp-table"><thead><tr><th>${t('SftpColName')}</th><th>${t('SftpColSize')}</th><th>${t('SftpColInfo')}</th></tr></thead><tbody data-tb="local"></tbody></table></div>
        </div>
        <div class="sftp-pane">
          <div class="sftp-pane-hdr" style="color:var(--cyan)">${t('SftpRemote', sess.hostAlias)}</div>
          <div class="sftp-nav">
            <button class="icon-btn" data-nav="rup">..</button>
            <input class="sftp-path" data-inp="remote" />
            <button class="icon-btn" data-nav="rhome">~</button>
          </div>
          <div class="sftp-table-wrap"><table class="sftp-table"><thead><tr><th>${t('SftpColName')}</th><th>${t('SftpColSize')}</th><th>${t('SftpColInfo')}</th></tr></thead><tbody data-tb="remote"></tbody></table></div>
        </div>
      </div>
    </div>
  `;

  const q = (sel) => sess.paneEl.querySelector(sel);
  const loadLocal = async (path) => {
    const res = await sendReq('sftpListLocal', { path });
    sftp.localPath = res.path;
    q('[data-inp="local"]').value = res.path;
    const tb = q('[data-tb="local"]');
    tb.innerHTML = '';
    sftp.selLocal = null;
    res.entries.forEach((en) => {
      const tr = document.createElement('tr');
      if (en.type !== 'file') tr.className = 'dir';
      tr.innerHTML = `<td>${en.type === 'dir' ? '[' + en.name + ']' : en.name}</td><td>${en.sizeText}</td><td>${en.info}</td>`;
      tr.onclick = () => {
        tb.querySelectorAll('tr').forEach((r) => r.classList.remove('selected'));
        tr.classList.add('selected');
        sftp.selLocal = en;
      };
      tr.ondblclick = () => { if (en.type !== 'file') loadLocal(en.path); };
      tb.appendChild(tr);
    });
  };

  const loadRemote = async (path) => {
    const res = await listRemoteWithAuth(sess.hostAlias, path, sess.asRoot);
    if (!res.ok) {
      q('[data-tb="remote"]').innerHTML = `<tr><td colspan="3" style="color:var(--coral);">${res.error || 'SFTP error'}</td></tr>`;
      return;
    }
    if (!sess.authenticated) {
      sess.authenticated = true;
      renderTermTabs();
      renderSessionsList();
    }
    sftp.remotePath = res.path;
    q('[data-inp="remote"]').value = res.path;
    const tb = q('[data-tb="remote"]');
    tb.innerHTML = '';
    sftp.selRemote = null;
    res.entries.forEach((en) => {
      const tr = document.createElement('tr');
      if (en.type !== 'file') tr.className = 'dir';
      tr.innerHTML = `<td>${en.type === 'dir' ? '[' + en.name + ']' : en.name}</td><td>${en.sizeText}</td><td>${en.info}</td>`;
      tr.onclick = () => {
        tb.querySelectorAll('tr').forEach((r) => r.classList.remove('selected'));
        tr.classList.add('selected');
        sftp.selRemote = en;
      };
      tr.ondblclick = () => {
        if (en.type !== 'file') loadRemote(en.path);
        else q('[data-act="download"]').click();
      };
      tb.appendChild(tr);
    });
  };

  if (sess.asRoot && !(await ensureSudoWithModal(sess.hostAlias))) {
    sess.asRoot = false;
    q('[data-act="root"]').checked = false;
  }

  q('[data-act="root"]').onchange = async (e) => {
    if (e.target.checked) {
      if (await ensureSudoWithModal(sess.hostAlias)) { sess.asRoot = true; loadRemote(sftp.remotePath); }
      else e.target.checked = false;
    } else {
      sess.asRoot = false;
      loadRemote(sftp.remotePath);
    }
  };

  q('[data-nav="lup"]').onclick = () => { const p = sftp.localPath.replace(/\\[^\\]+$/, ''); if (p) loadLocal(p); };
  q('[data-nav="lhome"]').onclick = () => loadLocal('');
  q('[data-inp="local"]').onkeydown = (e) => { if (e.key === 'Enter') loadLocal(e.target.value); };

  q('[data-nav="rup"]').onclick = () => { const p = sftp.remotePath.replace(/\/[^/]+$/, '') || '/'; loadRemote(p); };
  q('[data-nav="rhome"]').onclick = () => loadRemote(sess.asRoot ? '/root' : '~');
  q('[data-inp="remote"]').onkeydown = (e) => { if (e.key === 'Enter') loadRemote(e.target.value); };

  q('[data-act="refresh"]').onclick = () => { loadLocal(sftp.localPath); loadRemote(sftp.remotePath); };
  q('[data-act="upload"]').onclick = async () => {
    if (!sftp.selLocal || sftp.selLocal.type === 'up') return;
    await sendReq('sftpAction', { action: 'upload', hostAlias: sess.hostAlias, asRoot: sess.asRoot, localPath: sftp.selLocal.path, remotePath: sftp.remotePath, name: sftp.selLocal.name });
    loadRemote(sftp.remotePath);
  };
  q('[data-act="download"]').onclick = async () => {
    if (!sftp.selRemote || sftp.selRemote.type === 'up') return;
    await sendReq('sftpAction', { action: 'download', hostAlias: sess.hostAlias, asRoot: sess.asRoot, localPath: sftp.localPath, remotePath: sftp.selRemote.path, name: sftp.selRemote.name });
    loadLocal(sftp.localPath);
  };
  q('[data-act="mkdir"]').onclick = async () => {
    const dlg = await openModal({ title: t('SftpNewFolder'), desc: t('SftpNewFolderPrompt'), isPassword: false, showRemember: false, okText: 'OK' });
    if (!dlg || !dlg.value.trim()) return;
    await sendReq('sftpAction', { action: 'mkdir', hostAlias: sess.hostAlias, asRoot: sess.asRoot, remotePath: sftp.remotePath, name: dlg.value.trim() });
    loadRemote(sftp.remotePath);
  };
  q('[data-act="delete"]').onclick = async () => {
    if (!sftp.selRemote || sftp.selRemote.type === 'up') return;
    if (!confirm(t('SftpConfirmDelete', sftp.selRemote.name))) return;
    await sendReq('sftpAction', { action: 'delete', hostAlias: sess.hostAlias, asRoot: sess.asRoot, remotePath: sftp.selRemote.path, name: sftp.selRemote.name });
    loadRemote(sftp.remotePath);
  };

  loadLocal('');
  loadRemote(sess.asRoot ? '/root' : '~');
};

/* SIDEBAR TREE SFTP */
const getTreeTargetAlias = () => {
  const cur = state.sessions.find((s) => s.id === state.activeTermId);
  if (cur && (cur.mode === 'ssh' || cur.mode === 'sftp')) return cur.hostAlias;
  const any = state.sessions.find((s) => s.mode === 'ssh' || s.mode === 'sftp');
  return any ? any.hostAlias : null;
};

const renderTreeNodes = (container, hostAlias, entries, asRoot, isRootLevel = true) => {
  container.innerHTML = '';
  entries.filter((e) => isRootLevel || e.type !== 'up').forEach((en) => {
    const row = document.createElement('div');
    row.className = `tree-node ${en.type === 'up' ? 'dir' : en.type}`;
    row.textContent = en.type === 'up'
      ? '⬆ 📁 ..'
      : (en.type === 'dir' ? `▸ 📁 ${en.name}` : `   📄 ${en.name} (${en.sizeText})`);

    const children = document.createElement('div');
    children.className = 'tree-children';
    children.style.display = 'none';
    let loaded = false;

    row.onclick = async () => {
      if (en.type === 'up') {
        if (document.getElementById('chkTreeFollowCd').checked) {
          const active = state.sessions.find((s) => s.id === state.activeTermId && s.mode === 'ssh');
          if (active) {
            state.suppressCwdSyncUntil = Date.now() + 1500;
            post({ cmd: 'termInput', id: active.id, data: `cd '${en.path.replace(/'/g, "'\\''")}'\r` });
          }
        }
        refreshTree(en.path);
        return;
      }
      if (en.type === 'dir') {
        if (children.style.display === 'none') {
          if (!loaded) {
            const res = await sendReq('sftpListRemote', { hostAlias, path: en.path, asRoot });
            if (res.ok) renderTreeNodes(children, hostAlias, res.entries, asRoot, false);
            loaded = true;
          }
          children.style.display = 'block';
          row.textContent = `▾ 📁 ${en.name}`;
        } else {
          children.style.display = 'none';
          row.textContent = `▸ 📁 ${en.name}`;
        }
        if (document.getElementById('chkTreeFollowCd').checked) {
          const active = state.sessions.find((s) => s.id === state.activeTermId && s.mode === 'ssh');
          if (active) {
            state.suppressCwdSyncUntil = Date.now() + 1500;
            post({ cmd: 'termInput', id: active.id, data: `cd '${en.path.replace(/'/g, "'\\''")}'\r` });
          }
        }
      }
    };

    row.oncontextmenu = (ev) => {
      ev.preventDefault();
      state.treeSelectedNode = { hostAlias, entry: en, asRoot };
      const m = document.getElementById('treeCtxMenu');
      m.style.left = `${ev.clientX}px`;
      m.style.top = `${ev.clientY}px`;
      m.classList.add('open');
    };

    container.appendChild(row);
    if (en.type === 'dir') container.appendChild(children);
  });
};

const refreshTree = async (startPath = '') => {
  const hostAlias = getTreeTargetAlias();
  const box = document.getElementById('treeContainer');
  if (!hostAlias) {
    state.treeConn = null;
    box.innerHTML = '';
    document.getElementById('treeStatus').textContent = t('TreeNoServer');
    return;
  }
  state.treeConn = hostAlias;
  let asRoot = document.getElementById('chkTreeAdmin').checked;
  if (asRoot && !(await ensureSudoWithModal(hostAlias))) {
    asRoot = false;
    document.getElementById('chkTreeAdmin').checked = false;
  }
  const target = startPath || (asRoot ? '/root' : '~');
  state.treeRoot = target;
  document.getElementById('treeStatus').textContent = `${hostAlias}: ...`;
  const res = await listRemoteWithAuth(hostAlias, target, asRoot);
  if (res.ok) {
    state.treeRoot = res.path || target;
    document.getElementById('treeStatus').textContent = `${hostAlias}${asRoot ? ' [root]' : ''}: ${res.path}`;
    renderTreeNodes(box, hostAlias, res.entries, asRoot);
  } else {
    box.innerHTML = '';
    document.getElementById('treeStatus').textContent = `${hostAlias}: ${res.error || 'Chyba'}`;
  }
};

document.getElementById('btnTreeRefresh').onclick = () => refreshTree(state.treeRoot);
document.getElementById('btnTreeHome').onclick = () => refreshTree(document.getElementById('chkTreeAdmin').checked ? '/root' : '~');
document.getElementById('btnTreeRoot').onclick = () => refreshTree('/');

const handleAdminRootToggle = async (enabled) => {
  document.getElementById('chkTreeAdmin').checked = enabled;

  const active = state.sessions.find((s) => s.id === state.activeTermId && s.mode === 'ssh');
  if (active && active.authenticated) {
    if (enabled) {
      post({ cmd: 'termInput', id: active.id, data: 'sudo -i\r' });
      active.term?.focus();
      const chk = await sendReq('sftpCheckSudo', { hostAlias: active.hostAlias });
      if (chk.ok) {
        state.waitingTerminalSudo = false;
        refreshTree('/root');
      } else {
        state.waitingTerminalSudo = true;
        document.getElementById('treeStatus').textContent = `${active.hostAlias} [root]: zadejte sudo heslo v terminálu...`;
      }
    } else {
      state.waitingTerminalSudo = false;
      post({ cmd: 'termInput', id: active.id, data: 'exit\r' });
      active.term?.focus();
      refreshTree('~');
    }
    return;
  }

  if (state.activeSideTab === 'files') refreshTree(enabled ? '/root' : '~');
};

document.getElementById('chkTreeAdmin').onchange = (e) => handleAdminRootToggle(e.target.checked);

document.addEventListener('click', () => document.getElementById('treeCtxMenu').classList.remove('open'));
document.getElementById('ctxCd').onclick = () => {
  const sel = state.treeSelectedNode;
  if (!sel || sel.entry.type !== 'dir') return;
  const active = state.sessions.find((s) => s.id === state.activeTermId && s.mode === 'ssh');
  if (active) {
    state.suppressCwdSyncUntil = Date.now() + 1500;
    post({ cmd: 'termInput', id: active.id, data: `cd '${sel.entry.path.replace(/'/g, "'\\''")}'\r` });
  }
};
document.getElementById('ctxDownload').onclick = () => {
  const sel = state.treeSelectedNode;
  if (!sel || sel.entry.type !== 'file') return;
  sendReq('sftpAction', { action: 'pickDownload', hostAlias: sel.hostAlias, asRoot: sel.asRoot, remotePath: sel.entry.path, name: sel.entry.name });
};
document.getElementById('ctxUpload').onclick = async () => {
  const sel = state.treeSelectedNode;
  if (!sel || sel.entry.type !== 'dir') return;
  await sendReq('sftpAction', { action: 'pickUpload', hostAlias: sel.hostAlias, asRoot: sel.asRoot, remotePath: sel.entry.path });
  refreshTree(state.treeRoot);
};
document.getElementById('ctxRefresh').onclick = () => refreshTree(state.treeRoot);

/* GLOBAL BUTTONS */
const runExportImport = () => {
  openInfoModal(t('ExpImpTitle'), `
    <p style="margin-bottom:14px;">${t('ExpImpDesc')}</p>
    <div style="display:flex;gap:10px;flex-wrap:wrap;">
      <button class="btn btn-emerald" id="btnDoExport" type="button">${t('ExpBtn')}</button>
      <button class="btn btn-blue" id="btnDoImport" type="button">${t('ImpBtn')}</button>
    </div>
  `);

  document.getElementById('btnDoExport').onclick = async () => {
    closeInfoModal();
    const dlg = await openModal({
      title: t('ExpPwdTitle'),
      desc: t('ExpPwdDesc'),
      isPassword: true,
      showRemember: false,
      okText: t('ExpPwdOk')
    });
    if (!dlg || !dlg.value) return;
    await sendReq('exportBundle', { allKeys: true, password: dlg.value });
  };

  document.getElementById('btnDoImport').onclick = async () => {
    closeInfoModal();
    let res = await sendReq('importBundle');
    while (res && (res.needPassword || res.badPassword) && res.filePath) {
      const dlg = await openModal({
        title: t('ImpPwdTitle'),
        desc: res.badPassword ? t('ImpBadPwd') : t('ImpPwdDesc'),
        isPassword: true,
        showRemember: false,
        okText: t('ImpPwdOk')
      });
      if (!dlg || !dlg.value) break;
      res = await sendReq('importBundle', { filePath: res.filePath, password: dlg.value });
    }
  };
};

document.getElementById('btnReload').onclick = () => post({ cmd: 'reloadConfig' });
document.getElementById('chkAutoReconnect').onchange = (e) => post({ cmd: 'setAutoReconnect', enabled: e.target.checked });
document.getElementById('btnQuit').onclick = () => post({ cmd: 'quitApp' });
document.getElementById('btnToggleLog').onclick = () => {
  const dr = document.getElementById('logDrawer');
  dr.classList.toggle('open');
  document.getElementById('logArrow').textContent = dr.classList.contains('open') ? '▼' : '▲';
};

/* INFO / HELP / ABOUT MODAL */
const openInfoModal = (title, html) => {
  document.getElementById('infoModalTitle').textContent = title;
  document.getElementById('infoModalBody').innerHTML = html;
  document.getElementById('infoModalBackdrop').classList.add('open');
};
const closeInfoModal = () => document.getElementById('infoModalBackdrop').classList.remove('open');
document.getElementById('infoModalClose').onclick = closeInfoModal;
document.getElementById('infoModalBackdrop').onclick = (e) => {
  if (e.target.id === 'infoModalBackdrop') closeInfoModal();
};

const showHelpModal = () => openInfoModal(t('HelpTitle'), `
  <h4>${t('HelpH1')}</h4>
  <ul>
    <li>${t('HelpP1')}</li>
    <li>${t('HelpP2')}</li>
  </ul>
  <h4>${t('HelpH2')}</h4>
  <ul>
    <li>${t('HelpP3')}</li>
    <li>${t('HelpP4')}</li>
    <li>${t('HelpP5')}</li>
  </ul>
  <h4>${t('HelpH3')}</h4>
  <ul>
    <li>${t('HelpP6')}</li>
    <li>${t('HelpP7')}</li>
    <li>${t('HelpP8')}</li>
  </ul>
  <h4>${t('HelpH4')}</h4>
  <ul>
    <li>${t('HelpP9')}</li>
    <li>${t('HelpP10')}</li>
  </ul>
`);

const showAboutModal = () => {
  openInfoModal(t('AboutTitle'), `
    <div class="about-hero">
      <div class="shield-orb active" style="flex-shrink:0;">
        <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M12 2L4 5v6.09c0 5.05 3.41 9.76 8 10.91 4.59-1.15 8-5.86 8-10.91V5l-8-3z"/>
          <rect x="9" y="10" width="6" height="5" rx="1"/>
          <path d="M10 10V8a2 2 0 114 0v2"/>
        </svg>
      </div>
      <div>
        <div style="font-size:16px;font-weight:800;">Pontish <span class="version-pill">v${state.appVersion}</span></div>
        <div style="color:var(--text-muted);font-size:12px;">${t('AboutDesc')}</div>
      </div>
    </div>
    <ul>
      <li><b>${t('AboutTechLabel')}</b> .NET 8, Windows Forms, WebView2, xterm.js, Windows ConPTY &amp; OpenSSH.</li>
      <li><b>${t('AboutLicensesLabel')}</b> xterm.js &amp; addon-fit (MIT), Microsoft.Web.WebView2 (BSD-3-Clause) — <code>THIRD-PARTY-NOTICES.txt</code></li>
      <li><b>${t('AboutRepoLabel')}</b> <button type="button" class="about-link" id="aboutGitLink">github.com/petrbla/pontish</button></li>
    </ul>
  `);
  document.getElementById('aboutGitLink').onclick = () => post({ cmd: 'openUrl', url: 'https://github.com/petrbla/pontish' });
};

/* WINDOWS-STYLE MENU BAR */
(() => {
  const bar = document.getElementById('winMenubar');
  if (!bar) return;
  const menus = Array.from(bar.querySelectorAll('.win-menu'));
  const closeMenus = () => menus.forEach((m) => m.classList.remove('open'));

  menus.forEach((m) => {
    const trig = m.querySelector('.win-menu-trigger');
    trig.onclick = (e) => {
      e.stopPropagation();
      const wasOpen = m.classList.contains('open');
      closeMenus();
      if (!wasOpen) m.classList.add('open');
    };
    trig.onmouseenter = () => {
      if (menus.some((x) => x.classList.contains('open'))) {
        closeMenus();
        m.classList.add('open');
      }
    };
  });

  bar.querySelectorAll('[data-menu-act]').forEach((btn) => {
    btn.onclick = (e) => {
      e.stopPropagation();
      const act = btn.getAttribute('data-menu-act');
      closeMenus();
      switch (act) {
        case 'visualConfig': openCfgEditor(); break;
        case 'reloadConfig': post({ cmd: 'reloadConfig' }); break;
        case 'editConfig': post({ cmd: 'editConfig' }); break;
        case 'exportImport': runExportImport(); break;
        case 'quitApp': post({ cmd: 'quitApp' }); break;
        case 'tabHome': switchMainTab('home'); break;
        case 'tabTerminals': switchMainTab('terminals'); break;
        case 'tabBridges': switchMainTab('bridges'); break;
        case 'toggleTheme': applyTheme(state.theme === 'dark' ? 'light' : 'dark'); break;
        case 'toggleLog': document.getElementById('btnToggleLog').click(); break;
        case 'togglePersistPass': post({ cmd: 'setPersistPasswords', enabled: !state.persistPasswords }); break;
        case 'forgetPass': post({ cmd: 'forgetPasswords' }); break;
        case 'openPs': openSession('powershell', 'PowerShell', null); break;
        case 'openCmd': openSession('cmd', 'CMD', null); break;
        case 'showHelp': showHelpModal(); break;
        case 'checkUpdate': switchMainTab('home'); checkForUpdates(false); break;
        case 'showAbout': showAboutModal(); break;
      }
    };
  });

  document.addEventListener('click', closeMenus);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { closeMenus(); closeInfoModal(); closeCfgEditor(); }
    if (e.key === 'F1') { e.preventDefault(); closeMenus(); showHelpModal(); }
  });
})();

/* VISUAL SSH CONFIG EDITOR */
const closeCfgEditor = () => document.getElementById('cfgEditorBackdrop')?.classList.remove('open');
document.getElementById('cfgCloseTop').onclick = closeCfgEditor;
document.getElementById('cfgBtnEditNotepad').onclick = () => post({ cmd: 'editConfig' });

const openCfgEditor = (selectAlias = null) => {
  const backdrop = document.getElementById('cfgEditorBackdrop');
  backdrop.classList.add('open');
  let activeAlias = selectAlias || (state.hosts[0]?.hostAlias ?? '__new_ssh__');

  const renderHostList = () => {
    const listEl = document.getElementById('cfgHostList');
    listEl.innerHTML = '';
    if (state.hosts.length === 0) {
      listEl.innerHTML = `
        <div class="home-card-sub" style="padding:6px;">${!state.sshConfigExists ? t('CfgMissingBanner') : t('CfgNoHosts')}</div>
        ${!state.sshConfigExists ? `<button class="btn btn-sm btn-emerald" id="cfgBtnCreateFile" type="button" style="margin:6px 0;width:100%;">${t('CfgCreateBtn')}</button>` : ''}
      `;
      const crBtn = document.getElementById('cfgBtnCreateFile');
      if (crBtn) crBtn.onclick = () => post({ cmd: 'createSshConfig' });
      return;
    }

    const sshHosts = state.hosts.filter((h) => !h.hasForwards);
    const bridgeHosts = state.hosts.filter((h) => h.hasForwards);

    const appendGroup = (title, items) => {
      const hdr = document.createElement('div');
      hdr.className = 'cfg-group-hdr';
      hdr.innerHTML = `<span>${title}</span><span>${items.length}</span>`;
      listEl.appendChild(hdr);
      if (items.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'home-card-sub';
        empty.style.padding = '2px 4px 6px';
        empty.textContent = '—';
        listEl.appendChild(empty);
        return;
      }
      items.forEach((h) => {
        const div = document.createElement('div');
        div.className = `cfg-host-item ${activeAlias === h.hostAlias ? 'active' : ''}`;
        div.innerHTML = `
          <div class="cfg-host-alias">
            <span>${h.hostAlias}</span>
            ${h.forwards.length > 0 ? `<span class="cfg-badge-tun">${h.forwards.length}</span>` : ''}
          </div>
          <div class="cfg-host-meta">${h.user || ''}@${h.hostName || ''}</div>
        `;
        div.onclick = () => {
          activeAlias = h.hostAlias;
          renderHostList();
          renderForm();
        };
        listEl.appendChild(div);
      });
    };

    appendGroup(`&gt;_ ${t('CfgGroupSsh')}`, sshHosts);
    appendGroup(`${ICON_BRIDGE} ${t('CfgGroupBridges')}`, bridgeHosts);
  };

  const renderForm = (statusText = '', isErr = false) => {
    const col = document.getElementById('cfgFormCol');
    const isNewSsh = activeAlias === '__new_ssh__';
    const isNewBridge = activeAlias === '__new_bridge__';
    const existing = (isNewSsh || isNewBridge) ? null : state.hosts.find((x) => x.hostAlias === activeAlias);
    const model = existing
      ? {
          originalAlias: existing.hostAlias,
          hostAlias: existing.hostAlias,
          groupComment: existing.groupComment || '',
          hostName: existing.hostName || '',
          port: existing.port || 22,
          user: existing.user || '',
          identityFile: existing.identityFile || '',
          isBridge: !!existing.hasForwards,
          forwards: (existing.forwards || []).map((f) => ({
            name: f.name || '',
            localPort: f.localPort,
            remoteHost: f.remoteHost || '127.0.0.1',
            remotePort: f.remotePort
          }))
        }
      : {
          originalAlias: '',
          hostAlias: '',
          groupComment: '',
          hostName: '',
          port: 22,
          user: '',
          identityFile: '',
          isBridge: isNewBridge,
          forwards: isNewBridge ? [{ name: '', localPort: 8080, remoteHost: '127.0.0.1', remotePort: 80 }] : []
        };

    const tunnelsSection = model.isBridge
      ? `
        <div class="cfg-tunnels-hdr">
          <span>${t('CfgTunnels')}</span>
          <button class="btn btn-sm btn-ghost" id="cfgBtnAddTun" type="button">${t('CfgAddTunnel')}</button>
        </div>
        <div class="cfg-tunnels-list" id="cfgTunnelsList"></div>
      `
      : `
        <div style="margin-top:4px;">
          <button class="btn btn-sm btn-ghost" id="cfgBtnMakeBridge" type="button">${t('CfgConvertToBridge')}</button>
        </div>
      `;

    col.innerHTML = `
      <div class="cfg-grid">
        <div class="cfg-field">
          <label>${t('CfgAlias')}</label>
          <input class="modal-input" id="cfgInpAlias" value="${model.hostAlias}" placeholder="${model.isBridge ? 'vps1-tunely' : 'vps1'}" />
        </div>
        <div class="cfg-field">
          <label>${t('CfgComment')}</label>
          <input class="modal-input" id="cfgInpComment" value="${model.groupComment}" placeholder="${model.isBridge ? 'Tunely na server' : 'Prod server'}" />
        </div>
        <div class="cfg-field">
          <label>${t('CfgHostName')}</label>
          <input class="modal-input" id="cfgInpHostName" value="${model.hostName}" placeholder="1.2.3.4" />
        </div>
        <div class="cfg-field">
          <label>${t('CfgPort')}</label>
          <input class="modal-input" id="cfgInpPort" type="number" value="${model.port}" min="1" max="65535" />
        </div>
        <div class="cfg-field">
          <label>${t('CfgUser')}</label>
          <input class="modal-input" id="cfgInpUser" value="${model.user}" placeholder="root" />
        </div>
        <div class="cfg-field">
          <label>${t('CfgKey')}</label>
          <div class="cfg-key-row">
            <input class="modal-input" id="cfgInpKey" value="${model.identityFile}" placeholder="~/.ssh/id_ed25519" />
            <button class="btn btn-sm btn-ghost" id="cfgBtnBrowse" type="button">${t('CfgBrowse')}</button>
          </div>
        </div>
      </div>

      ${tunnelsSection}

      <div class="cfg-form-actions">
        <div class="cfg-status-msg ${isErr ? 'err' : ''}" id="cfgStatusMsg">${statusText}</div>
        <div style="display:flex;gap:8px;">
          ${existing ? `<button class="btn btn-sm btn-coral" id="cfgBtnDelete" type="button">${t('CfgDelete')}</button>` : ''}
          <button class="btn btn-sm btn-emerald" id="cfgBtnSave" type="button">${t('CfgSave')}</button>
        </div>
      </div>
    `;

    const tunList = document.getElementById('cfgTunnelsList');
    const drawTunnels = () => {
      if (!tunList) return;
      tunList.innerHTML = '';
      model.forwards.forEach((fw, idx) => {
        const row = document.createElement('div');
        row.className = 'cfg-tun-row';
        row.innerHTML = `
          <input class="modal-input" data-k="name" value="${fw.name}" placeholder="${t('CfgTunName')}" />
          <input class="modal-input" data-k="localPort" type="number" value="${fw.localPort || ''}" placeholder="${t('CfgTunLocal')}" title="${t('CfgTunLocal')}" />
          <span class="cfg-tun-arrow">→</span>
          <input class="modal-input" data-k="remoteHost" value="${fw.remoteHost}" placeholder="${t('CfgTunRHost')}" title="${t('CfgTunRHost')}" />
          <input class="modal-input" data-k="remotePort" type="number" value="${fw.remotePort || ''}" placeholder="${t('CfgTunRPort')}" title="${t('CfgTunRPort')}" />
          <button class="icon-btn" type="button" title="${t('CfgDelete')}">✕</button>
        `;
        row.querySelector('[data-k="name"]').oninput = (e) => { fw.name = e.target.value; };
        row.querySelector('[data-k="localPort"]').oninput = (e) => { fw.localPort = parseInt(e.target.value, 10) || 0; };
        row.querySelector('[data-k="remoteHost"]').oninput = (e) => { fw.remoteHost = e.target.value; };
        row.querySelector('[data-k="remotePort"]').oninput = (e) => { fw.remotePort = parseInt(e.target.value, 10) || 0; };
        row.querySelector('button').onclick = () => { model.forwards.splice(idx, 1); drawTunnels(); };
        tunList.appendChild(row);
      });
    };
    drawTunnels();

    const addTunBtn = document.getElementById('cfgBtnAddTun');
    if (addTunBtn) {
      addTunBtn.onclick = () => {
        model.forwards.push({ name: '', localPort: 8080, remoteHost: '127.0.0.1', remotePort: 80 });
        drawTunnels();
      };
    }

    const mkBridgeBtn = document.getElementById('cfgBtnMakeBridge');
    if (mkBridgeBtn) {
      mkBridgeBtn.onclick = () => {
        model.isBridge = true;
        model.forwards.push({ name: '', localPort: 8080, remoteHost: '127.0.0.1', remotePort: 80 });
        const container = mkBridgeBtn.parentElement;
        container.outerHTML = `
          <div class="cfg-tunnels-hdr">
            <span>${t('CfgTunnels')}</span>
            <button class="btn btn-sm btn-ghost" id="cfgBtnAddTun" type="button">${t('CfgAddTunnel')}</button>
          </div>
          <div class="cfg-tunnels-list" id="cfgTunnelsList"></div>
        `;
        const tl = document.getElementById('cfgTunnelsList');
        const redraw = () => {
          tl.innerHTML = '';
          model.forwards.forEach((fw, idx) => {
            const row = document.createElement('div');
            row.className = 'cfg-tun-row';
            row.innerHTML = `
              <input class="modal-input" data-k="name" value="${fw.name}" placeholder="${t('CfgTunName')}" />
              <input class="modal-input" data-k="localPort" type="number" value="${fw.localPort || ''}" placeholder="${t('CfgTunLocal')}" title="${t('CfgTunLocal')}" />
              <span class="cfg-tun-arrow">→</span>
              <input class="modal-input" data-k="remoteHost" value="${fw.remoteHost}" placeholder="${t('CfgTunRHost')}" title="${t('CfgTunRHost')}" />
              <input class="modal-input" data-k="remotePort" type="number" value="${fw.remotePort || ''}" placeholder="${t('CfgTunRPort')}" title="${t('CfgTunRPort')}" />
              <button class="icon-btn" type="button" title="${t('CfgDelete')}">✕</button>
            `;
            row.querySelector('[data-k="name"]').oninput = (e) => { fw.name = e.target.value; };
            row.querySelector('[data-k="localPort"]').oninput = (e) => { fw.localPort = parseInt(e.target.value, 10) || 0; };
            row.querySelector('[data-k="remoteHost"]').oninput = (e) => { fw.remoteHost = e.target.value; };
            row.querySelector('[data-k="remotePort"]').oninput = (e) => { fw.remotePort = parseInt(e.target.value, 10) || 0; };
            row.querySelector('button').onclick = () => { model.forwards.splice(idx, 1); redraw(); };
            tl.appendChild(row);
          });
        };
        redraw();
        document.getElementById('cfgBtnAddTun').onclick = () => {
          model.forwards.push({ name: '', localPort: 8080, remoteHost: '127.0.0.1', remotePort: 80 });
          redraw();
        };
      };
    }

    document.getElementById('cfgBtnBrowse').onclick = async () => {
      const res = await sendReq('pickIdentityFile', {});
      if (res?.path) document.getElementById('cfgInpKey').value = res.path;
    };

    document.getElementById('cfgBtnSave').onclick = async () => {
      const alias = document.getElementById('cfgInpAlias').value.trim();
      if (!alias) {
        document.getElementById('cfgStatusMsg').className = 'cfg-status-msg err';
        document.getElementById('cfgStatusMsg').textContent = t('CfgAliasReq');
        return;
      }
      const res = await sendReq('saveHostConfig', {
        originalAlias: model.originalAlias,
        hostAlias: alias,
        groupComment: document.getElementById('cfgInpComment').value.trim(),
        hostName: document.getElementById('cfgInpHostName').value.trim(),
        port: parseInt(document.getElementById('cfgInpPort').value, 10) || 22,
        user: document.getElementById('cfgInpUser').value.trim(),
        identityFile: document.getElementById('cfgInpKey').value.trim(),
        forwards: model.isBridge ? model.forwards.filter((f) => f.localPort > 0 && f.remotePort > 0) : []
      });
      if (res?.ok) {
        activeAlias = alias;
        setTimeout(() => { renderHostList(); renderForm(t('CfgSaved'), false); }, 120);
      } else {
        document.getElementById('cfgStatusMsg').className = 'cfg-status-msg err';
        document.getElementById('cfgStatusMsg').textContent = res?.error || 'Error';
      }
    };

    const delBtn = document.getElementById('cfgBtnDelete');
    if (delBtn) {
      delBtn.onclick = async () => {
        if (!confirm(t('CfgConfirmDel', existing.hostAlias))) return;
        const res = await sendReq('deleteHostConfig', { hostAlias: existing.hostAlias });
        if (res?.ok) {
          setTimeout(() => {
            activeAlias = state.hosts[0]?.hostAlias ?? '__new_ssh__';
            renderHostList();
            renderForm('', false);
          }, 120);
        }
      };
    }
  };

  document.getElementById('cfgBtnNewSsh').onclick = () => {
    activeAlias = '__new_ssh__';
    renderHostList();
    renderForm();
  };
  document.getElementById('cfgBtnNewBridge').onclick = () => {
    activeAlias = '__new_bridge__';
    renderHostList();
    renderForm();
  };

  state.refreshCfgList = renderHostList;
  renderHostList();
  renderForm();
};

/* INCOMING MESSAGES FROM C# */
window.chrome.webview.addEventListener('message', async (ev) => {
  const msg = ev.data;
  if (msg.reqId && state.pendingRequests.has(msg.reqId)) {
    const res = state.pendingRequests.get(msg.reqId);
    state.pendingRequests.delete(msg.reqId);
    res(msg);
    return;
  }

  switch (msg.type) {
    case 'state':
      state.lang = msg.lang;
      state.langs = msg.langs || [];
      state.i18n = msg.i18n || {};
      state.i18nFallback = msg.i18nFallback || {};
      state.hosts = msg.hosts || [];
      state.sshConfigExists = msg.sshConfigExists !== false;
      state.persistPasswords = !!msg.persistPasswords;
      const pm = document.getElementById('menuPersistPassMark');
      if (pm) pm.textContent = state.persistPasswords ? '☑' : '☐';
      state.appVersion = msg.version || '0.0.1';
      const verEl = document.getElementById('appVersion');
      verEl.textContent = `v${state.appVersion}`;
      verEl.title = t('ClickCheckUpd');
      document.getElementById('chkAutoReconnect').checked = !!msg.autoReconnect;
      renderLangSwitch();
      applyI18n();
      renderBridges();
      renderSessionsList();
      renderTermTabs();
      renderHome();
      if (document.getElementById('cfgEditorBackdrop')?.classList.contains('open') && typeof state.refreshCfgList === 'function') {
        state.refreshCfgList();
      }
      if (!state.autoUpdateChecked) {
        state.autoUpdateChecked = true;
        checkForUpdates(true);
      }
      break;

    case 'log':
      appendLog(msg.message);
      break;

    case 'needPassword': {
      const dlg = await openModal({
        title: t('PwdTitle', msg.hostAlias),
        desc: `${t('PwdPrompt', msg.hostAlias)} (${msg.info})`,
        isPassword: true,
        showRemember: true,
        okText: t('PwdConnect')
      });
      if (dlg && dlg.value) {
        post({ cmd: 'toggleBridge', hostAlias: msg.hostAlias, password: dlg.value, remember: dlg.remember });
      }
      break;
    }

    case 'termOutput': {
      const s = state.sessions.find((x) => x.id === msg.id);
      if (s?.term) {
        let out = msg.data;
        if (s.mode === 'ssh') {
          out = out.replace(/(^|\r?\n)(root@[^\s:@\x1b]+):([~\/][^\r\n\x1b$#]*)(# ?)/g,
            '$1\x1b[1;31m$2\x1b[0m:\x1b[1;34m$3\x1b[0m$4');
        }
        s.term.write(out);
      }
      break;
    }

    case 'termPasteText': {
      const s = state.sessions.find((x) => x.id === msg.id);
      if (s?.term && msg.text) {
        s.term.paste(msg.text);
        s.term.focus();
      }
      break;
    }

    case 'termAuthenticated': {
      const s = (msg.id ? state.sessions.find((x) => x.id === msg.id) : null)
        || state.sessions.find((x) => x.hostAlias === msg.hostAlias && x.mode === 'ssh' && !x.authenticated);
      if (s) s.authenticated = true;
      renderTermTabs();
      renderSessionsList();
      syncSftpSidebar(true);
      updateSysbarVisibility();
      pollSysStats();
      break;
    }

    case 'termSudoReady':
      state.waitingTerminalSudo = false;
      if (document.getElementById('chkTreeAdmin').checked) {
        syncSftpSidebar(false);
        refreshTree('/root');
      }
      break;

    case 'termCwdChanged':
      if (msg.id === state.activeTermId && !state.waitingTerminalSudo && Date.now() > state.suppressCwdSyncUntil && msg.path && state.treeRoot !== msg.path) {
        syncSftpSidebar(false);
        refreshTree(msg.path);
      }
      break;

    case 'termExit': {
      const s = state.sessions.find((x) => x.id === msg.id);
      if (s && s.mode === 'ssh' && (!s.authenticated || (msg.code !== undefined && msg.code !== 0))) {
        s.exited = true;
        s.failed = true;
        s.authenticated = false;
        s.term?.write(`\r\n\x1b[1;31m[SSH] ${t('TermConnFailed', msg.code ?? 1)}\x1b[0m\r\n`);
        renderTermTabs();
        renderSessionsList();
        updateSysbarVisibility();
      } else {
        closeSession(msg.id);
      }
      break;
    }
  }
});

document.getElementById('btnTheme').onclick = () => {
  applyTheme(state.theme === 'dark' ? 'light' : 'dark');
};

applyTheme(state.theme);
post({ cmd: 'init' });

/* MOBAXTERM-STYLE REMOTE SYSTEM MONITORING BAR */
const renderSysbar = (hostAlias, st) => {
  const bar = document.getElementById('sshSysbar');
  if (!bar) return;
  if (!st || !st.ok) return;

  const hist = state.cpuHistory.get(hostAlias) || [];
  hist.push(Number(st.cpuPct) || 0);
  if (hist.length > 22) hist.shift();
  state.cpuHistory.set(hostAlias, hist);

  const w = 44, h = 18;
  const pts = hist.map((v, i) => {
    const x = hist.length <= 1 ? w : (i / (hist.length - 1)) * w;
    const y = h - (Math.max(0, Math.min(100, v)) / 100) * (h - 2) - 1;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');

  const diskCells = (st.disks || [])
    .map((d) => `<div class="sysbar-cell" title="Využití disku (${d.mount}): ${d.pct}">${d.mount}: <b>${d.pct}</b></div>`)
    .join('');

  const icoClock = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>';
  const icoDisk = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2"><ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v6c0 1.66 3.58 3 8 3s8-1.34 8-3V6"/><path d="M4 12v6c0 1.66 3.58 3 8 3s8-1.34 8-3v-6"/></svg>';

  bar.innerHTML = `
    <div class="sysbar-cell" title="Název vzdáleného serveru (hostname) | OS: ${st.osId || 'linux'}">🐧 <b>${st.host || hostAlias}</b></div>
    <div class="sysbar-cell" title="Aktuální vytížení procesoru (CPU): ${st.cpuPct}% + graf posledních ~66 s">CPU <b>${st.cpuPct}%</b>
      <span class="sysbar-spark"><svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><polyline fill="none" stroke="#22c55e" stroke-width="1.4" points="${pts}" /></svg></span>
    </div>
    <div class="sysbar-cell" title="Operační paměť (RAM): využito ${st.ramUsedGb} GB z ${st.ramTotalGb} GB">RAM <b>${st.ramUsedGb} / ${st.ramTotalGb} GB</b></div>
    <div class="sysbar-cell" title="Odesílání ze serveru (Upload): ${st.txMbps} Mb/s"><span class="sysbar-up">↑ ${st.txMbps} Mb/s</span></div>
    <div class="sysbar-cell" title="Příjem na server (Download): ${st.rxMbps} Mb/s"><span class="sysbar-down">↓ ${st.rxMbps} Mb/s</span></div>
    <div class="sysbar-cell" title="Doba běhu serveru od posledního restartu (Uptime): ${st.uptime}">${icoClock} <b>${st.uptime}</b></div>
    <div class="sysbar-cell" title="Využití diskového prostoru na připojených svazcích">${icoDisk}</div>
    ${diskCells}
  `;
};

const updateSysbarVisibility = () => {
  const bar = document.getElementById('sshSysbar');
  if (!bar) return;
  const s = state.sessions.find((x) => x.id === state.activeTermId);
  const show = !!(s && s.mode === 'ssh' && s.authenticated);
  const wasShown = bar.classList.contains('active');
  const prevHost = bar.dataset.hostAlias || '';
  bar.classList.toggle('active', show);
  if (show) {
    if (!wasShown || prevHost !== s.hostAlias) {
      bar.dataset.hostAlias = s.hostAlias;
      bar.innerHTML = '';
      if (s.fit) setTimeout(() => s.fit.fit(), 30);
      pollSysStats();
    }
  } else if (wasShown) {
    bar.dataset.hostAlias = '';
    bar.innerHTML = '';
    if (s?.fit) setTimeout(() => s.fit.fit(), 30);
  }
};

const pollSysStats = async () => {
  if (state.activeMainTab !== 'terminals') return;
  const s = state.sessions.find((x) => x.id === state.activeTermId);
  if (!s || s.mode !== 'ssh' || !s.authenticated) return;
  const res = await sendReq('sshSysStats', { hostAlias: s.hostAlias });
  const cur = state.sessions.find((x) => x.id === state.activeTermId);
  if (cur && cur.hostAlias === s.hostAlias && res?.stats?.ok) {
    renderSysbar(s.hostAlias, res.stats);
  }
};

setInterval(pollSysStats, 3000);

const checkForUpdates = async (isAuto = false) => {
  if (!isAuto) {
    state.updateStatus = { checking: true };
    renderHome();
  }
  const res = await sendReq('checkUpdate', {});
  state.updateStatus = res || { ok: false, error: 'Neznámá chyba' };

  const verEl = document.getElementById('appVersion');
  if (verEl) {
    if (res?.ok && res.hasUpdate) {
      verEl.textContent = `v${state.appVersion} ⬆`;
      verEl.classList.add('has-update');
      verEl.title = t('HomeUpdAvail', res.latestVersion);
    } else {
      verEl.textContent = `v${state.appVersion}`;
      verEl.classList.remove('has-update');
      verEl.title = t('ClickCheckUpd');
    }
  }

  renderHome();

  if (res?.ok && res.hasUpdate) {
    const dlg = await openModal({
      title: t('UpdModalTitle', res.latestVersion),
      desc: t('UpdModalDesc', res.latestVersion, state.appVersion),
      showRemember: false,
      hideInput: true,
      okText: t('UpdModalOk', res.latestVersion)
    });
    if (dlg) {
      post({ cmd: 'openUrl', url: res.downloadUrl || res.releaseUrl });
    }
  }
};

document.getElementById('appVersion').onclick = () => {
  switchMainTab('home');
  checkForUpdates(false);
};

/* HOME DASHBOARD */
const renderHome = () => {
  const wrap = document.getElementById('homeWrap');
  if (!wrap) return;

  const pureHosts = state.hosts.filter((h) => !h.hasForwards);
  const displayHosts = pureHosts.length > 0 ? pureHosts : state.hosts;
  const tunnels = state.hosts.filter((h) => h.hasForwards);
  const runningBridges = tunnels.filter((h) => h.forwards.length > 0 && h.forwards.every((f) => f.isListening)).length;
  const activeSessions = state.sessions.filter((s) => s.mode === 'ssh' || s.mode === 'sftp').length;

  const u = state.updateStatus;
  let updateBadge = `<span class="update-pill">${t('HomeUpdCur', state.appVersion)}</span>`;
  let downloadBtn = '';
  if (u?.checking) {
    updateBadge = `<span class="update-pill checking">${t('HomeUpdChecking')}</span>`;
  } else if (u && u.ok === false) {
    updateBadge = `<span class="update-pill err">${t('HomeUpdErr', u.error || '?')}</span>`;
  } else if (u && u.ok) {
    if (u.hasUpdate) {
      updateBadge = `<span class="update-pill avail">${t('HomeUpdAvail', u.latestVersion)}</span>`;
      downloadBtn = `<button class="btn btn-sm btn-emerald" id="homeBtnDownloadUpd">${t('HomeUpdDownload', u.latestVersion)}</button>`;
    } else if (u.noRelease) {
      updateBadge = `<span class="update-pill ok">${t('HomeUpdNoRel', state.appVersion)}</span>`;
    } else {
      updateBadge = `<span class="update-pill ok">${t('HomeUpdLatest', state.appVersion)}</span>`;
    }
  }

  wrap.innerHTML = `
    <div class="home-hero">
      <div>
        <h1>Pontish <span class="version-pill">v${state.appVersion}</span></h1>
        <p>${t('HomeSubtitle')}</p>
      </div>
      <div class="home-hero-stats">
        <div class="home-stat"><b>${displayHosts.length}</b><span>${t('HomeStatServers')}</span></div>
        <div class="home-stat"><b>${activeSessions}</b><span>${t('HomeStatSessions')}</span></div>
        <div class="home-stat"><b>${runningBridges}/${tunnels.length}</b><span>${t('HomeStatBridges')}</span></div>
      </div>
    </div>

    <div class="home-update-bar">
      <div class="home-update-left">
        <span class="home-tools-label">${t('HomeUpdLabel')}</span>
        ${updateBadge}
      </div>
      <div class="home-update-right">
        ${downloadBtn}
        <button class="btn btn-sm btn-ghost" id="homeBtnCheckUpd" ${u?.checking ? 'disabled' : ''}>${t('HomeUpdBtn')}</button>
      </div>
    </div>

    ${!state.sshConfigExists ? `
    <div class="home-update-bar" style="border-color:rgba(16,185,129,0.4);">
      <div class="home-update-left">
        <span class="home-tools-label">${t('CfgMissingBanner')}</span>
      </div>
      <div class="home-update-right">
        <button class="btn btn-sm btn-emerald" id="homeBtnCreateCfg" type="button">${t('CfgCreateBtn')}</button>
      </div>
    </div>` : ''}

    <div class="home-grid">
      <div class="home-card">
        <div class="home-card-hdr">
          <div>
            <div class="home-card-title">${t('HomeTermTitle')}</div>
            <div class="home-card-sub">${t('HomeTermSub')}</div>
          </div>
        </div>
        <div class="home-quick-list" id="homeHostsList"></div>
        <div class="home-card-footer">
          <button class="btn btn-blue" id="homeGoTerminals">${t('HomeGoTerminals')}</button>
        </div>
      </div>

      <div class="home-card">
        <div class="home-card-hdr">
          <div>
            <div class="home-card-title">${ICON_BRIDGE} ${t('HomeBridgesTitle')}</div>
            <div class="home-card-sub">${t('HomeBridgesSub')}</div>
          </div>
        </div>
        <div class="home-quick-list" id="homeBridgesList"></div>
        <div class="home-card-footer">
          <button class="btn btn-emerald" id="homeGoBridges">${t('HomeGoBridges')}</button>
        </div>
      </div>
    </div>
  `;

  const hostsList = document.getElementById('homeHostsList');
  if (displayHosts.length === 0) {
    hostsList.innerHTML = `<div class="home-card-sub">${t('HomeNoServers')}</div>`;
  } else {
    displayHosts.forEach((h) => {
      const hs = state.sessions.filter((s) => s.hostAlias === h.hostAlias && (s.mode === 'ssh' || s.mode === 'sftp'));
      const okCount = hs.filter((s) => s.authenticated).length;
      const connCount = hs.filter((s) => !s.authenticated).length;
      const icoCls = okCount > 0 ? 'active' : (connCount > 0 ? 'connecting' : 'idle');

      const row = document.createElement('div');
      row.className = 'home-quick-item';
      row.innerHTML = `
        <div class="home-quick-info">
          <span class="session-srv-ico ${icoCls}">${ICON_SERVER}</span>
          <div style="min-width:0;">
            <div class="home-quick-name">${h.hostAlias}</div>
            <div class="home-quick-meta">${h.user}@${h.hostName}:${h.port}</div>
          </div>
        </div>
        <div class="home-quick-actions">
          <button class="btn btn-sm btn-blue" data-act="ssh">SSH</button>
          <button class="btn btn-sm btn-ghost" data-act="sftp">SFTP</button>
        </div>
      `;
      row.querySelector('[data-act="ssh"]').onclick = () => openSession('ssh', h.hostAlias, h);
      row.querySelector('[data-act="sftp"]').onclick = () => openSession('sftp', h.hostAlias, h);
      hostsList.appendChild(row);
    });
  }

  const bridgesList = document.getElementById('homeBridgesList');
  if (tunnels.length === 0) {
    bridgesList.innerHTML = `<div class="home-card-sub">${t('HomeNoBridges')}</div>`;
  } else {
    tunnels.forEach((h) => {
      const total = h.forwards.length;
      const listen = h.forwards.filter((f) => f.isListening).length;
      const isFull = total > 0 && listen === total;
      const isBusy = h.userRequestedRun || h.isConnecting || listen > 0;
      const row = document.createElement('div');
      row.className = 'home-quick-item';
      row.innerHTML = `
        <div class="home-quick-info">
          <span class="fw-dot ${isFull ? 'on' : ''}"></span>
          <div style="min-width:0;">
            <div class="home-quick-name">${h.hostAlias} (${listen}/${total})</div>
            <div class="home-quick-meta">${h.user}@${h.hostName}:${h.port}</div>
          </div>
        </div>
        <div class="home-quick-actions">
          <button class="btn btn-sm ${isBusy ? 'btn-coral' : 'btn-emerald'}">${isBusy ? t('BtnTurnOff') : t('BtnTurnOn')}</button>
        </div>
      `;
      row.querySelector('button').onclick = () => post({ cmd: 'toggleBridge', hostAlias: h.hostAlias });
      bridgesList.appendChild(row);
    });
  }

  document.getElementById('homeBtnCheckUpd').onclick = () => checkForUpdates();
  const homeCreateCfgBtn = document.getElementById('homeBtnCreateCfg');
  if (homeCreateCfgBtn) homeCreateCfgBtn.onclick = () => post({ cmd: 'createSshConfig' });
  const dlBtn = document.getElementById('homeBtnDownloadUpd');
  if (dlBtn && u) {
    dlBtn.onclick = () => post({ cmd: 'openUrl', url: u.downloadUrl || u.releaseUrl });
  }
  document.getElementById('homeGoTerminals').onclick = () => switchMainTab('terminals');
  document.getElementById('homeGoBridges').onclick = () => switchMainTab('bridges');
};
