(function () {
  'use strict';
  var F = S4Flat;
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  F.setAccent('#14b8a6');

  var TOKEN_KEY = 'relaunch.token', CACHE_KEY = 'relaunch.cache';
  function read(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
  function write(k, v) { try { if (v === null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch (e) { /* storage unavailable */ } }

  var S = {
    token: read(TOKEN_KEY), apps: [], byId: {}, favIds: [], pc: 'offline', session: 'unknown', pcName: '',
    online: navigator.onLine !== false, view: 'fav', busy: {}, current: null, liveOk: false
  };
  var tileEls = {}, slotEls = {}, tileKey = '', rowKey = '';
  var PAST = { start: 'started', stop: 'stopped', restart: 'restarted' };
  var ING = { start: 'Starting', stop: 'Stopping', restart: 'Restarting' };

  /* ───────── API ───────── */
  function api(path, opts) {
    opts = opts || {};
    opts.headers = Object.assign({ Authorization: 'Bearer ' + S.token }, opts.headers || {});
    return fetch(path, opts).then(function (r) {
      if (r.status === 401) { unpair(true); throw new Error('unauthorized'); }
      return r;
    });
  }
  function asJson(r) { return r.json(); }

  function load() {
    if (!S.token) return Promise.resolve();
    return Promise.all([
      api('/api/status').then(asJson),
      api('/api/apps').then(asJson),
      api('/api/apps?favorites=true').then(asJson)
    ]).then(function (res) {
      S.online = true;
      S.pcName = res[0].pcName || S.pcName;
      S.session = res[0].session;
      S.pc = res[1].pc;
      setApps(res[1].apps);
      S.favIds = res[2].apps.map(function (a) { return a.id; });
      saveCache();
      render();
      if (S.view === 'act') loadActivity();
    }).catch(function (e) {
      if (e.message === 'unauthorized') return;
      S.online = false;
      loadCache();
      render();
    });
  }

  function setApps(list) {
    S.apps = list;
    S.byId = {};
    list.forEach(function (a) { S.byId[a.id] = a; });
  }
  function saveCache() {
    write(CACHE_KEY, JSON.stringify({ apps: S.apps, favIds: S.favIds, pcName: S.pcName, session: S.session }));
  }
  function loadCache() {
    var raw = read(CACHE_KEY);
    if (!raw) return;
    try {
      var c = JSON.parse(raw);
      setApps(c.apps || []);
      S.favIds = c.favIds || [];
      S.pcName = c.pcName || S.pcName;
      S.session = c.session || S.session;
      S.pc = 'offline';
    } catch (e) { /* ignore a damaged cache */ }
  }

  /* ───────── helpers ───────── */
  function canAct() { return S.online && S.pc === 'online' && (S.session === 'active' || S.session === 'locked'); }

  function wordOf(a) {
    if (a.phase === 'stopping') return 'Stopping';
    if (a.phase === 'starting') return 'Starting';
    if (S.busy[a.id]) return ING[S.busy[a.id]];
    if (!a.trackable) return 'Ready';
    return a.status.running ? 'Running' : 'Stopped';
  }
  function isBusy(a) { return a.phase === 'stopping' || a.phase === 'starting' || !!S.busy[a.id]; }
  function pillClass(word) { return word === 'Running' ? 'ok' : (word === 'Stopped' || word === 'Ready') ? '' : 'info'; }

  function fmtUp(sec) {
    if (sec < 60) return 'Under a minute';
    var m = Math.floor(sec / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
    if (d > 0) return d + 'd ' + (h % 24) + 'h';
    if (h > 0) return h + 'h ' + (m % 60) + 'm';
    return m + ' min';
  }
  function fmtMem(bytes) { return F.fmt.num(Math.round(bytes / 1048576)) + ' MB'; }
  function subOf(a) {
    if (!a.trackable) return 'Start only';
    if (!a.status.running) return 'Not running';
    return 'Up ' + fmtUp(a.status.uptimeSeconds).toLowerCase();
  }
  function initial(name) { return (name.match(/[A-Za-z0-9]/) || ['?'])[0].toUpperCase(); }
  function setWord(el, text) { if (el.textContent !== text) F.text(el, text); }

  /* ───────── icons ───────── */
  var iconUrls = {}, iconQueue = [], iconActive = 0;
  function loadIcon(app) {
    var h = app.iconHash;
    if (!h) return Promise.resolve(null);
    if (iconUrls[h] !== undefined) return Promise.resolve(iconUrls[h]);
    iconUrls[h] = new Promise(function (resolve) {
      iconQueue.push(function (done) {
        api('/api/apps/' + app.id + '/icon').then(function (r) { return r.ok ? r.blob() : null; })
          .then(function (b) { var u = b ? URL.createObjectURL(b) : null; iconUrls[h] = u; resolve(u); done(); })
          .catch(function () { delete iconUrls[h]; resolve(null); done(); });
      });
      pumpIcons();
    });
    return iconUrls[h];
  }
  function pumpIcons() {
    while (iconActive < 4 && iconQueue.length) {
      iconActive++;
      iconQueue.shift()(function () { iconActive--; pumpIcons(); });
    }
  }
  function paintIcon(el) {
    var a = S.byId[el.getAttribute('data-id')];
    if (!a) return;
    if (!el.firstChild) el.textContent = initial(a.name);
    if (!a.iconHash || el.getAttribute('data-hash') === a.iconHash) return;
    el.setAttribute('data-hash', a.iconHash);
    Promise.resolve(loadIcon(a)).then(function (url) {
      if (!url || el.getAttribute('data-hash') !== a.iconHash) return;
      var img = new Image();
      img.alt = '';
      img.src = url;
      el.textContent = '';
      el.appendChild(img);
    });
  }
  function paintIcons() { $$('.app-ico[data-id]').forEach(paintIcon); }

  /* ───────── rendering ───────── */
  function render() {
    renderStatus();
    var paired = !!S.token, dock = $('.f-dock');
    if (dock.hidden === paired) {
      dock.hidden = !paired;
      // The nav bead is measured while visible; re-glide once the dock appears.
      if (paired) requestAnimationFrame(function () { F.glideAll(); });
    }
    if (!paired) return;
    renderTiles();
    renderRows();
    if (S.current) fillCard(false);
    paintIcons();
  }

  function renderStatus() {
    var pill = $('#connPill'), banner = $('#banner'), text = '', word, cls, note = '';
    if (!S.token) { pill.hidden = true; banner.hidden = true; return; }
    pill.hidden = false;
    if (!S.online) { word = 'No Network'; cls = 'bad'; note = 'No connection. Showing the last known apps.'; }
    else if (S.pc !== 'online') { word = 'Offline'; cls = 'bad'; note = 'PC is offline. Wake it with your Wake-on-LAN app.'; }
    else if (S.session === 'noSession') { word = 'No Session'; cls = 'warn'; note = 'Nobody is logged in on the PC, so apps can’t be started.'; }
    else if (S.session === 'locked') { word = 'Locked'; cls = 'info'; }
    else if (S.session === 'starting') { word = 'Starting'; cls = 'info'; note = 'The PC helper is starting up.'; }
    else { word = 'Online'; cls = 'ok'; }
    pill.className = 'f-pill ' + cls;
    setWord(pill, word);
    setWord($('#pcName'), S.pcName || 'reLAUNCH');
    text = note;
    if (text) { setWord($('#bannerText'), text); banner.hidden = false; } else banner.hidden = true;
  }

  function renderTiles() {
    var box = $('#tiles'), ids = S.favIds.filter(function (id) { return S.byId[id]; });
    var key = ids.join(',');
    if (key !== tileKey) {
      tileKey = key;
      box.innerHTML = '';
      tileEls = {};
      ids.forEach(function (id) {
        var a = S.byId[id], b = document.createElement('button');
        b.className = 'f-tile f-tap';
        b.setAttribute('data-id', id);
        b.innerHTML = '<span class="app-ico" data-id="' + id + '"></span><span class="k"></span><span class="v"></span><span class="s"></span>';
        $('.k', b).textContent = a.name;
        b.addEventListener('click', function () { openCard(id, b, null); });
        tileEls[id] = b;
        box.appendChild(b);
      });
    }
    ids.forEach(function (id) {
      var a = S.byId[id], b = tileEls[id];
      setWord($('.v', b), wordOf(a));
      setWord($('.s', b), subOf(a));
      b.classList.toggle('off', !canAct());
      b.classList.toggle('busy', isBusy(a));
    });
    var empty = ids.length === 0;
    $('#favEmpty').hidden = !empty;
    $('#favEmptyText').textContent = S.apps.length ? 'Star apps in All Apps to pin them here.' : 'No apps yet. They appear once the PC has connected.';
  }

  function renderRows() {
    var list = $('#rows'), key = S.apps.map(function (a) { return a.id; }).join(',');
    if (key !== rowKey) {
      rowKey = key;
      list.innerHTML = '';
      slotEls = {};
      S.apps.forEach(function (a) { var slot = buildSlot(a); slotEls[a.id] = slot; list.appendChild(slot); });
      applyFilter(false);
    }
    S.apps.forEach(function (a) {
      var slot = slotEls[a.id];
      if (!slot) return;
      var word = wordOf(a), pill = $('.f-pill', slot), fav = S.favIds.indexOf(a.id) > -1, star = $('.star', slot);
      pill.className = 'f-pill ' + pillClass(word);
      setWord(pill, word);
      setWord($('.m', slot), subOf(a));
      $('.f-rowplate', slot).classList.toggle('off', !canAct());
      if (star.classList.contains('on') !== fav) { star.classList.toggle('on', fav); F.inkCenter(star, fav); }
      star.setAttribute('aria-pressed', fav);
      $('use', star).setAttribute('href', fav ? '#ri-star-fill' : '#ri-star-line');
    });
  }

  function buildSlot(a) {
    var slot = document.createElement('div');
    slot.className = 'f-slot';
    slot.setAttribute('data-id', a.id);
    slot.innerHTML =
      '<div class="f-behind"><svg class="f-i sm"><use href="#ri-arrow-right-line"/></svg>Start</div>' +
      '<div class="f-rowplate"><button class="f-row has-star f-tap"><span class="app-ico" data-id="' + a.id + '"></span>' +
      '<span class="main"><span class="t"></span><span class="m"></span></span><span class="side"><span class="f-pill"></span></span></button>' +
      '<button class="f-chip f-ink f-tap star" aria-label="Favorite" aria-pressed="false"><svg class="f-i sm"><use href="#ri-star-line"/></svg></button></div>';
    $('.t', slot).textContent = a.name;
    var plate = $('.f-rowplate', slot), row = $('.f-row', slot), star = $('.star', slot);
    row.addEventListener('click', function () { openCard(a.id, row, plate); });
    star.addEventListener('click', function () { toggleFavorite(a.id); });
    F.swipe(slot, function () { act(a.id, 'start'); return 'stay'; }, function () {
      var cur = S.byId[a.id];
      return !!cur && canAct() && !isBusy(cur) && !(cur.trackable && cur.status.running);
    });
    return slot;
  }

  var query = '';
  function applyFilter(animate) {
    var slots = S.apps.map(function (a) { return slotEls[a.id]; }).filter(Boolean);
    var vis = function (slot) {
      var a = S.byId[slot.getAttribute('data-id')];
      return !query || (a && a.name.toLowerCase().indexOf(query) > -1);
    };
    if (animate) F.reflow($('#rows'), slots, vis, 'fade');
    else slots.forEach(function (s) { s.hidden = !vis(s); });
    $('#rowsEmpty').hidden = slots.some(vis);
  }
  $('#q').addEventListener('input', function () { query = this.value.trim().toLowerCase(); applyFilter(true); });

  /* ───────── activity ───────── */
  var rtf = window.Intl && Intl.RelativeTimeFormat ? new Intl.RelativeTimeFormat('en', { numeric: 'auto' }) : null;
  function ago(ts) {
    var s = Math.round((ts - Date.now()) / 1000), a = Math.abs(s);
    if (!rtf) return new Date(ts).toLocaleString();
    if (a < 45) return 'Just now';
    if (a < 3600) return rtf.format(Math.round(s / 60), 'minute');
    if (a < 86400) return rtf.format(Math.round(s / 3600), 'hour');
    return rtf.format(Math.round(s / 86400), 'day');
  }
  var RESULT_WORDS = { ok: 'Done', noSession: 'No Session', pcOffline: 'PC Offline', timeout: 'Timed Out', busy: 'Busy', startTimeout: 'Not Started', stopFailed: 'Not Stopped', elevationRequired: 'Needs Admin', unknownApp: 'Unknown App', tooManyActions: 'Too Fast' };
  function loadActivity() {
    if (!S.token) return Promise.resolve();
    return api('/api/activity?limit=50').then(asJson).then(function (j) {
      var box = $('#actRows');
      box.innerHTML = '';
      j.activity.forEach(function (r) {
        var slot = document.createElement('div');
        slot.className = 'f-slot';
        slot.innerHTML = '<div class="f-rowplate"><div class="f-row"><span class="app-ico" data-id="' + r.appId + '"></span>' +
          '<span class="main"><span class="t"></span><span class="m"></span></span><span class="side"><span class="f-pill"></span></span></div></div>';
        $('.t', slot).textContent = r.appName + ' ' + (PAST[r.action] || r.action);
        $('.m', slot).textContent = r.deviceName + ' · ' + ago(r.time);
        var pill = $('.f-pill', slot);
        pill.className = 'f-pill ' + (r.result === 'ok' ? 'ok' : 'bad');
        pill.textContent = RESULT_WORDS[r.result] || r.result;
        box.appendChild(slot);
      });
      $('#actEmpty').hidden = j.activity.length > 0;
      paintIcons();
    }).catch(function () { /* offline: keep what is shown */ });
  }

  /* ───────── card ───────── */
  var card = $('#card');
  function openCard(id, source, hide) {
    S.current = id;
    fillCard(true);
    F.openPlate(card, source, { modal: true, hide: hide || undefined, onClose: function () { S.current = null; } });
  }
  function fillCard(first) {
    var a = S.byId[S.current];
    if (!a) { F.closePlate(card); return; }
    var word = wordOf(a), busy = isBusy(a), run = a.trackable && a.status.running, act_ = canAct();
    $('#cardTitle').textContent = a.name;
    var ico = $('#cardIco');
    ico.setAttribute('data-id', a.id);
    if (first) { ico.removeAttribute('data-hash'); ico.textContent = ''; paintIcon(ico); }
    if (first) { $('#cardBig').textContent = word; $('#cardEyebrow').textContent = 'Status'; } else setWord($('#cardBig'), word);
    $('#cardLoad').hidden = !busy;
    setWord($('#kvProcs'), String(run ? a.status.processCount : 0));
    setWord($('#kvMem'), run ? fmtMem(a.status.workingSetBytes) : '0 MB');
    setWord($('#kvUp'), run ? fmtUp(a.status.uptimeSeconds) : '—');
    $('#bStart').disabled = busy || !act_ || run;
    $('#bRestart').disabled = busy || !act_ || !run;
    $('#bStop').disabled = busy || !act_ || !run;
    var note = $('#cardNote');
    var text = !act_ ? 'The PC is not reachable right now, so actions are paused.' : !a.trackable ? 'reLAUNCH can’t see this app’s processes, so it can only start it.' : '';
    note.hidden = !text;
    note.textContent = text;
  }
  $('#cardClose').addEventListener('click', function () { F.closePlate(card); });
  $('#bStart').addEventListener('click', function () { act(S.current, 'start'); });
  $('#bRestart').addEventListener('click', function () { act(S.current, 'restart'); });
  $('#bStop').addEventListener('click', function () {
    var a = S.byId[S.current];
    confirmDialog('Stop ' + a.name + '?', 'It will close, and anything unsaved in it may be lost.', 'Stop', function () { act(a.id, 'stop'); });
  });

  /* ───────── actions ───────── */
  function errorText(b, status) {
    var code = b.errorCode || b.error;
    var map = {
      noSession: 'Nobody is logged in on the PC.',
      pcOffline: 'The PC is offline. Wake it and try again.',
      timeout: 'The PC didn’t answer in time. Check its status in a moment.',
      busy: 'That app is still busy. Try again in a moment.',
      tooManyActions: 'Too many actions. Wait a minute and try again.',
      agentNotReady: 'The PC helper is still starting. Try again shortly.'
    };
    return map[code] || b.message || ('Something went wrong' + (status ? ' (' + status + ')' : '') + '.');
  }
  function act(id, kind) {
    var a = S.byId[id];
    if (!a || isBusy(a)) return Promise.resolve();
    S.busy[id] = kind;
    render();
    return api('/api/apps/' + id + '/' + kind, { method: 'POST' })
      .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
      .then(function (res) {
        if (res.body.ok) {
          if (a.trackable) a.status = Object.assign({}, a.status, { running: kind !== 'stop' });
          F.toast(a.name + ' ' + PAST[kind]);
        } else F.toast(errorText(res.body, res.status));
      })
      .catch(function (e) { if (e.message !== 'unauthorized') F.toast('Couldn’t reach the relay. Check your connection.'); })
      .then(function () { delete S.busy[id]; render(); });
  }

  function toggleFavorite(id) {
    var before = S.favIds.slice(), on = before.indexOf(id) < 0;
    S.favIds = on ? before.concat(id) : before.filter(function (x) { return x !== id; });
    render();
    api('/api/favorites', { method: 'PUT', body: JSON.stringify({ ids: S.favIds }), headers: { 'content-type': 'application/json' } })
      .then(function (r) { if (!r.ok) throw new Error('failed'); saveCache(); F.toast(S.byId[id].name + (on ? ' added to Favorites' : ' removed from Favorites')); })
      .catch(function (e) {
        if (e.message === 'unauthorized') return;
        S.favIds = before; render();
        F.toast('Couldn’t change favorites. The PC may be offline.');
      });
  }

  /* ───────── dialogs ───────── */
  var dlg = $('#stopDlg'), dlgAction = null;
  function confirmDialog(title, text, label, fn) {
    $('#stopT').textContent = title;
    $('#stopP').textContent = text;
    $('#stopYes').textContent = label;
    dlgAction = fn;
    F.dialog(dlg, true);
  }
  $('#stopNo').addEventListener('click', function () { dlgAction = null; F.dialog(dlg, false); });
  $('#stopYes').addEventListener('click', function () { var fn = dlgAction; dlgAction = null; F.dialog(dlg, false); if (fn) fn(); });

  /* ───────── views ───────── */
  function setView(name, instant) {
    if (S.view === name && !instant) return;
    var apply = function () {
      S.view = name;
      $$('[data-view]').forEach(function (s) { s.hidden = s.getAttribute('data-view') !== name; });
    };
    if (instant) apply(); else F.view(apply);
    var btn = $('[data-f-nav][data-go="' + name + '"]');
    if (btn && btn.getAttribute('aria-current') !== 'page') F.setNav($('#nav'), btn);
    if (name === 'act') loadActivity();
  }
  $$('[data-f-nav]').forEach(function (b) { b.addEventListener('click', function () { setView(b.getAttribute('data-go')); }); });

  /* ───────── menu ───────── */
  $('#moreBtn').addEventListener('click', function () { F.menu(this, $('#menu')); });
  $('#mTheme').addEventListener('click', function () { setTimeout(F.toggleTheme, 120); });
  $('#mRefresh').addEventListener('click', function () { load().then(function () { F.toast('Refreshed'); }); });
  $('#mForget').addEventListener('click', function () {
    setTimeout(function () {
      confirmDialog('Forget this phone?', 'You will need to scan a new pairing code to use reLAUNCH here again.', 'Forget', function () { unpair(false); });
    }, 160);
  });

  /* ───────── pairing ───────── */
  var sheet = $('#pairSheet');
  function deviceName() {
    var ua = navigator.userAgent || '', m;
    if ((m = /Android[^;]*;\s*([^;)]+?)(?:\sBuild|\))/.exec(ua))) return m[1].trim().slice(0, 40);
    if (/iPhone/.test(ua)) return 'iPhone';
    if (/iPad/.test(ua)) return 'iPad';
    return 'Phone';
  }
  function pairWith(code) {
    return fetch('/api/pair', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code: code, name: deviceName() }) })
      .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); });
  }
  function finishPairing(body) {
    write(TOKEN_KEY, body.token);
    S.token = body.token;
    tileKey = ''; rowKey = '';
    $('#vPair').hidden = true;
    setView('fav', true);
    render();
    load().then(function () { F.toast('Phone paired'); });
    connectLive();
  }
  $('#pairBtn').addEventListener('click', function () { F.openPlate(sheet, this, { focus: $('#code') }); });
  $('#pairCancel').addEventListener('click', function () { F.closePlate(sheet); });
  F.dragToClose(sheet, $('#pairGrab'));
  sheet.addEventListener('submit', function (e) {
    e.preventDefault();
    var code = $('#code').value.trim();
    var field = $('#codeField');
    if (!code) { field.classList.add('error'); $('#codeHelp').textContent = 'Type the code shown on the PC.'; return; }
    pairWith(code).then(function (res) {
      if (res.status === 200) { field.classList.remove('error'); F.closePlate(sheet).then(function () { finishPairing(res.body); }); return; }
      field.classList.add('error');
      $('#codeHelp').textContent = res.status === 429 ? 'Too many attempts. Wait a few minutes and try again.'
        : res.status === 403 ? 'That code is wrong or has expired. Create a new one on the PC.' : 'Pairing failed. Check your connection and try again.';
    }).catch(function () { field.classList.add('error'); $('#codeHelp').textContent = 'Couldn’t reach the relay. Check your connection.'; });
  });

  function unpair(revoked) {
    write(TOKEN_KEY, null);
    write(CACHE_KEY, null);
    S.token = null; S.apps = []; S.byId = {}; S.favIds = []; S.pc = 'offline'; S.session = 'unknown';
    tileKey = ''; rowKey = '';
    if (live) { var w = live; live = null; w.close(); }
    $('#tiles').innerHTML = ''; $('#rows').innerHTML = ''; $('#actRows').innerHTML = '';
    if (F.isOpen(card)) F.closePlate(card);
    setView('pair', true);
    render();
    if (revoked) F.toast('This phone was disconnected. Pair it again to continue.');
  }

  /* ───────── live socket ───────── */
  var live = null, retry = 0, pingTimer = null;
  function connectLive() {
    if (!S.token || live) return;
    var ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/api/live');
    live = ws;
    ws.onopen = function () { ws.send(JSON.stringify({ token: S.token })); };
    ws.onmessage = function (e) {
      if (e.data === 'pong') return;
      var m;
      try { m = JSON.parse(e.data); } catch (err) { return; }
      onLive(m);
    };
    ws.onclose = function (e) {
      if (live === ws) live = null;
      S.liveOk = false;
      clearInterval(pingTimer);
      if (e.code === 4401) { if (S.token) unpair(true); return; }
      if (!S.token) return;
      retry = Math.min(retry + 1, 6);
      setTimeout(connectLive, Math.min(30000, 500 * Math.pow(2, retry)));
    };
  }
  function onLive(m) {
    switch (m.type) {
      case 'hello':
        S.liveOk = true; retry = 0;
        S.session = m.session; S.pc = m.pc; S.pcName = m.pcName || S.pcName;
        clearInterval(pingTimer);
        pingTimer = setInterval(function () { if (live && live.readyState === 1) live.send('ping'); }, 25000);
        load();
        break;
      case 'pc':
        S.pc = m.state; S.session = m.session || S.session; S.pcName = m.pcName || S.pcName;
        render();
        if (m.state === 'online') load();
        break;
      case 'session': S.session = m.state; render(); break;
      case 'status': if (S.byId[m.appId]) { S.byId[m.appId].status = m.status; render(); } break;
      case 'action':
        if (S.byId[m.appId]) { S.byId[m.appId].phase = m.phase; render(); }
        if (S.view === 'act' && (m.phase === 'running' || m.phase === 'stopped' || m.phase === 'failed')) loadActivity();
        break;
      case 'catalogue': case 'icons': load(); break;
    }
  }

  window.addEventListener('online', function () { S.online = true; load(); connectLive(); });
  window.addEventListener('offline', function () { S.online = false; render(); });
  document.addEventListener('visibilitychange', function () { if (!document.hidden && S.token) { connectLive(); load(); } });
  // Fallback when the live socket is down.
  setInterval(function () { if (S.token && !S.liveOk && !document.hidden) load(); }, 20000);

  /* ───────── boot ───────── */
  function boot() {
    var m = /[#&]pair=([A-Za-z0-9 -]+)/.exec(location.hash);
    if (m) history.replaceState(null, '', location.pathname + location.search);
    if (S.token) {
      setView('fav', true);
      loadCache();
      render();
      load();
      connectLive();
      return;
    }
    setView('pair', true);
    render();
    if (m) {
      pairWith(m[1]).then(function (res) {
        if (res.status === 200) finishPairing(res.body);
        else { F.toast(res.status === 403 ? 'That pairing code is wrong or has expired.' : 'Pairing failed. Try again.'); }
      }).catch(function () { F.toast('Couldn’t reach the relay. Check your connection.'); });
    }
  }

  boot();
  F.init();
  F.favicon();
  if ('serviceWorker' in navigator && location.protocol !== 'file:') navigator.serviceWorker.register('/sw.js').catch(function () { /* optional */ });
})();
