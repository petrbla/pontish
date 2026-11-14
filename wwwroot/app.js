const post = (obj) => window.chrome.webview.postMessage(obj);

const state = {
  lang: 'cs',
  theme: localStorage.getItem('pontish_theme') || 'dark',
  langs: [],
  i18n: {},
  hosts: [],
  activeMainTab: 'bridges',
  activeSideTab: 'sessions',
  sessions: [], // { id, title, mode, hostAlias, term, fit, paneEl, sftpState }
  activeTermId: null,
  seq: 0,
  treeConn: null,
  treeRoot: '~',
  treeSelectedNode: null,
  pendingRequests: new Map(),
  reqSeq: 0,
  modalResolve: null
};

const XTERM_THEMES = {
  dark: {
    background: '#050811',
    foreground: '#e2e8f0',
    cursor: '#06b6d4',
    selectionBackground: 'rgba(59, 130, 246, 0.35)',
    black: '#0f172a', red: '#ef4444', green: '#10b981', yellow: '#f59e0b',
    blue: '#3b82f6', magenta: '#a855f7', cyan: '#06b6d4', white: '#e2e8f0'
  },
  light: {
    background: '#ffffff',
    foreground: '#0f172a',
    cursor: '#0284c7',
    selectionBackground: 'rgba(37, 99, 235, 0.22)',
    black: '#1e293b', red: '#dc2626', green: '#059669', yellow: '#b45309',
    blue: '#2563eb', magenta: '#7c3aed', cyan: '#0284c7', white: '#f1f5f9'
  }
};

const applyTheme = (theme) => {
  state.theme = theme === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', state.theme);
  localStorage.setItem('pontish_theme', state.theme);
  const btn = document.getElementById('btnTheme');
  if (btn) btn.textContent = state.theme === 'light' ? '🌙' : '☀️';
  state.sessions.forEach((s) => {
    if (s.term) s.term.options.theme = XTERM_THEMES[state.theme];
  });
  post({ cmd: 'setTheme', theme: state.theme });
};

