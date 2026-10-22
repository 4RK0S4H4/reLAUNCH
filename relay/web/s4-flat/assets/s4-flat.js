/* S4 Flat v2.1 runtime — Plate & Ink motion. No dependencies. Exposes window.S4Flat.
   Call S4Flat.init() once after the markup exists. Everything else is opt-in helpers. */
(function () {
  var F = {}, root = document.documentElement;
  var reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var EASE = { ink: 'cubic-bezier(.2,.7,.2,1)', plate: 'cubic-bezier(.16,1,.3,1)', exit: 'cubic-bezier(.4,0,.9,.6)' };
  var T = { press: 120, ink: 220, glide: 240, plate: 300, morph: 340, recede: 320, exit: 180 };
  F.T = T; F.EASE = EASE;
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };
  function anim(el, kf, opt) { if (reduce || !el.animate) return null; return el.animate(kf, opt); }
  function stop(el) { if (el && el.getAnimations) el.getAnimations().forEach(function (a) { a.cancel(); }); }
  function done(a, fn) { if (a) a.onfinish = fn; else fn(); }
  function compact() { return root.getAttribute('data-density') === 'compact'; }
  var loose = []; /* closers for open things that don't recede the page (side panels, menus) */
  F.anim = anim; F.stop = stop;

  /* ---------- INK ---------- */
  var lastPt = null;
  /* iOS Safari only shows :active press states when the page listens for touches. */
  document.addEventListener('touchstart', function () {}, { passive: true });
  function small() { return innerWidth < 600; }
  /* Travel budget: a morph may travel at most ~40% of the screen height; farther than that, the plate arrives in place. */
  function withinBudget(a, b) { return Math.hypot((a.left + a.width / 2) - (b.left + b.width / 2), (a.top + a.height / 2) - (b.top + b.height / 2)) <= innerHeight * .42; }
  document.addEventListener('pointerdown', function (e) { lastPt = { x: e.clientX, y: e.clientY, t: Date.now() }; }, true);
  function aim(el, cx, cy) {
    var r = el.getBoundingClientRect(), x, y;
    if (cx != null) { x = cx; y = cy; }
    else {
      var fresh = lastPt && Date.now() - lastPt.t < 900 && lastPt.x >= r.left && lastPt.x <= r.right && lastPt.y >= r.top && lastPt.y <= r.bottom;
      x = fresh ? lastPt.x - r.left : r.width / 2; y = fresh ? lastPt.y - r.top : r.height / 2;
    }
    var d = 2 * Math.hypot(Math.max(x, r.width - x), Math.max(y, r.height - y)) + 2;
    el.style.setProperty('--x', x + 'px'); el.style.setProperty('--y', y + 'px'); el.style.setProperty('--d', d + 'px');
    return { x: x, y: y, r: r };
  }
  /* Turn a .f-ink control on/off. Colour pours from the last touch point (or the centre, or cx/cy). */
  F.ink = function (el, on, cx, cy) { aim(el, cx, cy); el.classList.toggle('on', !!on); return el; };
  F.inkCenter = function (el, on) { return F.ink(el, on, el.offsetWidth / 2, el.offsetHeight / 2); };
  F.drain = function (el) { if (el.classList.contains('on')) F.ink(el, false); };
  F.prime = function (scope) { $$('.f-ink.on', scope).forEach(function (el) { aim(el, el.offsetWidth / 2, el.offsetHeight / 2); }); };

  /* One-shot buttons: a soft ripple from the touch point. */
  F.ripple = function (b) {
    var o = aim(b), d = 2 * Math.hypot(Math.max(o.x, o.r.width - o.x), Math.max(o.y, o.r.height - o.y));
    var s = document.createElement('span'); s.className = 'f-ripple';
    s.style.width = s.style.height = d + 'px'; s.style.left = (o.x - d / 2) + 'px'; s.style.top = (o.y - d / 2) + 'px';
    b.appendChild(s);
    done(anim(s, [{ transform: 'scale(0)', opacity: .2 }, { transform: 'scale(1)', opacity: 0 }], { duration: 400, easing: EASE.ink }), function () { s.remove(); });
  };

  /* ---------- GLIDE ---------- */
  /* Moves the .f-bead inside `container` under its active child ([aria-pressed=true], [aria-current], or .on). */
  F.glide = function (container) {
    var bead = $('.f-bead', container); if (!bead) return;
    var on = $(':scope > [aria-pressed="true"], :scope > [aria-current="page"], :scope > [aria-current="true"], :scope > .on', container);
    if (!on) { bead.style.opacity = 0; return; }
    bead.style.opacity = '';
    bead.style.width = on.offsetWidth + 'px'; bead.style.height = on.offsetHeight + 'px';
    bead.style.transform = 'translate(' + (on.offsetLeft - bead.offsetLeft) + 'px,' + (on.offsetTop - bead.offsetTop) + 'px)';
  };
  F.glideAll = function () { $$('.f-seg, .f-nav').forEach(F.glide); };

  /* ---------- RECEDE + SCRIM ---------- */
  var holders = new Set(), scrim = null, wrap = null, onScrim = {}, recT = null;
  function ensureScrim() {
    if (scrim) return scrim;
    scrim = document.createElement('div'); scrim.className = 'f-scrim'; document.body.appendChild(scrim);
    scrim.addEventListener('click', function () {
      var keys = Array.from(holders); var k = keys[keys.length - 1];
      if (k && onScrim[k]) onScrim[k]();
    });
    return scrim;
  }
  /* Ease the page back (and dim it) while a plate named `key` is on top. `onDismiss` runs on scrim tap or Escape. */
  F.recede = function (key, on, onDismiss, modal) {
    ensureScrim(); wrap = wrap || $('.f-wrap');
    if (on) { holders.delete(key); holders.add(key); if (onDismiss) onScrim[key] = onDismiss; if (modal) root.classList.add('f-modal-' + key); }
    else { holders.delete(key); delete onScrim[key]; root.classList.remove('f-modal-' + key); }
    var any = holders.size > 0;
    /* Long pages skip the scale (too much to repaint); the scrim still dims. Compact density skips it too. */
    var allow = !!wrap && document.body.getAttribute('data-f-recede') !== 'off' && wrap.offsetHeight < innerHeight * 8 && !compact() && !small(); /* phones: dim only */
    var was = root.classList.contains('f-receded');
    if (any && allow && !was) wrap.style.setProperty('--f-oy', (innerHeight / 2 - wrap.getBoundingClientRect().top) + 'px');
    root.classList.toggle('f-receded', any && allow);
    if (was && !(any && allow)) { root.classList.add('f-receding'); clearTimeout(recT); recT = setTimeout(function () { root.classList.remove('f-receding'); }, T.recede + 80); }
    scrim.classList.toggle('open', any);
    root.classList.toggle('f-layered', any);
    /* Everything behind an open plate is inert: no focus, no taps, hidden from screen readers. */
    $$('.f-wrap, .f-dock, .f-selbar').forEach(function (el) { if (any) el.setAttribute('inert', ''); else el.removeAttribute('inert'); });
    root.classList.toggle('f-modal', Array.from(root.classList).some(function (c) { return c.indexOf('f-modal-') === 0; }));
    document.dispatchEvent(new CustomEvent('f:layers', { detail: { open: any } }));
  };
  F.layersOpen = function () { return holders.size > 0; };
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    if (loose.length) { e.preventDefault(); loose[loose.length - 1](); return; }
    if (!holders.size) return;
    var keys = Array.from(holders), k = keys[keys.length - 1];
    if (onScrim[k]) { e.preventDefault(); onScrim[k](); }
  });
  /* Where an element will sit once the page comes forward again. */
  F.restingRect = function (el) {
    wrap = wrap || $('.f-wrap');
    var had = root.classList.contains('f-receded');
    if (wrap) wrap.style.transition = 'none';
    root.classList.remove('f-receded');
    var r = el.getBoundingClientRect();
    if (had) root.classList.add('f-receded');
    if (wrap) { void wrap.offsetWidth; wrap.style.transition = ''; }
    return r;
  };

  /* ---------- MORPH: a plate grows out of one shape and shrinks back into it ---------- */
  function frame(R, t, radius, bg) {
    return {
      transform: 'translate(' + (t.left - R.left) + 'px,' + (t.top - R.top) + 'px)',
      clipPath: 'inset(0px ' + Math.max(0, R.width - t.width) + 'px ' + Math.max(0, R.height - t.height) + 'px 0px round ' + radius + ')',
      backgroundColor: bg
    };
  }
  function full(radius, bg, dy) { return { transform: 'translate(0px,' + (dy || 0) + 'px)', clipPath: 'inset(0px 0px 0px 0px round ' + radius + ')', backgroundColor: bg }; }
  function shapeOf(el, rect) {
    var c = getComputedStyle(el), r = rect || el.getBoundingClientRect();
    var rad = c.borderTopLeftRadius;
    if (rad.indexOf('%') > -1 || parseFloat(rad) > Math.min(r.width, r.height) / 2) rad = (Math.min(r.width, r.height) / 2) + 'px';
    return { rect: r, radius: rad, bg: c.backgroundColor };
  }
  F.shapeOf = shapeOf;
  /* Grow `plate` (already visible, at its final place) out of `from` (an element or {rect,radius,bg}). Returns a Promise. */
  F.morphIn = function (plate, from, opts) {
    opts = opts || {};
    var src = from && from.nodeType ? shapeOf(from) : from;
    if (compact()) src = null; /* compact density: plates fade in place instead of morphing */
    stop(plate);
    var R = plate.getBoundingClientRect(), me = shapeOf(plate, R);
    if (src && !withinBudget(src.rect, R)) src = null;
    plate.style.willChange = 'transform, clip-path, opacity';
    var lift = small() ? 10 : 16;
    var a = src ? anim(plate, [frame(R, src.rect, src.radius, src.bg), full(me.radius, me.bg)], { duration: opts.duration || T.morph, easing: EASE.plate })
                : anim(plate, [{ opacity: 0, transform: 'translateY(' + lift + 'px)' }, { opacity: 1, transform: 'none' }], { duration: T.plate, easing: EASE.plate });
    Array.prototype.forEach.call(plate.children, function (c, i) {
      stop(c); anim(c, [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: T.ink, easing: EASE.ink, delay: (opts.contentDelay != null ? opts.contentDelay : 120) + i * 25, fill: 'backwards' });
    });
    return new Promise(function (res) { done(a, function () { plate.style.willChange = ''; res(); }); });
  };
  /* Shrink `plate` back into `to` (an element, a rect-shape, or null to drop and fade). `dy` = current drag offset. */
  F.morphOut = function (plate, to, opts) {
    opts = opts || {};
    var dst = to && to.nodeType ? shapeOf(to, opts.rect) : to;
    if (compact()) dst = null;
    plate.style.willChange = 'transform, clip-path, opacity';
    var R = plate.getBoundingClientRect(), me = shapeOf(plate, R), dy = opts.dy || 0;
    stop(plate);
    Array.prototype.forEach.call(plate.children, function (c) { stop(c); anim(c, [{ opacity: 1 }, { opacity: 0 }], { duration: 90, easing: EASE.exit, fill: 'forwards' }); });
    var r = dst && dst.rect, onScreen = r && r.width > 0 && r.bottom > 0 && r.top < innerHeight && withinBudget(r, R);
    var a = onScreen
      ? anim(plate, [full(me.radius, me.bg, dy), frame(R, r, dst.radius, dst.bg)], { duration: T.plate, easing: EASE.plate, fill: 'forwards' })
      : anim(plate, [{ opacity: 1, transform: 'translate(0px,' + dy + 'px)' }, { opacity: 0, transform: 'translate(0px,' + (dy + (small() ? 10 : 16)) + 'px)' }], { duration: T.exit, easing: EASE.exit, fill: 'forwards' });
    return new Promise(function (res) { done(a, function () { stop(plate); plate.style.willChange = ''; Array.prototype.forEach.call(plate.children, stop); res(); }); });
  };

  /* ---------- PLATES: open a sheet/card out of an element ---------- */
  /* opts: { hide: element hidden while open (default: source), recede: true, modal: false, onClose: fn, focus: element } */
  var openPlates = new Map();
  F.openPlate = function (plate, source, opts) {
    opts = opts || {};
    var side = plate.hasAttribute('data-side') && innerWidth >= 1280;
    if (openPlates.has(plate)) {
      var cur = openPlates.get(plate);
      /* A docked side panel just swaps to the new source: content crossfades, the panel stays put. */
      if (cur.side && source && cur.source !== source) {
        cur.source.classList.remove('f-active'); source.classList.add('f-active'); cur.source = source;
        Array.prototype.forEach.call(plate.children, function (c, i) { anim(c, [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: T.ink, easing: EASE.ink, delay: i * 20, fill: 'backwards' }); });
      }
      return Promise.resolve();
    }
    if (side) opts = Object.assign({}, opts, { recede: false, hide: null });
    plate.classList.toggle('is-side', side);
    if (plate.hasAttribute('aria-modal')) plate.setAttribute('aria-modal', side ? 'false' : 'true'); /* a docked panel sits beside the page, not over it */
    var key = plate.id || ('p' + Math.random().toString(36).slice(2));
    var hide = opts.hide === undefined ? source : opts.hide;
    var st = { source: source, hide: hide, key: key, opts: opts, side: side };
    if (side && source) source.classList.add('f-active');
    if (opts.recede === false) { st.closer = function () { F.closePlate(plate); }; loose.push(st.closer); }
    openPlates.set(plate, st);
    plate.hidden = false;
    if (opts.position) opts.position(plate, source);
    else if (plate.classList.contains('f-sheet') && source && innerWidth >= 1100) {
      var fr = source.getBoundingClientRect();
      plate.style.setProperty('--f-sheet-top', Math.max(16, Math.min(fr.top, innerHeight - plate.offsetHeight - 16)) + 'px');
    }
    else if (plate.classList.contains('f-card') && !side && source) {
      /* Cards unfold next to what opened them, so the morph is short. */
      var sr = source.getBoundingClientRect(), h = plate.offsetHeight, minTop = small() ? 72 : 84;
      plate.style.top = Math.max(minTop, Math.min(sr.top - 12, innerHeight - h - 16)) + 'px';
    } else if (plate.classList.contains('f-card')) plate.style.top = '';
    var src = source ? shapeOf(source) : null;
    if (hide) hide.style.visibility = 'hidden';
    if (opts.recede !== false) F.recede(key, true, function () { F.closePlate(plate); }, opts.modal);
    var p = F.morphIn(plate, src);
    setTimeout(function () { var f = opts.focus || $('[autofocus], .f-close', plate); if (f && matchMedia('(hover: hover)').matches) f.focus({ preventScroll: true }); }, T.morph);
    return p;
  };
  F.closePlate = function (plate, extra) {
    var st = openPlates.get(plate); if (!st) return Promise.resolve();
    openPlates.delete(plate);
    if (st.closer) loose = loose.filter(function (f) { return f !== st.closer; });
    if (st.side && st.source) st.source.classList.remove('f-active');
    var r = st.source ? F.restingRect(st.source) : null;
    if (st.opts.recede !== false) F.recede(st.key, false);
    return F.morphOut(plate, st.source ? { rect: r, radius: shapeOf(st.source, r).radius, bg: shapeOf(st.source, r).bg } : null, extra).then(function () {
      plate.hidden = true;
      if (st.hide) st.hide.style.visibility = '';
      if (st.source && st.source.focus) st.source.focus({ preventScroll: true });
      if (st.opts.onClose) st.opts.onClose();
    });
  };
  F.isOpen = function (plate) { return openPlates.has(plate); };

  /* Drag a sheet down by its handle to close it. */
  F.dragToClose = function (plate, handle) {
    var s = null;
    handle.addEventListener('pointerdown', function (e) {
      if (!openPlates.has(plate)) return;
      s = { y: e.clientY, t: performance.now(), dy: 0, v: 0 };
      handle.setPointerCapture(e.pointerId);
      stop(plate); wrap = wrap || $('.f-wrap');
      [plate, wrap, scrim].forEach(function (x) { if (x) x.style.transition = 'none'; });
    });
    handle.addEventListener('pointermove', function (e) {
      if (!s) return;
      var raw = e.clientY - s.y, now = performance.now(), dy = raw > 0 ? raw : raw * .15;
      s.v = (dy - s.dy) / Math.max(1, now - s.t); s.t = now; s.dy = dy;
      plate.style.transform = 'translateY(' + dy + 'px)';
      var p = Math.max(0, Math.min(1, 1 - dy / 320));
      if (wrap) wrap.style.transform = 'scale(' + (1 - .035 * p) + ')';
      if (scrim) scrim.style.opacity = p;
    });
    var up = function () {
      if (!s) return; var x = s; s = null;
      [plate, wrap, scrim].forEach(function (el) { if (el) el.style.transition = ''; });
      if (wrap) wrap.style.transform = ''; if (scrim) scrim.style.opacity = '';
      plate.style.transform = '';
      if (x.dy > 110 || x.v > .6) F.closePlate(plate, { dy: x.dy });
      else anim(plate, [{ transform: 'translateY(' + x.dy + 'px)' }, { transform: 'none' }], { duration: T.glide, easing: EASE.plate });
    };
    handle.addEventListener('pointerup', up); handle.addEventListener('pointercancel', up);
  };

  /* ---------- ARRIVE: dialogs ---------- */
  F.dialog = function (dlg, open, opener) {
    dlg.classList.toggle('open', open);
    F.recede(dlg.id || 'dialog', open, function () { F.dialog(dlg, false); }, true);
    if (open) { dlg._opener = opener || document.activeElement; setTimeout(function () { var b = $('button, [href], input', dlg); if (b) b.focus({ preventScroll: true }); }, 60); }
    else if (dlg._opener && dlg._opener.focus) dlg._opener.focus({ preventScroll: true });
  };

  /* ---------- TOAST ---------- */
  var toastEl = null, toastT = null;
  F.toast = function (msg, opts) {
    opts = opts || {};
    if (!toastEl) {
      toastEl = document.createElement('div'); toastEl.className = 'f-toast f-arrive'; toastEl.setAttribute('role', 'status');
      toastEl.innerHTML = '<span class="tc"><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg></span><span class="msg"></span><button type="button" hidden>Undo</button>';
      document.body.appendChild(toastEl);
      $('button', toastEl).addEventListener('click', function () { var f = toastEl._undo; toastEl._undo = null; toastEl.classList.remove('open'); if (f) f(); });
    }
    $('.msg', toastEl).textContent = msg;
    var b = $('button', toastEl); b.hidden = !opts.undo; b.textContent = opts.actionLabel || 'Undo'; toastEl._undo = opts.undo || null;
    toastEl.classList.add('open');
    clearTimeout(toastT); toastT = setTimeout(function () { toastEl.classList.remove('open'); toastEl._undo = null; }, opts.undo ? 4000 : 2000);
  };

  /* ---------- TEXT: changes in place roll softly (out up 90ms, in from below 170ms); never snap ---------- */
  F.text = function (el, val, html) {
    val = String(val);
    var cur = html ? el.innerHTML : el.textContent;
    if (el._fSwap) { el._fNext = { val: val, html: html }; return; }
    if (val === cur) return;
    var set = function (v, h) { if (h) el.innerHTML = v; else el.textContent = v; };
    if (reduce || !el.animate) { set(val, html); return; }
    if (getComputedStyle(el).display === 'inline') el.style.display = 'inline-block';
    el._fSwap = true;
    var out = el.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(-4px)' }], { duration: 90, easing: EASE.exit, fill: 'forwards' });
    out.onfinish = function () {
      var n = el._fNext || { val: val, html: html }; el._fNext = null;
      set(n.val, n.html); out.cancel(); el._fSwap = false;
      el.animate([{ opacity: 0, transform: 'translateY(5px)' }, { opacity: 1, transform: 'none' }], { duration: 170, easing: EASE.ink });
    };
  };

  /* ---------- SELECTION BAR: grows out of the check you ticked ---------- */
  F.selbar = function (bar, show, fromCheck) {
    var shown = !bar.hidden && !bar._leaving;
    if (show === shown) return;
    /* The bar is a consequence of selecting, not something you opened: it rises quietly in its own place. (fromCheck is kept for compatibility.) */
    if (show) { bar._leaving = false; bar.hidden = false; F.morphIn(bar, null, { contentDelay: 60 }); }
    else { bar._leaving = true; F.morphOut(bar, null).then(function () { if (bar._leaving) { bar.hidden = true; bar._leaving = false; } }); }
  };

  /* ---------- LISTS: leaving items go first, the rest glide into place, new ones fade in ---------- */
  /* items: array of elements; isVisible(el) decides; leave: 'fade' | 'slide' | 'instant' */
  var pendingList = new Map();
  F.reflow = function (container, items, isVisible, leave) {
    var pend = pendingList.get(container);
    if (pend) { clearTimeout(pend.t); pend.run(); }
    var leaving = items.filter(function (el) { return !el.hidden && !isVisible(el); });
    var run = function () {
      pendingList.delete(container);
      leaving.forEach(function (el) { stop(el); });
      var before = new Map();
      items.forEach(function (el) { if (!el.hidden) before.set(el, el.getBoundingClientRect().top); });
      items.forEach(function (el) { el.hidden = !isVisible(el); });
      var any = false;
      items.forEach(function (el) {
        if (el.hidden) return; any = true;
        var top = el.getBoundingClientRect().top;
        if (before.has(el)) { var dy = before.get(el) - top; if (Math.abs(dy) > .5) anim(el, [{ transform: 'translateY(' + dy + 'px)' }, { transform: 'none' }], { duration: T.plate, easing: EASE.plate }); }
        else anim(el, [{ opacity: 0 }, { opacity: 1 }], { duration: T.ink, easing: EASE.ink });
      });
      var empty = $(':scope > .f-empty', container);
      if (empty) { var was = empty.hidden; empty.hidden = any; if (was && !any) anim(empty, [{ opacity: 0 }, { opacity: 1 }], { duration: T.ink, easing: EASE.ink }); }
    };
    if (leaving.length && !reduce && leave !== 'instant') {
      leaving.forEach(function (el) {
        anim(el, leave === 'slide' ? [{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateX(56px)' }] : [{ opacity: 1 }, { opacity: 0 }], { duration: T.exit, easing: EASE.exit, fill: 'forwards' });
      });
      pendingList.set(container, { run: run, t: setTimeout(run, T.exit) });
    } else run();
  };

  /* ---------- SWIPE: a row follows your finger; past the threshold it commits ---------- */
  /* slot = .f-slot, plate = .f-rowplate, handle = .f-row. onCommit(tx) returns 'stay' or 'leave'. canSwipe() optional. */
  F.swipe = function (slot, onCommit, canSwipe) {
    var plate = $('.f-rowplate', slot), handle = $('.f-row', slot), behind = $('.f-behind', slot), s = null;
    handle.addEventListener('pointerdown', function (e) { if (e.button > 0 || (canSwipe && !canSwipe())) return; s = { x: e.clientX, y: e.clientY, drag: false, tx: 0, id: e.pointerId }; });
    handle.addEventListener('pointermove', function (e) {
      if (!s || e.pointerId !== s.id) return;
      var dx = e.clientX - s.x, dy = e.clientY - s.y;
      if (!s.drag) {
        if (Math.abs(dx) > 8 && Math.abs(dx) > Math.abs(dy) * 1.3 && dx > 0) { s.drag = true; handle.setPointerCapture(e.pointerId); plate.style.transition = 'none'; }
        else if (Math.abs(dy) > 10) { s = null; return; } else return;
      }
      var tx = Math.max(0, dx); if (tx > 120) tx = 120 + (tx - 120) * .35;
      s.tx = tx; plate.style.transform = 'translateX(' + tx + 'px)';
      if (behind) behind.classList.toggle('armed', tx > 96);
    });
    var end = function () {
      if (!s) return; var x = s; s = null;
      if (!x.drag) return;
      handle._fDragged = true; setTimeout(function () { handle._fDragged = false; }, 60);
      plate.style.transition = ''; plate.style.transform = '';
      var reset = function () { if (behind) behind.classList.remove('armed'); };
      if (x.tx > 96) {
        var mode = onCommit(x.tx);
        if (mode === 'leave') {
          var a = anim(plate, [{ transform: 'translateX(' + x.tx + 'px)', opacity: 1 }, { transform: 'translateX(' + slot.offsetWidth + 'px)', opacity: 0 }], { duration: T.exit, easing: EASE.exit, fill: 'forwards' });
          done(a, function () { slot.dispatchEvent(new CustomEvent('f:swiped')); stop(plate); reset(); });
          return;
        }
      }
      anim(plate, [{ transform: 'translateX(' + x.tx + 'px)' }, { transform: 'none' }], { duration: x.tx > 96 ? T.plate : T.glide, easing: EASE.plate });
      setTimeout(reset, T.plate);
    };
    handle.addEventListener('pointerup', end); handle.addEventListener('pointercancel', end);
    // Swallow the click that ends a drag.
    handle.addEventListener('click', function (e) { if (handle._fDragged) { e.stopImmediatePropagation(); e.preventDefault(); handle._fDragged = false; } }, true);
  };

  /* ---------- THEME, ACCENT, DENSITY: a quiet crossfade ---------- */
  F.crossfade = function (mutate) {
    if (!document.startViewTransition || reduce) { mutate(); return; }
    root.classList.add('f-vt');
    var t = document.startViewTransition(mutate);
    t.finished.finally(function () { root.classList.remove('f-vt'); });
  };
  function lum(hex) {
    var h = hex.replace('#', ''); if (h.length === 3) h = h.replace(/./g, '$&$&');
    return [0, 2, 4].map(function (i) { var c = parseInt(h.substr(i, 2), 16) / 255; return c <= .03928 ? c / 12.92 : Math.pow((c + .055) / 1.055, 2.4); })
      .reduce(function (a, c, i) { return a + c * [.2126, .7152, .0722][i]; }, 0);
  }
  F.onColor = function (hex) { var L = lum(hex); return (1.05 / (L + .05)) >= ((L + .05) / .05) ? '#ffffff' : '#0e1015'; };
  /* setAccent('#2f54eb') or setAccent('ink') for the monochrome default. Hex colours only. */
  function hexMix(a, b, t) {
    a = a.replace('#', ''); b = b.replace('#', '');
    return '#' + [0, 2, 4].map(function (i) { var v = Math.round(parseInt(a.substr(i, 2), 16) * (1 - t) + parseInt(b.substr(i, 2), 16) * t); return ('0' + v.toString(16)).slice(-2); }).join('');
  }
  function contrast(a, b) { var x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); }
  /* The accent nudged darker (light theme) or lighter (dark theme) until thin strokes reach 3:1 on surface-2. */
  function lineFor(hex, ground, toward) { for (var t = 0; t <= 1.0001; t += .05) { var m = hexMix(hex, toward, t); if (contrast(m, ground) >= 3) return m; } return toward; }
  F.contrast = contrast;
  F.setAccent = function (a) {
    var st = root.style, keys = ['--accent', '--on-accent', '--nav-fill', '--nav-ink', '--accent-line-l', '--accent-line-d'];
    if (!a || a === 'ink') { keys.forEach(function (k) { st.removeProperty(k); }); return; }
    if (a.length === 4) a = '#' + a.slice(1).replace(/./g, '$&$&');
    st.setProperty('--accent', a); st.setProperty('--on-accent', F.onColor(a));
    st.setProperty('--nav-fill', 'var(--accent)'); st.setProperty('--nav-ink', 'var(--on-accent)');
    st.setProperty('--accent-line-l', lineFor(a, '#e6e9ef', '#000000'));
    st.setProperty('--accent-line-d', lineFor(a, '#22252c', '#ffffff'));
  };
  F.isDark = function () { var t = root.getAttribute('data-theme'); return t ? t === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches; };
  F.toggleTheme = function () { var n = F.isDark() ? 'light' : 'dark'; F.crossfade(function () { root.setAttribute('data-theme', n); }); };
  F.setDensity = function (compact) { F.crossfade(function () { if (compact) root.setAttribute('data-density', 'compact'); else root.removeAttribute('data-density'); F.glideAll(); F.prime(); }); };

  /* ---------- AUTO-WIRING (data attributes) ----------
     .f-toggle                    → toggles on click; fires 'f:change' {on}
     .f-seg                       → buttons set aria-pressed, bead glides; fires 'f:change' {value}
     [data-f-single]              → one .f-ink child on at a time (chips, radios, choices); fires 'f:change' {value}
     [data-f-multi]               → each .f-ink child toggles; fires 'f:change' {value,on}
     [data-f-ripple]              → ripple on click
     .f-nav [data-f-nav]          → aria-current moves, bead glides
     .f-confirm[data-done]        → pours green, rolls its .lbl to the data-done text, resets after 1.6s
     .f-search-toggle             → toggles .f-searching on the closest .f-topbar
     .f-setting                   → tapping the row flips its switch
     table.f-table th[data-sort]  → sortable columns (data-sort="num" for numbers); fires 'f:sort'
     .f-drop                      → file picker + drag-and-drop; fires 'f:files' {files}
     [data-f-cal]                 → calendar (data-value="2026-10-23"); fires 'f:change' {value}
     .f-progress[data-v] / .f-ring[data-v][data-label] → progress bar / ring (0–1)
     [data-tip]                   → tooltip on mouse hover or keyboard focus
     [data-f-vt]                  → view-transition name for morphs between views or pages */
  F.wire = function (scope) {
    scope = scope || document;
    $$('.f-toggle:not([data-f-w])', scope).forEach(function (t) {
      t.setAttribute('data-f-w', ''); t.setAttribute('role', 'switch'); t.setAttribute('aria-checked', t.classList.contains('on'));
      if (!t.querySelector('i > b')) t.innerHTML = '<i><b><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6.5 12.5l3.5 3.5 7.5-8"/></svg></b></i>';
      t.addEventListener('click', function () {
        var on = !t.classList.contains('on'), k = t.querySelector('i').getBoundingClientRect(), r = t.getBoundingClientRect();
        F.ink(t, on, k.left - r.left + k.width / 2, r.height / 2); t.setAttribute('aria-checked', on);
        /* the knob has a little give: it stretches along the track while it travels, then settles (no overshoot) */
        anim(t.querySelector('i > b'), [{ transform: 'scaleX(1)' }, { transform: 'scaleX(1.32)', offset: .45 }, { transform: 'scaleX(1)' }], { duration: T.glide + 20, easing: EASE.plate });
        t.dispatchEvent(new CustomEvent('f:change', { bubbles: true, detail: { on: on } }));
      });
    });
    $$('.f-seg:not([data-f-w])', scope).forEach(function (seg) {
      seg.setAttribute('data-f-w', '');
      if (!$('.f-bead', seg)) { var b = document.createElement('span'); b.className = 'f-bead'; b.setAttribute('aria-hidden', 'true'); seg.insertBefore(b, seg.firstChild); }
      $$(':scope > button', seg).forEach(function (btn) {
        btn.addEventListener('click', function () {
          if (btn.getAttribute('aria-pressed') === 'true') return;
          $$(':scope > button', seg).forEach(function (x) { x.setAttribute('aria-pressed', x === btn); });
          F.glide(seg); seg.dispatchEvent(new CustomEvent('f:change', { bubbles: true, detail: { value: btn.dataset.value || btn.textContent.trim() } }));
        });
      });
    });
    $$('[data-f-single]:not([data-f-w])', scope).forEach(function (g) {
      g.setAttribute('data-f-w', '');
      $$('.f-ink', g).forEach(function (it) {
        var attr = it.getAttribute('role') === 'radio' ? 'aria-checked' : 'aria-pressed';
        it.setAttribute(attr, it.classList.contains('on'));
        it.addEventListener('click', function () {
          if (it.classList.contains('on')) return;
          $$('.f-ink', g).forEach(function (x) { if (x !== it) { F.drain(x); x.setAttribute(attr, 'false'); } });
          F.ink(it, true); it.setAttribute(attr, 'true');
          g.dispatchEvent(new CustomEvent('f:change', { bubbles: true, detail: { value: it.dataset.value || it.textContent.trim() } }));
        });
      });
    });
    $$('[data-f-multi]:not([data-f-w])', scope).forEach(function (g) {
      g.setAttribute('data-f-w', '');
      $$('.f-ink', g).forEach(function (it) {
        it.setAttribute('aria-pressed', it.classList.contains('on'));
        it.addEventListener('click', function () {
          var on = !it.classList.contains('on'); F.ink(it, on); it.setAttribute('aria-pressed', on);
          g.dispatchEvent(new CustomEvent('f:change', { bubbles: true, detail: { value: it.dataset.value || it.textContent.trim(), on: on } }));
        });
      });
    });
    $$('[data-f-ripple]:not([data-f-w])', scope).forEach(function (b) { b.setAttribute('data-f-w', ''); b.addEventListener('click', function () { F.ripple(b); }); });
    $$('.f-confirm:not([data-f-w])', scope).forEach(function (b) {
      b.setAttribute('data-f-w', ''); var tm, lbl = $('.lbl', b), from = lbl ? lbl.innerHTML : '', to = b.getAttribute('data-done');
      var label = function (on) { if (lbl && to) F.text(lbl, on ? '<svg class="f-i sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M10 15.17l9.19-9.19 1.42 1.41L10 18 3.64 11.64l1.41-1.41L10 15.17z"/></svg>' + to : from, true); };
      b.addEventListener('click', function () {
        clearTimeout(tm); if (b.classList.contains('on')) { F.ink(b, false); label(false); return; }
        F.ink(b, true); label(true);
        tm = setTimeout(function () { F.inkCenter(b, false); label(false); }, 1600);
      });
    });
    $$('.f-nav:not([data-f-w])', scope).forEach(function (nav) {
      nav.setAttribute('data-f-w', '');
      if (!$('.f-bead', nav)) { var b = document.createElement('span'); b.className = 'f-bead'; b.setAttribute('aria-hidden', 'true'); nav.insertBefore(b, nav.firstChild); }
      $$(':scope > [data-f-nav]', nav).forEach(function (it) {
        it.addEventListener('click', function () { F.setNav(nav, it); });
      });
    });
    $$('.f-search-toggle:not([data-f-w])', scope).forEach(function (b) {
      b.setAttribute('data-f-w', '');
      b.addEventListener('click', function () {
        var bar = b.closest('.f-topbar'), on = !bar.classList.contains('f-searching');
        bar.classList.toggle('f-searching', on); b.classList.toggle('on', on); b.setAttribute('aria-expanded', on);
        var input = $('.f-search input', bar); if (input) { input.tabIndex = on ? 0 : -1; if (on) input.focus({ preventScroll: true }); else if (input.value) { input.value = ''; input.dispatchEvent(new Event('input', { bubbles: true })); } }
      });
    });
    /* Whole settings rows toggle their switch. */
    $$('.f-setting:not([data-f-w])', scope).forEach(function (row) {
      row.setAttribute('data-f-w', '');
      row.addEventListener('click', function (e) { if (e.target.closest('button, a, input, select, textarea, label')) return; var t = $('.f-toggle', row); if (t) t.click(); });
    });
    $$('table.f-table', scope).forEach(F.table);
    $$('.f-drop', scope).forEach(F.drop);
    $$('[data-f-cal]:not([data-f-w])', scope).forEach(function (el) { el.setAttribute('data-f-w', ''); F.calendar(el); });
    $$('.f-progress[data-v]', scope).forEach(function (el) { F.progress(el, el.getAttribute('data-v')); });
    $$('.f-ring[data-v]', scope).forEach(function (el) { F.ring(el, el.getAttribute('data-v'), el.getAttribute('data-label')); });
    F.tips(scope); F.nameViews(scope);
    F.prime(scope); F.glideAll();
  };
  F.setNav = function (nav, item) {
    $$(':scope > [data-f-nav]', nav).forEach(function (x) { if (x === item) x.setAttribute('aria-current', 'page'); else x.removeAttribute('aria-current'); });
    F.glide(nav);
  };

  /* ---------- REORDER: items glide to their new order (sorting) ---------- */
  F.reorder = function (container, ordered) {
    var before = new Map();
    ordered.forEach(function (el) { before.set(el, el.getBoundingClientRect().top); });
    ordered.forEach(function (el) { container.appendChild(el); });
    ordered.forEach(function (el) {
      var dy = before.get(el) - el.getBoundingClientRect().top;
      if (Math.abs(dy) > .5) anim(el, [{ transform: 'translateY(' + dy + 'px)' }, { transform: 'none' }], { duration: T.plate, easing: EASE.plate });
    });
  };

  /* ---------- LEDGER TABLE: th[data-sort] (add data-sort="num" for numbers) sorts rows; cells may carry data-v ---------- */
  var ARROW = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M13 7.83V20h-2V7.83l-5.36 5.36-1.42-1.41L12 4l7.78 7.78-1.42 1.41L13 7.83z"/></svg>';
  F.table = function (table) {
    if (table.hasAttribute('data-f-w')) return; table.setAttribute('data-f-w', '');
    var body = table.tBodies[0];
    $$('th[data-sort]', table).forEach(function (th) {
      var b = document.createElement('button'); b.type = 'button'; b.className = 'f-sort';
      b.innerHTML = '<span></span>' + ARROW; b.firstChild.textContent = th.textContent.trim();
      th.textContent = ''; th.appendChild(b);
      b.addEventListener('click', function () {
        var dir = th.getAttribute('aria-sort') === 'ascending' ? 'descending' : 'ascending';
        $$('th[aria-sort]', table).forEach(function (x) { x.removeAttribute('aria-sort'); });
        th.setAttribute('aria-sort', dir);
        var i = th.cellIndex, num = th.getAttribute('data-sort') === 'num';
        var key = function (tr) { var c = tr.cells[i], v = c.getAttribute('data-v') != null ? c.getAttribute('data-v') : c.textContent.trim(); return num ? parseFloat(String(v).replace(/[^0-9.\-]/g, '')) || 0 : v.toLowerCase(); };
        var rows = Array.prototype.slice.call(body.rows).sort(function (a, b) { var x = key(a), y = key(b); return (x > y ? 1 : x < y ? -1 : 0) * (dir === 'ascending' ? 1 : -1); });
        F.reorder(body, rows);
        table.dispatchEvent(new CustomEvent('f:sort', { bubbles: true, detail: { column: i, dir: dir } }));
      });
    });
  };

  /* ---------- MENU: grows out of its button; closes on outside tap, Escape or choosing an item ---------- */
  F.menu = function (btn, menu) {
    if (openPlates.has(menu)) return F.closePlate(menu);
    menu.hidden = false; menu.setAttribute('role', 'menu');
    $$('.f-menu-item', menu).forEach(function (it) { it.setAttribute('role', 'menuitem'); });
    var r = btn.getBoundingClientRect(), mw = menu.offsetWidth, mh = menu.offsetHeight;
    var left = Math.max(16, Math.min(r.left, innerWidth - mw - 16)), top = r.bottom + 8;
    if (top + mh > innerHeight - 16) top = Math.max(16, r.top - mh - 8);
    menu.style.left = left + 'px'; menu.style.top = top + 'px';
    btn.setAttribute('aria-expanded', 'true');
    if (!menu._fw) {
      menu._fw = true;
      menu.addEventListener('click', function (e) { if (e.target.closest('.f-menu-item')) setTimeout(function () { F.closePlate(menu); }, 90); });
    }
    var outside = function (e) { if (!menu.contains(e.target) && !btn.contains(e.target)) F.closePlate(menu); };
    document.addEventListener('pointerdown', outside, true);
    return F.openPlate(menu, btn, { recede: false, hide: null, focus: matchMedia('(hover: hover)').matches ? $('.f-menu-item', menu) : null,
      onClose: function () { btn.setAttribute('aria-expanded', 'false'); document.removeEventListener('pointerdown', outside, true); } });
  };

  /* ---------- TOOLTIPS: [data-tip="Text"] on hover (mouse) or keyboard focus ---------- */
  var tipEl = null, tipT = null;
  function showTip(el) {
    if (!tipEl) { tipEl = document.createElement('div'); tipEl.className = 'f-tip'; tipEl.setAttribute('role', 'tooltip'); document.body.appendChild(tipEl); }
    tipEl.textContent = el.getAttribute('data-tip');
    var r = el.getBoundingClientRect(), w = tipEl.offsetWidth, h = tipEl.offsetHeight;
    var top = r.top - h - 8; if (top < 8) top = r.bottom + 8;
    tipEl.style.left = Math.max(8, Math.min(r.left + r.width / 2 - w / 2, innerWidth - w - 8)) + 'px'; tipEl.style.top = top + 'px';
    tipEl.classList.add('show');
  }
  function hideTip() { clearTimeout(tipT); if (tipEl) tipEl.classList.remove('show'); }
  F.tips = function (scope) {
    $$('[data-tip]:not([data-f-tw])', scope).forEach(function (el) {
      el.setAttribute('data-f-tw', '');
      if (!el.getAttribute('aria-label')) el.setAttribute('aria-label', el.getAttribute('data-tip'));
      el.addEventListener('pointerenter', function (e) { if (e.pointerType !== 'mouse') return; clearTimeout(tipT); tipT = setTimeout(function () { showTip(el); }, 350); });
      el.addEventListener('pointerleave', hideTip); el.addEventListener('pointerdown', hideTip);
      el.addEventListener('focus', function () { if (el.matches(':focus-visible')) showTip(el); });
      el.addEventListener('blur', hideTip);
    });
  };

  /* ---------- PROGRESS + RING ---------- */
  F.progress = function (el, v) {
    v = Math.max(0, Math.min(1, +v || 0));
    if (!el.firstElementChild) el.appendChild(document.createElement('i'));
    el.style.setProperty('--v', v); el.setAttribute('role', 'progressbar'); el.setAttribute('aria-valuemin', 0); el.setAttribute('aria-valuemax', 100); el.setAttribute('aria-valuenow', Math.round(v * 100));
  };
  var CIRC = 2 * Math.PI * 42;
  F.ring = function (el, v, label) {
    v = Math.max(0, Math.min(1, +v || 0));
    if (!$('svg', el)) el.insertAdjacentHTML('afterbegin', '<svg viewBox="0 0 100 100" aria-hidden="true"><circle class="trk" cx="50" cy="50" r="42" fill="none" stroke-width="12"/><circle class="val" cx="50" cy="50" r="42" fill="none" stroke-width="12" stroke-dasharray="' + CIRC.toFixed(2) + '" stroke-dashoffset="' + CIRC.toFixed(2) + '"/></svg><div class="lbl"></div>');
    $('.val', el).style.strokeDashoffset = (CIRC * (1 - v)).toFixed(2);
    if (label != null) { var lb = $('.lbl', el); if (!lb.innerHTML) lb.innerHTML = label; else F.text(lb, label, true); }
    el.setAttribute('role', 'img'); el.setAttribute('aria-label', Math.round(v * 100) + '%');
  };

  /* ---------- FILE DROP: .f-drop with a hidden file input; fires 'f:files' {files} ---------- */
  F.drop = function (zone) {
    if (zone.hasAttribute('data-f-w')) return; zone.setAttribute('data-f-w', '');
    var input = $('input[type=file]', zone);
    var send = function (files) { if (files && files.length) zone.dispatchEvent(new CustomEvent('f:files', { bubbles: true, detail: { files: files } })); };
    zone.setAttribute('role', 'button'); if (!zone.hasAttribute('tabindex')) zone.tabIndex = 0;
    zone.addEventListener('click', function () { if (input) input.click(); });
    zone.addEventListener('keydown', function (e) { if ((e.key === 'Enter' || e.key === ' ') && input) { e.preventDefault(); input.click(); } });
    if (input) input.addEventListener('change', function () { send(input.files); });
    zone.addEventListener('dragover', function (e) {
      e.preventDefault();
      if (!zone.classList.contains('on')) { var r = zone.getBoundingClientRect(); F.ink(zone, true, e.clientX - r.left, e.clientY - r.top); }
    });
    zone.addEventListener('dragleave', function (e) { if (!zone.contains(e.relatedTarget)) F.ink(zone, false); });
    zone.addEventListener('drop', function (e) { e.preventDefault(); F.ink(zone, false); send(e.dataTransfer && e.dataTransfer.files); });
  };

  /* ---------- CALENDAR: soft-square days that round into circles when chosen; fires 'f:change' {value: Date} ---------- */
  var CHEV_L = '<svg class="f-i sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M10.83 12l4.95 4.95-1.41 1.41L8 12l6.37-6.36 1.41 1.41L10.83 12z"/></svg>';
  var CHEV_R = '<svg class="f-i sm" viewBox="0 0 24 24" aria-hidden="true"><path d="M13.17 12L8.22 7.05l1.41-1.41L16 12l-6.37 6.36-1.41-1.41L13.17 12z"/></svg>';
  function sameDay(a, b) { return a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
  F.calendar = function (el, opts) {
    opts = opts || {};
    var attr = el.getAttribute('data-value');
    var value = opts.value ? new Date(opts.value) : attr ? new Date(attr + 'T00:00') : null;
    var view = new Date((value || new Date()).getFullYear(), (value || new Date()).getMonth(), 1);
    var today = new Date();
    el.classList.add('f-cal');
    function render(dir) {
      var y = view.getFullYear(), m = view.getMonth();
      var first = new Date(y, m, 1).getDay(), days = new Date(y, m + 1, 0).getDate();
      var cells = [];
      for (var i = 0; i < first; i++) cells.push(new Date(y, m, i - first + 1));
      for (var d = 1; d <= days; d++) cells.push(new Date(y, m, d));
      while (cells.length % 7) cells.push(new Date(y, m + 1, cells.length - first - days + 1));
      el.innerHTML = '<div class="f-cal-head"><button type="button" class="f-icon-btn f-tap" data-step="-1" aria-label="Previous month">' + CHEV_L + '</button><span></span><button type="button" class="f-icon-btn f-tap" data-step="1" aria-label="Next month">' + CHEV_R + '</button></div><div class="f-cal-grid" role="grid"></div>';
      $('.f-cal-head span', el).textContent = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' }).format(view);
      var grid = $('.f-cal-grid', el);
      ['S', 'M', 'T', 'W', 'T', 'F', 'S'].forEach(function (w) { var s = document.createElement('span'); s.className = 'dow'; s.textContent = w; s.setAttribute('aria-hidden', 'true'); grid.appendChild(s); });
      cells.forEach(function (date) {
        var b = document.createElement('button'); b.type = 'button';
        b.className = 'f-choice f-ink f-tap' + (date.getMonth() !== m ? ' out' : '') + (sameDay(date, today) ? ' today' : '') + (sameDay(date, value) ? ' on' : '');
        b.textContent = date.getDate(); b.setAttribute('aria-pressed', sameDay(date, value));
        b.setAttribute('aria-label', new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' }).format(date));
        b.addEventListener('click', function () {
          if (date.getMonth() !== m) { value = date; view = new Date(date.getFullYear(), date.getMonth(), 1); render(); }
          else { $$('.f-choice', grid).forEach(function (x) { if (x !== b) { F.drain(x); x.setAttribute('aria-pressed', 'false'); } }); F.ink(b, true); b.setAttribute('aria-pressed', 'true'); value = date; }
          el.dispatchEvent(new CustomEvent('f:change', { bubbles: true, detail: { value: value } }));
          if (opts.onChange) opts.onChange(value);
        });
        grid.appendChild(b);
      });
      $$('[data-step]', el).forEach(function (b) { b.addEventListener('click', function () { view = new Date(view.getFullYear(), view.getMonth() + (+b.getAttribute('data-step')), 1); render(+b.getAttribute('data-step')); $('[data-step="' + b.getAttribute('data-step') + '"]', el).focus({ preventScroll: true }); }); });
      F.prime(el);
      if (dir) anim(grid, [{ opacity: 0, transform: 'translateX(' + (dir * 10) + 'px)' }, { opacity: 1, transform: 'none' }], { duration: T.plate, easing: EASE.plate });
    }
    render();
    return { get value() { return value; }, set: function (d) { value = new Date(d); view = new Date(value.getFullYear(), value.getMonth(), 1); render(); } };
  };

  /* ---------- NUMBERS ---------- */
  F.fmt = {
    money: function (n, cur, loc) { return new Intl.NumberFormat(loc || 'en-US', { style: 'currency', currency: cur || 'USD' }).format(+n || 0); },
    num: function (n, d, loc) { return new Intl.NumberFormat(loc || 'en-US', { minimumFractionDigits: d || 0, maximumFractionDigits: d || 0 }).format(+n || 0); },
    pct: function (n, d, loc) { return new Intl.NumberFormat(loc || 'en-US', { style: 'percent', minimumFractionDigits: d || 0, maximumFractionDigits: d || 0 }).format(+n || 0); },
    compact: function (n, loc) { return new Intl.NumberFormat(loc || 'en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(+n || 0); },
    date: function (d, o, loc) { return new Intl.DateTimeFormat(loc || 'en-US', o || { month: 'short', day: 'numeric' }).format(new Date(d)); }
  };

  /* ---------- VIEW MORPHS: elements sharing data-f-vt="name" morph between views (and pages) ---------- */
  F.nameViews = function (scope) {
    $$('[data-f-vt]', scope).forEach(function (el) { el.style.viewTransitionName = 'f-' + el.getAttribute('data-f-vt').replace(/[^a-zA-Z0-9_-]/g, '-'); });
  };
  /* Swap views in one step; named plates morph from their old place to their new one. Returns a Promise. */
  F.view = function (mutate) {
    if (!document.startViewTransition || reduce) { mutate(); F.nameViews(); return Promise.resolve(); }
    var t = document.startViewTransition(function () { mutate(); F.nameViews(); });
    return t.finished.catch(function () {});
  };

  /* ---------- FAVICON: the mark in the current accent ---------- */
  F.favicon = function () {
    var cs = getComputedStyle(root), a = cs.getPropertyValue('--accent').trim() || '#0e1015', o = cs.getPropertyValue('--on-accent').trim() || '#ffffff';
    var svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><circle cx="32" cy="32" r="32" fill="' + a + '"/><rect x="19.5" y="19.5" width="25" height="25" rx="7" fill="' + o + '"/></svg>';
    var link = $('link[rel="icon"]') || document.head.appendChild(Object.assign(document.createElement('link'), { rel: 'icon' }));
    link.href = 'data:image/svg+xml,' + encodeURIComponent(svg);
  };

  /* ---------- INIT ---------- */
  F.init = function () {
    root.classList.add('f-boot');
    document.body.classList.add('f-body');
    if ($('.f-dock')) document.body.classList.add('f-has-dock');
    if ($('[data-side]')) document.body.classList.add('f-has-inspector'); /* reserve the side panel's room from 1280px */
    F.wire(document);
    requestAnimationFrame(function () { requestAnimationFrame(function () { root.classList.remove('f-boot'); }); });
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { root.classList.add('f-boot'); F.glideAll(); F.prime(); requestAnimationFrame(function () { root.classList.remove('f-boot'); }); });
    addEventListener('resize', F.glideAll);
  };

  window.S4Flat = F;
})();
/* end S4 Flat */