const t = (key, ...args) => {
  let s = state.i18n[key] || key;
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

const openModal = ({ title, desc, isPassword = true, showRemember = true, okText = 'OK' }) => {
  return new Promise((resolve) => {
    state.modalResolve = resolve;
    document.getElementById('modalTitle').textContent = title;
    document.getElementById('modalDesc').textContent = desc;
    const inp = document.getElementById('modalInput');
    inp.type = isPassword ? 'password' : 'text';
    inp.value = '';
    document.getElementById('modalRememberWrap').style.display = showRemember ? 'inline-flex' : 'none';
    document.getElementById('modalOk').textContent = okText;
    document.getElementById('modalBackdrop').classList.add('open');
    setTimeout(() => inp.focus(), 50);
  });
};

const closeModal = (val) => {
  document.getElementById('modalBackdrop').classList.remove('open');
  if (state.modalResolve) {
    state.modalResolve(val);
    state.modalResolve = null;
  }
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
  document.getElementById('lblTabBridges').textContent = t('TabBridges', tunnels.length);
  document.getElementById('lblTabTerminals').textContent = t('TabTerminals', state.sessions.length);
  document.getElementById('headerSummary').textContent = t('HeaderSummary', activeCount, tunnels.length);
  document.getElementById('shieldOrb').classList.toggle('active', activeCount > 0);
};

const renderLangSwitch = () => {
  const wrap = document.getElementById('langSwitch');
  wrap.innerHTML = '';
  state.langs.forEach((l) => {
    const b = document.createElement('button');
    b.className = `lang-btn ${l.code === state.lang ? 'active' : ''}`;
    b.textContent = l.code.toUpperCase();
    b.title = l.name;
    b.onclick = () => post({ cmd: 'setLang', lang: l.code });
    wrap.appendChild(b);
  });
};

const renderBridges = () => {
  const grid = document.getElementById('bridgesGrid');
  grid.innerHTML = '';
  const tunnels = state.hosts.filter((h) => h.hasForwards);

  tunnels.forEach((h) => {
    const total = h.forwards.length;
    const listenCount = h.forwards.filter((f) => f.isListening).length;
    const isFull = total > 0 && listenCount === total;
    const isPartial = listenCount > 0 && listenCount < total;

    const card = document.createElement('div');
    card.className = `bridge-card ${isFull ? 'running' : ''}`;

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
      <div>
        <div class="bridge-title">${h.hostAlias}</div>
        <div class="bridge-meta">${h.user}@${h.hostName}:${h.port}${h.groupComment ? ' &nbsp;|&nbsp; ' + h.groupComment : ''}</div>
      </div>
      <div class="bridge-right">
        <span class="status-pill ${statusCls}">${statusText}</span>
        <button class="btn ${btnCls}">${btnText}</button>
      </div>
    `;
    hdr.querySelector('button').onclick = () => post({ cmd: 'toggleBridge', hostAlias: h.hostAlias });
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

const renderSessionsList = () => {
  const list = document.getElementById('sessionsList');
  list.innerHTML = '';

  const pureHosts = state.hosts.filter((h) => !h.hasForwards);
  const displayHosts = pureHosts.length > 0 ? pureHosts : state.hosts;

  displayHosts.forEach((h) => {
    const item = document.createElement('div');
    item.className = 'session-item';
    item.innerHTML = `
      <div>
        <div class="session-name">&gt;_ ${h.hostAlias}</div>
        <div class="session-sub">${h.user}@${h.hostName}:${h.port}</div>
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
  document.getElementById('tabBridges').classList.toggle('active', tab === 'bridges');
  document.getElementById('tabTerminals').classList.toggle('active', tab === 'terminals');
  document.getElementById('viewBridges').classList.toggle('active', tab === 'bridges');
  document.getElementById('viewTerminals').classList.toggle('active', tab === 'terminals');
  document.getElementById('bridgeGlobalBtns').style.display = tab === 'bridges' ? 'flex' : 'none';
  if (tab === 'terminals') {
    const active = state.sessions.find((s) => s.id === state.activeTermId);
    if (active?.fit) setTimeout(() => { active.fit.fit(); active.term.focus(); }, 40);
  }
};

document.getElementById('tabBridges').onclick = () => switchMainTab('bridges');
document.getElementById('tabTerminals').onclick = () => switchMainTab('terminals');

const switchSideTab = (side) => {
  state.activeSideTab = side;
  document.getElementById('sideTabSessions').classList.toggle('active', side === 'sessions');
  document.getElementById('sideTabFiles').classList.toggle('active', side === 'files');
  document.getElementById('paneSessions').classList.toggle('active', side === 'sessions');
  document.getElementById('paneFiles').classList.toggle('active', side === 'files');
  if (side === 'files') refreshTree();
};

document.getElementById('sideTabSessions').onclick = () => switchSideTab('sessions');
document.getElementById('sideTabFiles').onclick = () => switchSideTab('files');

const renderTermTabs = () => {
  const bar = document.getElementById('termTabbar');
  bar.innerHTML = '';
  document.getElementById('emptyWorkspace').style.display = state.sessions.length === 0 ? 'flex' : 'none';
  document.getElementById('lblTabTerminals').textContent = t('TabTerminals', state.sessions.length);

  state.sessions.forEach((s) => {
    const tab = document.createElement('div');
    tab.className = `term-tab ${s.id === state.activeTermId ? 'active' : ''}`;
    tab.innerHTML = `<span>${s.title}</span><button class="term-tab-close">✕</button>`;
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
  if (state.activeSideTab === 'files') refreshTree();
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
  if (state.activeTermId) activateSession(state.activeTermId);
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
    const asRootInit = document.getElementById('chkAdminRoot').checked;
    const sess = { id, title, mode, hostAlias, hostObj, paneEl, asRoot: asRootInit };
    state.sessions.push(sess);
    switchMainTab('terminals');
    activateSession(id);
    await buildDualSftpUI(sess);
    return;
  }

  const wrap = document.createElement('div');
  wrap.className = 'xterm-wrap';
  paneEl.appendChild(wrap);

  const term = new Terminal({
    cursorBlink: true,
    fontFamily: '"Cascadia Mono", "Consolas", monospace',
    fontSize: 13.5,
    theme: XTERM_THEMES[state.theme]
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(wrap);

  const sess = { id, title, mode, hostAlias, hostObj, term, fit, paneEl };
  state.sessions.push(sess);
  switchMainTab('terminals');
  activateSession(id);

  setTimeout(() => {
    fit.fit();
    const asRoot = mode === 'ssh' && document.getElementById('chkAdminRoot').checked;
    post({ cmd: 'termStart', id, mode, hostAlias, asRoot, cols: term.cols, rows: term.rows });
  }, 50);

  term.onData((data) => post({ cmd: 'termInput', id, data }));
  new ResizeObserver(() => {
    if (state.activeTermId === id && state.activeMainTab === 'terminals') {
      fit.fit();
      post({ cmd: 'termResize', id, cols: term.cols, rows: term.rows });
    }
  }).observe(wrap);
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
    const res = await sendReq('sftpListRemote', { hostAlias: sess.hostAlias, path, asRoot: sess.asRoot });
    if (!res.ok) return;
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

const renderTreeNodes = (container, hostAlias, entries, asRoot) => {
  container.innerHTML = '';
  entries.filter((e) => e.type !== 'up').forEach((en) => {
    const row = document.createElement('div');
    row.className = `tree-node ${en.type}`;
    row.textContent = en.type === 'dir' ? `▸ 📁 ${en.name}` : `   📄 ${en.name} (${en.sizeText})`;

    const children = document.createElement('div');
    children.className = 'tree-children';
    children.style.display = 'none';
    let loaded = false;

    row.onclick = async () => {
      if (en.type === 'dir') {
        if (children.style.display === 'none') {
          if (!loaded) {
            const res = await sendReq('sftpListRemote', { hostAlias, path: en.path, asRoot });
            if (res.ok) renderTreeNodes(children, hostAlias, res.entries, asRoot);
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
          if (active) post({ cmd: 'termInput', id: active.id, data: `cd '${en.path.replace(/'/g, "'\\''")}'\r` });
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
    document.getElementById('chkAdminRoot').checked = false;
  }
  const target = startPath || (asRoot ? '/root' : '~');
  state.treeRoot = target;
  const res = await sendReq('sftpListRemote', { hostAlias, path: target, asRoot });
  if (res.ok) {
    document.getElementById('treeStatus').textContent = `${hostAlias}${asRoot ? ' [root]' : ''}: ${res.path}`;
    renderTreeNodes(box, hostAlias, res.entries, asRoot);
  }
};

document.getElementById('btnTreeRefresh').onclick = () => refreshTree(state.treeRoot);
document.getElementById('btnTreeHome').onclick = () => refreshTree(document.getElementById('chkTreeAdmin').checked ? '/root' : '~');
document.getElementById('btnTreeRoot').onclick = () => refreshTree('/');

document.getElementById('chkAdminRoot').onchange = (e) => {
  document.getElementById('chkTreeAdmin').checked = e.target.checked;
};
document.getElementById('chkTreeAdmin').onchange = (e) => {
  document.getElementById('chkAdminRoot').checked = e.target.checked;
  if (state.activeSideTab === 'files') refreshTree(e.target.checked ? '/root' : '~');
};

document.addEventListener('click', () => document.getElementById('treeCtxMenu').classList.remove('open'));
document.getElementById('ctxCd').onclick = () => {
  const sel = state.treeSelectedNode;
  if (!sel || sel.entry.type !== 'dir') return;
  const active = state.sessions.find((s) => s.id === state.activeTermId && s.mode === 'ssh');
  if (active) post({ cmd: 'termInput', id: active.id, data: `cd '${sel.entry.path.replace(/'/g, "'\\''")}'\r` });
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
document.getElementById('btnStartAll').onclick = () => post({ cmd: 'startAll' });
document.getElementById('btnStopAll').onclick = () => post({ cmd: 'stopAll' });
document.getElementById('btnReload').onclick = () => post({ cmd: 'reloadConfig' });
document.getElementById('btnForgetPass').onclick = () => post({ cmd: 'forgetPasswords' });
document.getElementById('btnExportImport').onclick = async () => {
  const exp = confirm('OK = Exportovat zálohu (.zip)\nStorno = Importovat zálohu (.zip / config)');
  if (exp) post({ cmd: 'exportBundle', allKeys: true });
  else post({ cmd: 'importBundle' });
};
document.getElementById('btnEditConfig').onclick = () => post({ cmd: 'editConfig' });
document.getElementById('chkAutoReconnect').onchange = (e) => post({ cmd: 'setAutoReconnect', enabled: e.target.checked });
document.getElementById('btnQuit').onclick = () => post({ cmd: 'quitApp' });
document.getElementById('btnToggleLog').onclick = () => {
  const dr = document.getElementById('logDrawer');
  dr.classList.toggle('open');
  document.getElementById('logArrow').textContent = dr.classList.contains('open') ? '▼' : '▲';
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
      state.hosts = msg.hosts || [];
      document.getElementById('appVersion').textContent = `v${msg.version}`;
      document.getElementById('chkAutoReconnect').checked = !!msg.autoReconnect;
      renderLangSwitch();
      applyI18n();
      renderBridges();
      renderSessionsList();
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
      if (s?.term) s.term.write(msg.data);
      break;
    }

    case 'termExit': {
      closeSession(msg.id);
      break;
    }
  }
});

document.getElementById('btnTheme').onclick = () => {
  applyTheme(state.theme === 'dark' ? 'light' : 'dark');
};

applyTheme(state.theme);
post({ cmd: 'init' });
