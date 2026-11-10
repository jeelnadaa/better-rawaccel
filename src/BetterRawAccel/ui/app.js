"use strict";
// Better Raw Accel interface. Every number on screen comes from the host (BetterRawAccel.exe),
// which computes curves with Raw Accel's own wrapper.dll, so the plot is what the driver will do.

const wv = window.chrome && window.chrome.webview;
const $ = id => document.getElementById(id);
const root = document.documentElement;
const RM = matchMedia("(prefers-reduced-motion: reduce)");
const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const store = { get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
                set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} } };

// ---------- bridge to the host ----------
let seq = 0;
const waiting = new Map();
function rpc(method, params) {
  return new Promise((resolve, reject) => {
    if (!wv) { reject(new Error("Not running inside the app")); return; }
    const id = ++seq;
    waiting.set(id, { resolve, reject });
    wv.postMessage({ id, method, params: params || {} });
  });
}
if (wv) wv.addEventListener("message", e => {
  const m = e.data;
  if (m && m.id) {
    const w = waiting.get(m.id); waiting.delete(m.id);
    if (m.state) applyState(m.state);
    if (w) { if (m.error) w.reject(new Error(m.error)); else w.resolve(m.result); }
  } else if (m && m.event) onEvent(m);
});

// ---------- static presentation data ----------
const THEMES = [
  { id: "paper",      name: "Paper",      kind: "light" },
  { id: "ledger",     name: "Ledger",     kind: "light" },
  { id: "parchment",  name: "Parchment",  kind: "light" },
  { id: "millimeter", name: "Millimeter", kind: "light" },
  { id: "technical",  name: "Technical",  kind: "light" },
  { id: "chalk",      name: "Chalk",      kind: "light" },
  { id: "blueprint",  name: "Blueprint",  kind: "dark" },
  { id: "graphite",   name: "Graphite",   kind: "dark" },
  { id: "phosphor",   name: "Phosphor",   kind: "dark" },
  { id: "obsidian",   name: "Obsidian",   kind: "dark" },
  { id: "midnight",   name: "Midnight",   kind: "dark" },
  { id: "radar",      name: "Radar",      kind: "dark" },
  { id: "cyberpunk",  name: "Cyberpunk",  kind: "dark" },
  { id: "solarized",  name: "Solarized",  kind: "dark" },
];
const FONTS = [
  { id: "satoshi", name: "Satoshi",         family: "Satoshi",         lic: "ITF FFL" },
  { id: "cabinet", name: "Cabinet Grotesk", short: "Cabinet", family: "Cabinet Grotesk", lic: "ITF FFL" },
  { id: "archivo", name: "Archivo",         family: "Archivo",         lic: "OFL" },
];
const TIPS = {
  mode:    ["Mode", "Click a tile or press <kbd>1</kbd>–<kbd>8</kbd>. All thumbnails share one scale, so shapes compare directly. Each mode keeps its own values."],
  label:   ["Scrub", "Drag the name left or right to scrub the value. Hold <kbd>Shift</kbd> for fine steps. Double-click resets to default."],
  value:   ["Exact value", "Type a number; <kbd>Enter</kbd> applies, <kbd>Esc</kbd> cancels. <kbd>↑</kbd><kbd>↓</kbd> step, <kbd>Shift</kbd> ×10. Wheel steps while focused. Typed values may go past the ruler ends."],
  ruler:   ["Ruler", "Click to jump, drag to set. <kbd>Shift</kbd>-drag moves at 1/10 speed. Double-click resets to the default ▾. Focused: <kbd>←</kbd><kbd>→</kbd> step, <kbd>Shift</kbd> ×10, <kbd>Home</kbd>/<kbd>End</kbd>."],
  axis:    ["Axis", "X and Y have separate curves in this profile. Choose which one the controls below edit; both are drawn on the plot."],
  write:   ["Write to driver", "Sends every profile and device setting to the kernel driver and saves settings.json. <kbd>Ctrl</kbd>+<kbd>Enter</kbd> works from anywhere. The driver waits one second before using new settings."],
  status:  ["Driver state", "Whether the driver matches what you see. Unwritten changes are counted here in the accent colour."],
  undo:    ["Undo · redo", "Step back and forward through your edits. <kbd>Ctrl</kbd>+<kbd>Z</kbd> and <kbd>Ctrl</kbd>+<kbd>Y</kbd>."],
  revert:  ["Discard", "Drop every change that has not been written and go back to what the driver has. Undo brings them back."],
  in:      ["Live marker", "Your current hand speed, worked back from raw mouse input through the curve that is active in the driver. The ring and crosshair show where it sits on the curve."],
  sens:    ["Sensitivity", "The multiplier the edited curve gives at the live hand speed: the y value under the marker."],
  out:     ["Output speed", "Hand speed × sensitivity: what the cursor would receive with the curve you are editing."],
  peak:    ["Peak hold", "Fastest hand speed in the last 3 seconds, also marked ▲ on the x axis. Flick, then check where the flick landed on the curve."],
  view:    ["Plot", "Sensitivity is output speed ÷ hand speed. Velocity is the output speed itself, against the dashed 1:1 line. Gain is the slope of velocity: how much output one more count of hand speed adds."],
  legend:  ["Curves", "Solid accent: what you are editing. Dashed: what the driver has now. Solid ink: the Y axis curve when X and Y are apart."],
  theme:   ["Sheet", "Colour theme. Remembered on this machine."],
  font:    ["Type", "Interface typeface. Numbers always stay in IBM Plex Mono."],
  help:    ["How to use", "Every control and shortcut on one sheet. Shortcut <kbd>?</kbd>"],
  profile: ["Profile", "The profile being edited. Open to switch, add, duplicate, rename or delete profiles."],
  device:  ["Device", "Plot scale, and per-mouse settings: DPI, polling rate, profile, or leave a mouse alone."],
  driver:  ["Driver", "Connection to the Raw Accel driver. The square blinks on a write or an MCP call. Open for start-up, MCP, updates, import and export."],
  cm360:   ["Sensitivity & cm/360", "Calculate cm/360, compare sensitivities across games (Source/CS2, Valorant, Overwatch 2, Apex, Fortnite, etc.), and view the effective range of your curve."],
  pin:     ["Pin reference curve", "Overlay a snapshot of the current curve or another profile on the plot to compare changes directly."],
  hist:    ["Speed histogram", "Shows where your hand moves during tracking and flicks as a distribution under the curve."],
};

// ---------- state ----------
let S = null;                 // last state from the host (merged)
let META = null, MODES = [], MODE = {};
let axis = "x";
const sticky = { x: false, y: false };   // "classic" stays selected even when its exponent is 2
let N = 240, maxX = 80;
let target = new Float32Array(N), shown = new Float32Array(N).fill(1), vel = new Float32Array(N);
let targetY = new Float32Array(N), shownY = new Float32Array(N).fill(1), velY = new Float32Array(N);
let inDriver = new Float32Array(N).fill(1);
let hasY = false, hasDriver = false, pending = 0, writing = false, sweepT = -1;
let pendingPatch = null, inFlight = false;

const args = () => S.profile[axis];
function uiModeOf(a, stick) {
  switch (a.mode) {
    case "noaccel": return "off";
    case "classic": return stick || a.exponentClassic !== 2 ? "classic" : "linear";
    default: return a.mode;
  }
}
const curMode = () => uiModeOf(args(), sticky[axis]);

function fillCurve(dst, src) {
  if (!src) return false;
  if (dst.length !== src.length) return false;
  for (let i = 0; i < src.length; i++) dst[i] = src[i];
  return true;
}

function applyState(st) {
  const full = "meta" in st;
  if (!full && pendingPatch) {
    // A newer local edit is waiting to be sent: keep our values, take the fresh curve.
    st = Object.assign({}, st); delete st.profile;
  }
  S = Object.assign(S || {}, st);
  if (full && !META) { META = S.meta; MODES = META.modes; MODE = Object.fromEntries(MODES.map(m => [m.id, m])); buildTiles(); buildStatic(); }
  if (S.maxSpeed) maxX = S.maxSpeed;
  if (S.curve) {
    fillCurve(target, S.curve.x);
    hasY = !!S.curve.y && fillCurve(targetY, S.curve.y);
  }
  if (full) hasDriver = !!S.driverCurve && fillCurve(inDriver, S.driverCurve.x);
  if (S.profile.whole && axis === "y") axis = "x";
  render(full);
}

// ---------- mode tiles ----------
const modesEl = $("modes"), selEl = $("sel"), btns = {};
const SPEC_Y = 4;
function thumbPath(arr) {
  if (!arr || !arr.length) return "";
  let d = ""; const n = arr.length, stepBy = Math.max(1, Math.floor(n / 64));
  for (let i = 0; i < n; i += stepBy) {
    const y = clamp(arr[i], 0, SPEC_Y);
    d += (i ? "L" : "M") + (i / (n - 1) * 100).toFixed(2) + "," + (30 - y / SPEC_Y * 30).toFixed(2);
  }
  return d;
}
function buildTiles() {
  MODES.forEach((m, i) => {
    const b = document.createElement("button");
    b.dataset.m = m.id; b.dataset.tip = "mode"; b.setAttribute("aria-label", `${m.label} mode (key ${i + 1})`);
    b.innerHTML = `<span class="nm"><span class="t">${esc(m.label)}</span><em>${i + 1}</em></span>
      <svg viewBox="0 0 100 30" preserveAspectRatio="none" aria-hidden="true"><line class="u" x1="0" x2="100" y1="${30 - 30 / SPEC_Y}" y2="${30 - 30 / SPEC_Y}" vector-effect="non-scaling-stroke"/><path class="c" vector-effect="non-scaling-stroke"/></svg>`;
    b.onclick = () => setMode(m.id);
    modesEl.appendChild(b); btns[m.id] = b;
  });
  new ResizeObserver(() => { placeSel(); fitTiles(); }).observe(modesEl);
}
function placeSel() {
  if (!S) return;
  const b = btns[curMode()]; if (!b) return;
  selEl.style.transform = `translate(${b.offsetLeft}px, ${b.offsetTop}px)`;
  selEl.style.width = b.offsetWidth + "px"; selEl.style.height = b.offsetHeight + "px";
}
function fitTiles() {
  for (const id in btns) {
    const b = btns[id], nm = b.querySelector(".nm"), t = b.querySelector(".t");
    b.classList.remove("tight"); t.textContent = MODE[id].label;
    if (nm.scrollWidth > nm.clientWidth + 0.5) {
      b.classList.add("tight");
      if (nm.scrollWidth > nm.clientWidth + 0.5 && MODE[id].short) t.textContent = MODE[id].short;
    }
  }
}
function setMode(id) {
  if (!S || id === curMode()) return;
  sticky[axis] = id === "classic";
  flushNow().then(() => rpc("set_mode", { mode: id, axis })).catch(fail);
}

// ---------- parameter rows ----------
const decimals = s => { let d = 0; while (d < 6 && Math.abs(Math.round(s * 10 ** d) - s * 10 ** d) > 1e-9) d++; return d; };
const TICKS = (() => {
  let s = "";
  for (let i = 0; i <= 40; i++) {
    const x = i * 2.5, maj = i % 10 === 0, mid = i % 5 === 0;
    s += `<line ${maj ? 'class="M"' : ""} x1="${x}" x2="${x}" y1="${maj ? 0 : mid ? 3 : 6}" y2="10"/>`;
  }
  return `<svg viewBox="0 0 100 10" preserveAspectRatio="none" aria-hidden="true">${s}</svg>`;
})();

function numberRow(d, get, set, index) {
  const { min, max, step } = d, dec = decimals(step);
  const hmin = d.hmin == null ? min : d.hmin, hmax = d.hmax == null ? max : d.hmax;
  const row = document.createElement("div"); row.className = "prm";
  row.style.animationDelay = (index * 45) + "ms";
  row.style.setProperty("--d", clamp((d.def - min) / (max - min), 0, 1));
  row.innerHTML = `<div class="prm-h"><span class="prm-l" data-tip="label">${esc(d.label)}</span>${d.unit ? `<span class="prm-u">${esc(d.unit)}</span>` : ""}${d.hint ? `<span class="hint">${esc(d.hint)}</span>` : ""}
      <input class="prm-v" type="text" inputmode="decimal" spellcheck="false" autocomplete="off" aria-label="${esc(d.label)}" data-tip="value"></div>
    <div class="scale" tabindex="0" role="slider" aria-label="${esc(d.label)} ruler" aria-valuemin="${min}" aria-valuemax="${max}" data-tip="ruler">${TICKS}<div class="def"></div><div class="fill"></div><div class="needle"></div></div>
    <div class="scale-l"><span>${min}</span><span>${max}</span></div>`;
  const inp = row.querySelector("input"), sc = row.querySelector(".scale"), lab = row.querySelector(".prm-l");
  const fmt = v => { const s = v.toFixed(dec); return Math.abs(+s - v) < 1e-9 ? s : String(+v.toFixed(6)); };
  let dragging = false;

  const paint = v => {
    sc.style.setProperty("--p", clamp((v - min) / (max - min), 0, 1));
    sc.setAttribute("aria-valuenow", v);
    if (document.activeElement !== inp) inp.value = fmt(v);
  };
  // quantize: values from the ruler snap to its step and stay on it; typed values only obey the hard limits.
  const put = (v, quantize) => {
    if (!isFinite(v)) return;
    if (quantize) { v = clamp(v, min, max); v = +(Math.round((v - min) / step) * step + min).toFixed(dec); v = clamp(v, Math.max(min, hmin), Math.min(max, hmax)); }
    else v = +clamp(v, hmin, hmax).toFixed(6);
    if (v === get()) { paint(v); return; }
    set(v); paint(v);
  };
  const nudge = (base, dir, shift) => put(+(base + dir * step * (shift ? 10 : 1)).toFixed(6), false);

  const drag = (el, absolute, pxRange) => {
    let raw = 0, lastX = 0, active = false, moved = false;
    el.addEventListener("pointerdown", e => {
      if (e.button !== 0) return;
      e.preventDefault(); el.setPointerCapture(e.pointerId); active = true; moved = false; lastX = e.clientX;
      if (absolute) sc.focus({ preventScroll: true });
      raw = clamp(get(), min, max);
      if (absolute && !e.shiftKey) { const r = sc.getBoundingClientRect(); raw = min + (max - min) * clamp((e.clientX - r.left) / r.width, 0, 1); put(raw, true); }
    });
    el.addEventListener("pointermove", e => {
      if (!active) return;
      const dx = e.clientX - lastX; lastX = e.clientX; if (!dx) return;
      if (!moved) { moved = true; dragging = true; row.classList.add("drag"); }
      const span = pxRange || sc.getBoundingClientRect().width;
      raw = clamp(raw + dx / span * (max - min) * (e.shiftKey ? 0.1 : 1), min, max);
      put(raw, true);
    });
    const end = () => { active = false; dragging = false; row.classList.remove("drag"); };
    el.addEventListener("pointerup", end); el.addEventListener("pointercancel", end);
    el.addEventListener("dblclick", () => put(d.def, false));
  };
  drag(sc, true); drag(lab, false, 260);
  sc.addEventListener("keydown", e => {
    const v = get(), mul = e.shiftKey ? 10 : 1;
    const map = { ArrowRight: v + step * mul, ArrowUp: v + step * mul, ArrowLeft: v - step * mul, ArrowDown: v - step * mul, Home: min, End: max, PageUp: v + step * 10, PageDown: v - step * 10 };
    if (e.key in map) { e.preventDefault(); put(+map[e.key].toFixed(6), e.key === "Home" || e.key === "End"); }
  });

  const parse = () => parseFloat(inp.value.replace(",", "."));
  const commit = () => {
    const v = parse();
    if (!isFinite(v)) { inp.value = fmt(get()); return; }
    if (v < hmin || v > hmax) { inp.classList.remove("clamped"); void inp.offsetWidth; inp.classList.add("clamped"); setTimeout(() => inp.classList.remove("clamped"), 900); }
    put(v, false); inp.value = fmt(get());
  };
  inp.addEventListener("focus", () => requestAnimationFrame(() => inp.select()));
  inp.addEventListener("blur", commit);
  inp.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.ctrlKey) inp.blur();
    else if (e.key === "Escape") { inp.value = fmt(get()); inp.blur(); }
    else if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault();
      const cur = parse(); nudge(isFinite(cur) ? cur : get(), e.key === "ArrowUp" ? 1 : -1, e.shiftKey);
      inp.value = fmt(get());
    }
  });
  inp.addEventListener("wheel", e => {
    if (document.activeElement !== inp) return;
    e.preventDefault(); nudge(get(), e.deltaY < 0 ? 1 : -1, e.shiftKey); inp.value = fmt(get());
  }, { passive: false });

  paint(get());
  return { el: row, sync() { if (!dragging) paint(get()); } };
}

function choiceRow(d, get, set, index) {
  const row = document.createElement("div"); row.className = "prm ch";
  row.style.animationDelay = (index * 45) + "ms";
  row.innerHTML = `<div class="prm-h"><span class="prm-l plain">${esc(d.label)}</span><div class="seg">${d.options.map((o, i) => `<button data-i="${i}">${esc(o[1])}</button>`).join("")}</div></div>`;
  const bs = [...row.querySelectorAll("button")];
  const sync = () => bs.forEach((b, i) => b.setAttribute("aria-pressed", d.options[i][0] === get()));
  bs.forEach((b, i) => b.onclick = () => { if (d.options[i][0] !== get()) { set(d.options[i][0]); sync(); } });
  sync();
  return { el: row, sync };
}

function lutRow(d, get, set) {
  const row = document.createElement("div"); row.className = "prm lut";
  row.innerHTML = `<div class="prm-h"><span class="prm-l plain">${esc(d.label)}</span><span class="hint">one “speed, value” per line</span></div>
    <textarea spellcheck="false" aria-label="Table points" placeholder="0, 1&#10;20, 1.5&#10;60, 2"></textarea>
    <div class="row"><small></small><span class="sp"></span><button class="bt">Use points</button></div>`;
  const ta = row.querySelector("textarea"), info = row.querySelector("small");
  const text = pts => (pts || []).map(p => `${+p[0]}, ${+p[1]}`).join("\n");
  const sync = () => { if (document.activeElement !== ta) ta.value = text(get()); info.textContent = `${(get() || []).length} point(s) in use · needs at least 2`; };
  row.querySelector("button").onclick = () => {
    const pts = [];
    for (const line of ta.value.split(/\n/)) {
      const t = line.trim(); if (!t) continue;
      const m = t.split(/[\s,;]+/).map(Number);
      if (m.length !== 2 || !m.every(isFinite)) { notice(`Could not read “${t}”. Use: speed, value`, true); return; }
      pts.push(m);
    }
    set(pts);
  };
  sync();
  return { el: row, sync };
}

function makeRow(d, get, set, index) {
  if (d.type === "choice") return choiceRow(d, get, set, index);
  if (d.type === "lut") return lutRow(d, get, set);
  return numberRow(d, get, set, index);
}

// ---------- editing: optimistic locally, coalesced to the host ----------
function merge(into, patch) {
  for (const k in patch) {
    if (patch[k] && typeof patch[k] === "object" && !Array.isArray(patch[k])) into[k] = merge(Object.assign({}, into[k]), patch[k]);
    else into[k] = patch[k];
  }
  return into;
}
function edit(patch) {
  merge(S.profile, patch);
  pendingPatch = merge(pendingPatch || {}, patch);
  if (!inFlight) flush();
  renderParams(); updateStatus();
}
function flush() {
  if (!pendingPatch) return Promise.resolve();
  inFlight = true;
  const p = pendingPatch; pendingPatch = null;
  return rpc("update_profile", { patch: p }).catch(fail).then(() => { inFlight = false; if (pendingPatch) return flush(); });
}
function flushNow() { return new Promise(res => { const wait = () => (inFlight || pendingPatch) ? setTimeout(wait, 10) : res(); if (pendingPatch && !inFlight) flush(); wait(); }); }
function fail(e) { notice(e && e.message ? e.message : String(e), true); }

// ---------- sections ----------
const paramsEl = $("params"), outputEl = $("output"), advEl = $("advanced");
let paramRows = [], paramKey = "", staticRows = [];

function renderParams() {
  const a = args(), id = curMode(), m = MODE[id];
  const defs = m.params.map(k => META.args[k]).filter(d => !d.when || Object.keys(d.when).every(f => d.when[f].includes(a[f])));
  const key = axis + "|" + id + "|" + defs.map(d => d.field + d.label).join(",");
  if (key !== paramKey) {
    paramKey = key; paramsEl.innerHTML = ""; paramRows = [];
    const ax = axis;
    defs.forEach((d, i) => {
      const r = makeRow(d, () => S.profile[ax][d.field], v => edit({ [ax]: { [d.field]: v } }), i);
      paramRows.push(r); paramsEl.appendChild(r.el);
    });
    if (!defs.length) paramsEl.innerHTML = `<p class="empty">No acceleration. The sensitivity multiplier under Output still applies.</p>`;
    $("prmHint").textContent = defs.some(d => !d.type) && S.profile.whole ? "drag · type · ↑↓" : "";
  } else paramRows.forEach(r => r.sync());
}

function buildStatic() {
  const add = (el, list) => list.forEach((d, i) => {
    const r = makeRow(d, () => S.profile[d.field], v => edit({ [d.field]: v }), i);
    staticRows.push(r); el.appendChild(r.el);
  });
  add(outputEl, META.output); add(advEl, META.advanced);
  const fold = $("advFold"), head = $("advHead");
  const setFold = open => { fold.classList.toggle("closed", !open); head.setAttribute("aria-expanded", open); store.set("ra.adv", open ? "1" : ""); };
  setFold(store.get("ra.adv") === "1");
  head.onclick = () => setFold(fold.classList.contains("closed"));
  head.onkeydown = e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); head.onclick(); } };
  $("axisSeg").querySelectorAll("button").forEach(b => b.onclick = () => { axis = b.dataset.a; render(false); });
}

const anno = $("anno");
let annoMode = "";
function render(full) {
  const id = curMode();
  for (const k in btns) { btns[k].classList.toggle("on", k === id); btns[k].setAttribute("aria-pressed", k === id); }
  placeSel();
  if (full && S.thumbs) for (const k in btns) btns[k].querySelector(".c").setAttribute("d", thumbPath(S.thumbs[k]));
  if (S.curve && axis === "x") btns[id].querySelector(".c").setAttribute("d", thumbPath(S.curve.x));

  const seg = $("axisSeg"); seg.hidden = S.profile.whole;
  seg.querySelectorAll("button").forEach(b => b.setAttribute("aria-pressed", b.dataset.a === axis));
  renderParams(); staticRows.forEach(r => r.sync());

  if (annoMode !== id + axis) {
    annoMode = id + axis;
    anno.querySelector("b").textContent = MODE[id].label + (S.profile.whole ? "" : " · " + axis.toUpperCase());
    anno.querySelector("span").textContent = MODE[id].note;
    anno.classList.remove("swap"); void anno.offsetWidth; anno.classList.add("swap");
  }

  if (full) {
    $("profV").textContent = S.profile.name;
    $("devV").textContent = `${S.settings.plotDpi} dpi · ${S.settings.plotPollRate} Hz`;
    $("rev").textContent = `v${S.app.version}${S.app.dev ? " dev" : ""} · driver ${S.app.driverVersion}`;
    $("drvV").textContent = "connected";
    root.style.setProperty("--wd", ((S.app.writeDelayMs || 1000) + 60) + "ms");
    if (S.settings.theme !== root.dataset.theme) setTheme(S.settings.theme, true, false);
    if (S.settings.font !== root.dataset.font) setFont(S.settings.font, false);
    if (S.settings.plotView && S.settings.plotView !== view) setView(S.settings.plotView, false);
    helpBtn.classList.toggle("new", !S.settings.helpSeen);
    refreshSheet();
  }
  $("undoBtn").disabled = !S.canUndo; $("redoBtn").disabled = !S.canRedo; $("revertBtn").disabled = !S.pending;
  updateStatus();
}

// ---------- driver state ----------
const st = $("st"), legend = $("legend"), writeBtn = $("write"), wlabel = $("wlabel"), led = $("led");
function flat(o, pre, out) {
  for (const k in o) {
    const v = o[k], key = pre + k;
    if (Array.isArray(v)) out[key] = JSON.stringify(v);
    else if (v && typeof v === "object") flat(v, key + ".", out);
    else out[key] = v;
  }
  return out;
}
function updateStatus() {
  if (!S) return;
  const dp = S.driverProfile;
  let text, pend = true;
  if (S.errors) { text = S.errors; pending = 1; }
  else if (!dp) { text = "Profile not in the driver yet"; pending = 1; }
  else {
    const a = flat(S.profile, "", {}), b = flat(dp, "", {});
    let n = 0; for (const k in a) if (a[k] !== b[k]) n++;
    const dm = uiModeOf(dp.x, false), em = uiModeOf(S.profile.x, sticky.x);
    pending = n;
    if (dm !== em && !(dm === "linear" && em === "classic")) text = `Driver still on ${MODE[dm].label}`;
    else if (n > 0) text = `${n} change${n > 1 ? "s" : ""} not written`;
    else if (S.pending && !pendingPatch && !inFlight) { text = "Other profiles or devices not written"; pending = 1; hasDriver && (pending = -2); }
    else { text = "Driver matches" + (S.writtenAt ? " · " + S.writtenAt : ""); pend = false; }
  }
  if (writing) text = "Writing…";
  st.classList.toggle("pend", pend); st.textContent = text; st.title = text;
  const ghost = pend && hasDriver && pending !== -2 && !!dp;
  ghostOn = ghost;
  const hasRef = !!(S.pinnedCurve && S.pinnedCurve.x);
  const refEl = legend.querySelector('[data-l="ref"]');
  if (refEl) {
    refEl.classList.toggle("on", hasRef);
    if (hasRef && $("refLabel")) $("refLabel").textContent = S.pinnedCurve.name || "reference";
  }
  legend.querySelector('[data-l="drv"]').classList.toggle("on", ghost);
  legend.querySelector('[data-l="y"]').classList.toggle("on", hasY);
  legend.querySelector('[data-l="edit"]').classList.toggle("on", ghost || hasY || hasRef);
  legend.classList.toggle("on", ghost || hasY || hasRef);
  updateCm360Readout();
}
let ghostOn = false, showHistogram = false;

$("pinBtn").onclick = () => call("pin_reference_curve", { source: "editing" });
$("unpinBtn").onclick = () => call("clear_reference_curve", {});
$("histBtn").onclick = () => {
  showHistogram = !showHistogram;
  $("histBtn").setAttribute("aria-pressed", showHistogram);
};
$("exportPngBtn").onclick = () => {
  const url = cv.toDataURL("image/png");
  const a = document.createElement("a");
  a.download = `RawAccel-${(S?.profile?.name || "curve").toLowerCase()}.png`;
  a.href = url;
  a.click();
  notice("Plot image exported as PNG", false);
};

async function write() {
  if (writing || !S) return;
  if (document.activeElement && document.activeElement.classList.contains("prm-v")) document.activeElement.blur();
  await flushNow();
  writing = true; wlabel.textContent = "Writing…";
  writeBtn.classList.remove("done"); writeBtn.classList.add("writing"); updateStatus();
  let ok = false;
  try { await rpc("apply"); ok = true; } catch (e) { fail(e); }
  writing = false;
  writeBtn.classList.remove("writing");
  if (ok) {
    sweepT = RM.matches ? -1 : 0;
    writeBtn.classList.add("done"); wlabel.textContent = "Written"; blink("tx");
    setTimeout(() => { writeBtn.classList.remove("done"); wlabel.textContent = "Write to driver"; }, 1300);
  } else wlabel.textContent = "Write to driver";
  updateStatus();
}
writeBtn.onclick = write;
function blink(cls) { led.classList.remove("tx", "mcp"); void led.offsetWidth; led.classList.add(cls); }
$("undoBtn").onclick = () => flushNow().then(() => rpc("undo")).catch(fail);
$("redoBtn").onclick = () => flushNow().then(() => rpc("redo")).catch(fail);
$("revertBtn").onclick = () => flushNow().then(() => rpc("revert")).then(() => notice("Unwritten changes dropped. Ctrl+Z brings them back.")).catch(fail);

// ---------- notices ----------
const noticeEl = $("notice"); let noticeTimer = 0;
function notice(text, bad, label) {
  noticeEl.innerHTML = `<b>${esc(label || (bad ? "NOT DONE" : "NOTE"))}</b>${esc(text)}`;
  noticeEl.classList.toggle("bad", !!bad); noticeEl.classList.add("on");
  clearTimeout(noticeTimer); noticeTimer = setTimeout(() => noticeEl.classList.remove("on"), bad ? 6500 : 3800);
}

// ---------- events from the host ----------
let speedT = 0, lastInputAt = 0;
function onEvent(m) {
  switch (m.event) {
    case "input": speedT = m.in; lastInputAt = performance.now(); break;
    case "state":
      applyState(m.state);
      if (m.source === "mcp") { blink("mcp"); notice(m.tool, false, "MCP"); }
      break;
    case "mcp": if (S && m.mcp) S.mcp = m.mcp; if (m.phase === "start") blink("mcp"); else refreshSheet(); break;
    case "update":
      if (!S) break;
      { const was = S.update && S.update.state; S.update = m.update; refreshSheet();
        if (m.update.state === "ready" && was !== "ready") notice(`Version ${m.update.latest} is downloaded. It installs when you close the app, or now from Driver.`, false, "UPDATE"); }
      break;
    case "devices": if (S) { S.devices = m.devices; refreshSheet(); } break;
    case "panel":
      { const b = { profiles: $("profBtn"), devices: $("devBtn"), system: $("sysBtn"), help: helpBtn, themes: themeBtn, fonts: fontBtn, cm360: $("cm360Btn") }[m.panel];
        closePop(); if (b) b.click(); }
      break;
  }
}

// ---------- callout tooltips ----------
const tip = $("tip"), lead = $("lead"), PREF = { mode: "right", label: "right", value: "right", ruler: "right", write: "right", status: "right", undo: "right", revert: "right", axis: "right",
  in: "top", sens: "top", out: "top", peak: "top", legend: "top", view: "bottom", theme: "bottom", font: "bottom", help: "bottom", driver: "bottom", profile: "bottom", device: "bottom", cm360: "bottom", pin: "bottom", hist: "bottom" };
const TIP_NO = Object.fromEntries(Object.keys(TIPS).map((k, i) => [k, String(i + 1).padStart(2, "0")]));
let tipEl = null, tipTimer = 0, hideTimer = 0;
function showTip(el) {
  const key = el.dataset.tip, d = TIPS[key]; if (!d) return;
  tipEl = el;
  tip.querySelector(".k i").textContent = TIP_NO[key];
  tip.querySelector(".k span").textContent = d[0];
  tip.querySelector("p").innerHTML = d[1];
  tip.classList.add("on");
  const r = el.getBoundingClientRect(), w = tip.offsetWidth, h = tip.offsetHeight, G = 16, M = 8, VW = innerWidth, VH = innerHeight;
  const order = { right: ["right", "left", "bottom", "top"], top: ["top", "bottom", "right", "left"], bottom: ["bottom", "top", "right", "left"] }[PREF[key] || "bottom"];
  const fits = {
    right:  () => r.right + G + w <= VW - M,
    left:   () => r.left - G - w >= M,
    bottom: () => r.bottom + G + h <= VH - M,
    top:    () => r.top - G - h >= M,
  };
  const side = order.find(s => fits[s]()) || order[0];
  let x, y;
  if (side === "right" || side === "left") {
    x = side === "right" ? r.right + G : r.left - G - w;
    y = clamp(r.top + r.height / 2 - 14, M, VH - h - M);
  } else {
    y = side === "bottom" ? r.bottom + G : r.top - G - h;
    x = clamp(r.left + Math.min(r.width / 2, 40) - 20, M, VW - w - M);
  }
  x = clamp(x, M, VW - w - M); y = clamp(y, M, VH - h - M);
  tip.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
  lead.className = "lead on";
  if (side === "right" || side === "left") {
    const ly = clamp(r.top + r.height / 2, Math.max(r.top, y) + 4, Math.min(r.bottom, y + h) - 4);
    const x0 = side === "right" ? r.right + 2 : x + w, x1 = side === "right" ? x : r.left - 2;
    lead.classList.add("h", side === "right" ? "a" : "b");
    Object.assign(lead.style, { transform: `translate(${Math.round(x0)}px, ${Math.round(ly)}px)`, width: Math.max(0, x1 - x0) + "px", height: "" });
  } else {
    const lx = clamp(r.left + Math.min(r.width / 2, 40), Math.max(r.left, x) + 4, Math.min(r.right, x + w) - 4);
    const y0 = side === "bottom" ? r.bottom + 2 : y + h, y1 = side === "bottom" ? y : r.top - 2;
    lead.classList.add("v", side === "bottom" ? "a" : "b");
    Object.assign(lead.style, { transform: `translate(${Math.round(lx)}px, ${Math.round(y0)}px)`, height: Math.max(0, y1 - y0) + "px", width: "" });
  }
}
function hideTip() { clearTimeout(tipTimer); tipEl = null; tip.classList.remove("on"); lead.classList.remove("on"); }
const tipTarget = e => e.target.closest && e.target.closest("[data-tip]");
document.addEventListener("pointerover", e => {
  const el = tipTarget(e); if (!el || el === tipEl) return;
  clearTimeout(tipTimer); clearTimeout(hideTimer);
  if (document.querySelector(".pop.on")) return;
  tipTimer = setTimeout(() => showTip(el), tipEl ? 60 : 450);
});
document.addEventListener("pointerout", e => {
  const el = tipTarget(e); if (!el) return;
  if (e.relatedTarget && el.contains(e.relatedTarget)) return;
  clearTimeout(tipTimer); hideTimer = setTimeout(() => { if (document.activeElement !== tipEl) hideTip(); }, 80);
});
document.addEventListener("focusin", e => {
  const el = tipTarget(e);
  if (el && el.matches(":focus-visible") && !document.querySelector(".pop.on")) { clearTimeout(tipTimer); showTip(el); } else if (!el) hideTip();
});
document.addEventListener("focusout", e => { if (tipTarget(e) === tipEl) hideTip(); });
document.addEventListener("pointerdown", hideTip, true);
addEventListener("resize", hideTip);

// ---------- popovers: menus, notes, sheets ----------
const menuEl = $("menu"), notesEl = $("notes"), sheetEl = $("sheet"), helpBtn = $("helpBtn"), themeBtn = $("themeBtn"), fontBtn = $("fontBtn"), cm360Btn = $("cm360Btn"), scrimEl = $("scrim");
let popOwner = null, sheetRender = null;
function placePop(pop, btn, alignRight) {
  const r = btn.getBoundingClientRect(), w = pop.offsetWidth, M = 8;
  const x = alignRight ? r.right - w : r.left;
  pop.style.left = Math.round(clamp(x, M, Math.max(M, innerWidth - w - M))) + "px";
  pop.style.top = Math.round(r.bottom + 4) + "px";
}
function closePop(refocus) {
  if (!popOwner) return;
  menuEl.classList.remove("on"); notesEl.classList.remove("on"); sheetEl.classList.remove("on");
  if (scrimEl) scrimEl.classList.remove("on");
  popOwner.setAttribute("aria-expanded", "false");
  if (refocus) popOwner.focus();
  popOwner = null; sheetRender = null;
}
if (scrimEl) scrimEl.onclick = () => closePop(true);
function openMenu(btn, title, items, current, pick) {
  if (popOwner === btn) { closePop(); return; }
  closePop(); hideTip();
  menuEl.innerHTML = `<div class="k">${title}</div>` + items.map(it =>
    `<button role="menuitemradio" aria-checked="${it.id === current}" data-id="${it.id}" style="${it.style || ""}">${it.html}</button>`).join("");
  menuEl.classList.add("on"); popOwner = btn; btn.setAttribute("aria-expanded", "true");
  if (scrimEl) scrimEl.classList.add("on");
  placePop(menuEl, btn, true);
  const list = [...menuEl.querySelectorAll("button")];
  list.forEach(b => b.onclick = () => { pick(b.dataset.id); closePop(true); });
  (list.find(b => b.getAttribute("aria-checked") === "true") || list[0]).focus();
  menuEl.onkeydown = e => {
    const i = list.indexOf(document.activeElement);
    if (e.key === "ArrowDown") { e.preventDefault(); list[(i + 1) % list.length].focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); list[(i - 1 + list.length) % list.length].focus(); }
    else if (e.key === "Tab") closePop();
  };
}
function toggleNotes() {
  if (popOwner === helpBtn) { closePop(true); return; }
  closePop(); hideTip();
  notesEl.classList.add("on"); popOwner = helpBtn; helpBtn.setAttribute("aria-expanded", "true");
  if (scrimEl) scrimEl.classList.add("on");
  placePop(notesEl, helpBtn, false);
  helpBtn.classList.remove("new");
  if (S && !S.settings.helpSeen) rpc("set_app_settings", { helpSeen: true }).catch(() => {});
  $("notesClose").focus({ preventScroll: true });
}
helpBtn.onclick = toggleNotes;
$("notesClose").onclick = () => closePop(true);
document.addEventListener("pointerdown", e => {
  if (popOwner && !e.target.closest(".pop") && !popOwner.contains(e.target)) closePop();
});
addEventListener("resize", () => closePop());

function openSheet(btn, title, sub, renderBody) {
  if (popOwner === btn) { closePop(true); return; }
  closePop(); hideTip();
  sheetEl.innerHTML = `<header><h3>${esc(title)}</h3><span>${esc(sub)}</span><button data-x>Esc · close</button></header><div class="body"></div>`;
  sheetEl.querySelector("[data-x]").onclick = () => closePop(true);
  sheetEl.setAttribute("aria-label", title);
  popOwner = btn; btn.setAttribute("aria-expanded", "true");
  if (scrimEl) scrimEl.classList.add("on");
  sheetRender = () => renderBody(sheetEl.querySelector(".body"));
  sheetRender();
  sheetEl.classList.add("on");
  placePop(sheetEl, btn, true);
}
function refreshSheet() {
  if (!sheetRender) return;
  const f = document.activeElement;
  if (f && sheetEl.contains(f) && (f.tagName === "INPUT" && f.type !== "checkbox")) return;   // do not pull text from under the cursor
  const top = sheetEl.scrollTop; sheetRender(); sheetEl.scrollTop = top;
}
const call = (method, params, done) => flushNow().then(() => rpc(method, params)).then(r => { if (done) done(r); return r; }).catch(fail);
const intOf = el => { const v = parseInt(el.value, 10); return isFinite(v) ? v : null; };

// sensitivity and cm/360
let sensCalc = { game: "source", gameSens: 1.0, dpi: 0 };
function updateCm360Readout() {
  const el = $("cm360V");
  if (!el || !S) return;
  const dpi = sensCalc.dpi || S.settings?.plotDpi || 1600;
  const yaws = { source: 0.022, cs2: 0.022, valorant: 0.07, overwatch: 0.0066, overwatch2: 0.0066, fortnite: 0.005555, rainbow6: 0.00572 };
  const yaw = yaws[sensCalc.game] || 0.022;
  const baseCm = (360.0 / (sensCalc.gameSens * yaw * dpi)) * 2.54;
  const profSens = S.profile?.sens || 1.0;
  const effCm = baseCm / profSens;
  el.textContent = isFinite(effCm) && effCm > 0 ? `${effCm.toFixed(1)} cm` : "calc";
}

if (cm360Btn) {
  cm360Btn.onclick = () => openSheet(cm360Btn, "Sensitivity & cm/360", "angles · game conversions · curve range", body => {
    const dpi = sensCalc.dpi || S.settings?.plotDpi || 1600;
    const sens = sensCalc.gameSens;
    const game = sensCalc.game;

    rpc("calculate_cm360", { dpi: dpi, game: game, game_sens: sens, speed: speed }).then(res => {
      body.innerHTML = `
        <div class="grp">
          <div class="row">
            <label class="k" style="width:85px">Game</label>
            <select class="fi w" id="sensGame">
              <option value="source" ${game === "source" ? "selected" : ""}>CS2 / Source / Apex / Quake</option>
              <option value="valorant" ${game === "valorant" ? "selected" : ""}>Valorant</option>
              <option value="overwatch2" ${game === "overwatch2" ? "selected" : ""}>Overwatch 2</option>
              <option value="fortnite" ${game === "fortnite" ? "selected" : ""}>Fortnite</option>
              <option value="rainbow6" ${game === "rainbow6" ? "selected" : ""}>Rainbow Six Siege</option>
            </select>
          </div>
          <div class="row">
            <label class="k" style="width:85px">In-game sens</label>
            <input class="fi n" id="sensVal" type="number" step="0.01" min="0.001" value="${sens}">
            <span class="sp"></span>
            <label class="k" style="width:40px">DPI</label>
            <input class="fi n" id="sensDpi" type="number" step="50" min="100" value="${dpi}">
          </div>
          <div class="sens-grid">
            <div class="sens-card">
              <div class="k">Base 360 distance</div>
              <div class="v">${res.baseCm360} <small>cm</small></div>
              <small class="hint">${res.baseIn360} in · unaccelerated</small>
            </div>
            <div class="sens-card">
              <div class="k">At current speed (${res.evalSpeed} c/ms)</div>
              <div class="v">${res.currentCm360} <small>cm</small></div>
              <small class="hint">${res.currentMultiplier}× multiplier</small>
            </div>
            <div class="sens-card">
              <div class="k">Fastest flick (max speed)</div>
              <div class="v">${res.fastestFlickCm360} <small>cm</small></div>
              <small class="hint">at upper limit / cap</small>
            </div>
            <div class="sens-card">
              <div class="k">Slowest tracking (near 0)</div>
              <div class="v">${res.slowestTrackingCm360} <small>cm</small></div>
              <small class="hint">micro-adjustment floor</small>
            </div>
          </div>
        </div>
        <div class="grp">
          <span class="k">Equivalent sensitivities in other games</span>
          <table class="sens-table">
            <thead>
              <tr><th>Game</th><th class="num">Yaw</th><th class="num">Sens at ${dpi} DPI</th></tr>
            </thead>
            <tbody>
              <tr><td>CS2 / Source / Apex Legends</td><td class="num">0.0220</td><td class="num"><b>${res.equivalents.source_cs2_apex}</b></td></tr>
              <tr><td>Valorant</td><td class="num">0.0700</td><td class="num"><b>${res.equivalents.valorant}</b></td></tr>
              <tr><td>Overwatch 2</td><td class="num">0.0066</td><td class="num"><b>${res.equivalents.overwatch2}</b></td></tr>
              <tr><td>Fortnite</td><td class="num">0.0055</td><td class="num"><b>${res.equivalents.fortnite}</b></td></tr>
              <tr><td>Rainbow Six Siege</td><td class="num">0.0057</td><td class="num"><b>${res.equivalents.rainbow6}</b></td></tr>
            </tbody>
          </table>
        </div>
      `;

      const gEl = body.querySelector("#sensGame");
      const vEl = body.querySelector("#sensVal");
      const dEl = body.querySelector("#sensDpi");

      const recalc = () => {
        sensCalc.game = gEl.value;
        sensCalc.gameSens = parseFloat(vEl.value) || 1.0;
        sensCalc.dpi = parseInt(dEl.value, 10) || dpi;
        updateCm360Readout();
        refreshSheet();
      };
      gEl.onchange = recalc;
      vEl.onchange = recalc;
      dEl.onchange = recalc;
    }).catch(fail);
  });
}

// profiles
$("profBtn").onclick = () => openSheet($("profBtn"), "Profiles", "the first one is the default for every device", body => {
  const cur = S.profile.name, inDrv = new Set(S.driverProfiles);
  body.innerHTML = `<div class="grp">
      <div class="plist" role="radiogroup">${S.profiles.map((n, i) =>
        `<button role="radio" aria-checked="${i === S.sel}" data-i="${i}"><i>${String(i + 1).padStart(2, "0")}</i>${esc(n)}<span class="x">${i === 0 ? "default · " : ""}${inDrv.has(n) ? "in driver" : "not written"}</span></button>`).join("")}</div>
      <div class="row"><input class="fi w" id="pfName" maxlength="200" spellcheck="false" value="${esc(cur)}" aria-label="Profile name"><button class="bt" data-a="rename">Rename</button></div>
      <div class="row"><button class="bt" data-a="new">New</button><button class="bt" data-a="dup">Duplicate</button><span class="sp"></span><button class="bt q" data-a="del" ${S.sel === 0 ? "disabled" : ""}>Delete</button></div>
      <div class="row" style="margin-top:6px"><button class="bt q" data-a="share">Copy share code</button><button class="bt q" data-a="import">Import share code</button></div>
    </div>
    <div class="grp"><p>Each profile is a full set of curve and output settings. Assign a profile to a mouse under Device. Changes here reach the driver when you write.</p></div>`;
  const unique = base => { let n = base, i = 2; const has = x => S.profiles.some(p => p.toLowerCase() === x.toLowerCase()); while (has(n)) n = `${base} ${i++}`; return n; };
  body.querySelectorAll(".plist button").forEach(b => b.onclick = () => call("select_profile", { profile: +b.dataset.i }));
  body.querySelector('[data-a="rename"]').onclick = () => call("rename_profile", { name: $("pfName").value.trim() });
  $("pfName").onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); $("pfName").blur(); call("rename_profile", { name: $("pfName").value.trim() }); } };
  body.querySelector('[data-a="new"]').onclick = () => call("create_profile", { name: unique("profile") });
  body.querySelector('[data-a="dup"]').onclick = () => call("create_profile", { name: unique(cur + " copy"), copy_from: S.sel });
  body.querySelector('[data-a="del"]').onclick = () => call("delete_profile", { profile: S.sel });
  body.querySelector('[data-a="share"]').onclick = () => {
    rpc("export_profile_code", { profile: S.sel }).then(res => {
      if (res && res.code) {
        if (navigator.clipboard) {
          navigator.clipboard.writeText(res.code).then(() => notice("Share code copied to clipboard", false)).catch(() => prompt("Profile share code:", res.code));
        } else prompt("Profile share code:", res.code);
      }
    }).catch(fail);
  };
  body.querySelector('[data-a="import"]').onclick = () => {
    const code = prompt("Paste profile share code (RA1:...):");
    if (code && code.trim()) call("import_profile_code", { code: code.trim() });
  };
});

// devices
$("devBtn").onclick = () => openSheet($("devBtn"), "Devices", "plot scale and per-mouse settings", body => {
  const d = S.devices, def = d.default;
  const cfg = (c, pre) => `<label class="row" style="gap:6px"><small>DPI</small><input class="fi n" data-k="${pre}dpi" inputmode="numeric" value="${c.dpi || 0}"></label>
      <label class="row" style="gap:6px"><small>Hz</small><input class="fi n" data-k="${pre}pollingRate" inputmode="numeric" value="${c.pollingRate || 0}"></label>
      <label class="ck"><input type="checkbox" data-k="${pre}disable" ${c.disable ? "checked" : ""}>leave alone</label>`;
  body.innerHTML = `<div class="grp"><span class="k">Plot scale</span>
      <div class="row"><label class="row" style="gap:6px"><small>Mouse DPI</small><input class="fi n" id="plDpi" inputmode="numeric" value="${S.settings.plotDpi}"></label>
        <label class="row" style="gap:6px"><small>Polling Hz</small><input class="fi n" id="plHz" inputmode="numeric" value="${S.settings.plotPollRate}"></label></div>
      <p>Only sets how far the plot's speed axis reaches (DPI × 0.05 counts/ms). It does not change the driver.</p></div>
    <div class="grp"><span class="k">Every device without its own settings</span>
      <div class="row">${cfg(def, "def:")}</div>
      <p>DPI 0 keeps speeds in counts/ms. A DPI value makes the driver work in inches/s, so one curve feels the same on mice with different DPI. Hz 0 measures the polling rate automatically.</p></div>
    <div class="grp"><span class="k">Mice</span>${d.list.length ? d.list.map((m, i) => `<div class="dev ${m.connected ? "" : "gone"}">
        <div class="row"><span class="nm">${esc(m.name || "Unnamed device")}</span>${m.connected ? "" : `<span class="tag">not connected</span>`}<span class="sp"></span>
          <label class="ck"><input type="checkbox" data-own="${i}" ${m.override ? "checked" : ""}>own settings</label></div>
        <span class="id">${esc(m.id)}</span>
        ${m.override ? `<div class="row">${cfg(m, i + ":")}<span class="sp"></span><select class="fi" data-prof="${i}" aria-label="Profile">
            <option value="">default profile</option>${S.profiles.map(p => `<option ${p === m.profile ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></div>` : ""}
      </div>`).join("") : `<p>No mouse found.</p>`}</div>`;
  const plot = () => { const dpi = intOf($("plDpi")), hz = intOf($("plHz")); if (dpi == null || hz == null) return; call("set_app_settings", { plotDpi: dpi, plotPollRate: hz }); };
  $("plDpi").onchange = plot; $("plHz").onchange = plot;
  body.querySelectorAll("[data-k]").forEach(el => el.onchange = () => {
    const [who, field] = el.dataset.k.split(":");
    const val = el.type === "checkbox" ? el.checked : intOf(el);
    if (val == null) { refreshSheet(); return; }
    if (who === "def") call("set_default_device", { [field]: val });
    else call("set_device", { id: d.list[+who].id, [field]: val });
  });
  body.querySelectorAll("[data-own]").forEach(el => el.onchange = () => {
    const m = d.list[+el.dataset.own];
    if (el.checked) call("set_device", { id: m.id, name: m.name }); else call("remove_device", { id: m.id });
  });
  body.querySelectorAll("[data-prof]").forEach(el => el.onchange = () => call("set_device", { id: d.list[+el.dataset.prof].id, profile: el.value }));
});

// system
const UPDATE_TEXT = { idle: "not checked yet", checking: "checking…", current: "up to date", available: "available", downloading: "downloading…", ready: "downloaded, ready to install", error: "check failed" };
$("sysBtn").onclick = () => openSheet($("sysBtn"), "Driver & app", "start-up · MCP · updates · files", body => {
  const s = S.settings, m = S.mcp || {}, u = S.update || {}, a = S.app;
  const cmd = `claude mcp add --transport http rawaccel ${m.url || ""}`;
  body.innerHTML = `<div class="grp"><span class="k">Driver</span>
      <div class="row"><span>Raw Accel driver ${esc(a.driverVersion)}</span><span class="tag">connected</span><span class="sp"></span><button class="bt q" data-a="reset">Turn acceleration off</button></div>
      <p>Turning acceleration off resets the driver. Your settings stay here as unwritten changes, so one write brings them back.</p></div>
    <div class="grp"><span class="k">Start-up</span>
      <div class="row"><label class="ck"><input type="checkbox" data-s="startWithWindows" ${s.startWithWindows ? "checked" : ""}>Apply my settings when I sign in to Windows</label></div>
      <div class="row"><label class="ck"><input type="checkbox" data-s="applyOnStart" ${s.applyOnStart ? "checked" : ""}>Write saved settings when this app starts</label></div>
      <p>The driver forgets its settings on every restart. Sign-in apply uses Raw Accel's small writer.exe, so this window does not need to open.</p></div>
    <div class="grp"><span class="k">MCP server</span>
      <div class="row"><label class="ck"><input type="checkbox" data-s="mcpEnabled" ${s.mcpEnabled ? "checked" : ""}>Let AI tools on this PC control this window</label><span class="sp"></span>
        <span class="tag ${m.running ? "" : "p"}">${m.running ? "listening" : s.mcpEnabled ? "not running" : "off"}</span>
        <label class="row" style="gap:6px"><small>port</small><input class="fi n" id="mcpPort" inputmode="numeric" value="${s.mcpPort}"></label></div>
      ${m.error ? `<p style="color:var(--pen)">${esc(m.error)}</p>` : ""}
      <div class="row"><span class="code" id="mcpUrl">${esc(m.url || "")}</span><button class="bt q" data-copy="mcpUrl">Copy</button></div>
      <div class="row"><span class="code" id="mcpCmd">${esc(cmd)}</span><button class="bt q" data-copy="mcpCmd">Copy</button></div>
      <p><a class="lk" data-url="https://github.com/${esc(a.repo)}/blob/main/doc/MCP.md">How to connect Claude, Cursor, VS Code and other tools</a></p>
      <p>Only reachable from this computer. Edits made over MCP show up here live and reach the driver only on a write. ${m.calls ? `Last call: ${esc(m.lastTool)} at ${esc(m.lastCall)} · ${m.calls} in total.` : "No calls yet."}</p></div>
    <div class="grp"><span class="k">Updates</span>
      <div class="row"><span>Version ${esc(a.version)}${a.dev ? " (local build)" : ""}</span><span class="tag ${u.state === "ready" || u.state === "available" || u.state === "error" ? "p" : ""}">${esc(u.latest && (u.state === "available" || u.state === "ready") ? u.latest + " " : "")}${esc(UPDATE_TEXT[u.state] || "")}</span>
        <span class="sp"></span><button class="bt q" data-a="check" ${u.state === "checking" || u.state === "downloading" ? "disabled" : ""}>Check now</button>
        ${u.state === "available" || u.state === "ready" ? `<button class="bt" data-a="install">Install and restart</button>` : ""}</div>
      ${u.error ? `<p style="color:var(--pen)">${esc(u.error)}</p>` : ""}
      <div class="row"><label class="ck"><input type="checkbox" data-s="autoUpdate" ${s.autoUpdate ? "checked" : ""}>Download new versions automatically and install them when the app closes</label></div>
      <p>Releases come from <a class="lk" data-url="https://github.com/${esc(a.repo)}/releases">github.com/${esc(a.repo)}</a> and are checked against their published SHA-256 before use.${a.dev ? " A local build never replaces itself." : ""}</p></div>
    <div class="grp"><span class="k">Settings file</span>
      <div class="row"><button class="bt q" data-a="export">Export…</button><button class="bt q" data-a="import">Import…</button><small>same format as Raw Accel's settings.json</small></div></div>
    <div class="grp"><span class="k">Credits</span>
      <p>The driver, the acceleration math and settings format are <a class="lk" data-url="https://github.com/${esc(a.upstream)}">Raw Accel</a> by a1xd (simon), _m00se_, Sidiouth, TauntyArmordillo and contributors, MIT licence. This app is a new interface on top of their work and ships their signed driver unchanged.</p></div>`;
  const q = sel => body.querySelector(sel);
  q('[data-a="reset"]').onclick = () => call("reset_driver", {}, () => notice("Acceleration is off in the driver. Write to bring your settings back."));
  body.querySelectorAll("[data-s]").forEach(el => el.onchange = () => call("set_app_settings", { [el.dataset.s]: el.checked }).then(refreshSheet));
  $("mcpPort").onchange = () => { const p = intOf($("mcpPort")); if (p != null) call("set_app_settings", { mcpPort: p }); };
  body.querySelectorAll("[data-copy]").forEach(b => b.onclick = () => navigator.clipboard.writeText($(b.dataset.copy).textContent).then(() => { b.textContent = "Copied"; setTimeout(() => b.textContent = "Copy", 1200); }, () => fail(new Error("Could not copy"))));
  q('[data-a="check"]').onclick = () => call("check_for_updates", {});
  const inst = q('[data-a="install"]'); if (inst) inst.onclick = () => call("install_update", {});
  q('[data-a="export"]').onclick = () => call("ui_export_file", {}, r => { if (r && r.path) notice("Saved " + r.path); });
  q('[data-a="import"]').onclick = () => call("ui_import_file", {}, r => { if (r && !r.cancelled) notice("Imported. Nothing is written to the driver until you write."); });
  body.querySelectorAll("[data-url]").forEach(l => l.onclick = () => rpc("ui_open_url", { url: l.dataset.url }).catch(fail));
});

// ---------- themes & type ----------
const cssVar = n => getComputedStyle(root).getPropertyValue(n).trim();
const hex = s => { s = s.replace("#", ""); return [0, 2, 4].map(i => parseInt(s.slice(i, i + 2), 16)); };
function readTheme() {
  return { ink: hex(cssVar("--ink")), ink2: hex(cssVar("--ink2")), ink3: hex(cssVar("--ink3")), pen: hex(cssVar("--pen")),
           sheet: hex(cssVar("--sheet")), grid: hex(cssVar("--grid")),
           a1: [+cssVar("--grid-a1")], a2: [+cssVar("--grid-a2")], ha: [+cssVar("--hatch-a")] };
}
const themeVars = id => {
  const d = document.createElement("div"); d.dataset.theme = id; d.style.display = "none"; document.body.appendChild(d);
  const cs = getComputedStyle(d), o = { sheet: cs.getPropertyValue("--sheet").trim(), ink: cs.getPropertyValue("--ink").trim(), pen: cs.getPropertyValue("--pen").trim() };
  d.remove(); return o;
};
const swatch = id => { const v = themeVars(id); return `<span class="sw" style="background:${v.sheet};border-color:${v.ink};--c:${v.pen}"></span>`; };
let C = null, Cfrom = null, Cto = null, themeT = 1, rethemeTimer = 0;
function setTheme(id, animate, persist) {
  if (!THEMES.some(t => t.id === id)) id = "paper";
  root.dataset.theme = id; store.set("ra.theme", id);
  const to = readTheme();
  if (animate && C && !RM.matches) {
    Cfrom = JSON.parse(JSON.stringify(C)); Cto = to; themeT = 0;
    root.classList.add("retheme"); clearTimeout(rethemeTimer); rethemeTimer = setTimeout(() => root.classList.remove("retheme"), 420);
  } else { C = to; themeT = 1; }
  $("themeV").innerHTML = swatch(id) + THEMES.find(t => t.id === id).name;
  if (persist) rpc("set_app_settings", { theme: id }).catch(fail);
}
let UIF = "Satoshi, sans-serif";
function setFont(id, persist) {
  if (!FONTS.some(f => f.id === id)) id = "satoshi";
  root.dataset.font = id; store.set("ra.font", id);
  UIF = cssVar("--ui");
  const f = FONTS.find(f => f.id === id); $("fontV").textContent = f.short || f.name;
  const after = () => { fitTiles(); placeSel(); };
  if (S) after();
  if (document.fonts) document.fonts.load(`700 12px "${f.family}"`).then(() => S && after(), () => {});
  if (persist) rpc("set_app_settings", { font: id }).catch(fail);
}
themeBtn.onclick = () => openMenu(themeBtn, "Sheet", THEMES.map(t => ({ id: t.id, html: `${swatch(t.id)}${t.name}<span class="x">${t.kind}</span>` })), root.dataset.theme, id => setTheme(id, true, true));
fontBtn.onclick = () => openMenu(fontBtn, "Type", FONTS.map(f => ({ id: f.id, style: `font-family:'${f.family}', Arial, sans-serif`, html: `${f.name}<span class="x">${f.lic}</span>` })), root.dataset.font, id => setFont(id, true));

addEventListener("keydown", e => {
  if (e.key === "Escape") { if (popOwner) { closePop(true); return; } hideTip(); }
  if (e.key === "Enter" && e.ctrlKey) { e.preventDefault(); write(); return; }
  const typing = e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA" || e.target.tagName === "SELECT";
  if (e.ctrlKey && !e.altKey && !typing) {
    const k = e.key.toLowerCase();
    if (k === "z" && !e.shiftKey) { e.preventDefault(); $("undoBtn").click(); return; }
    if (k === "y" || (k === "z" && e.shiftKey)) { e.preventDefault(); $("redoBtn").click(); return; }
  }
  if (typing || e.ctrlKey || e.altKey || e.metaKey) return;
  if (popOwner && popOwner !== helpBtn) return;
  if (e.key === "?") { e.preventDefault(); toggleNotes(); return; }
  const i = "12345678".indexOf(e.key);
  if (i >= 0 && e.key.length === 1 && MODES[i]) setMode(MODES[i].id);
});

// ---------- plot ----------
let speed = 0, peak = 0, peakAt = 0;
const trail = [];
const MONO = "'IBM Plex Mono', Consolas, monospace";
const col = (c, a = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
const cv = $("cv"), g = cv.getContext("2d");
let W = 0, H = 0, DPR = 1, hatch = null, hatchKey = "";
function makeHatch(color) {
  const s = Math.max(2, Math.round(7 * DPR)), c = document.createElement("canvas"); c.width = c.height = s;
  const x = c.getContext("2d"); x.strokeStyle = color; x.lineWidth = Math.max(1, DPR * .9);
  x.beginPath(); for (const o of [-s, 0, s]) { x.moveTo(o, s); x.lineTo(o + s, 0); } x.stroke();
  hatch = g.createPattern(c, "repeat"); hatch.setTransform(new DOMMatrix().scale(1 / DPR)); hatchKey = color;
}
function fit() {
  const r = cv.getBoundingClientRect(); DPR = devicePixelRatio || 1; W = r.width; H = r.height;
  cv.width = Math.round(W * DPR); cv.height = Math.round(H * DPR); g.setTransform(DPR, 0, 0, DPR, 0, 0);
  hatchKey = "";
  if (C) step(performance.now());
}
new ResizeObserver(fit).observe(cv);

const sample = (arr, x) => { const f = clamp(x / maxX * (N - 1), 0, N - 1.001), i = f | 0; return arr[i] + (arr[i + 1] - arr[i]) * (f - i); };
const crisp = v => Math.round(v) + .5;
const sIn = $("sIn"), sSens = $("sSens"), sOut = $("sOut"), sPk = $("sPk");
const setText = (el, s) => { if (el.textContent !== s) el.textContent = s; };
const NICE = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000];
const YSTEPS = [0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
const num = v => String(+v.toFixed(2));

let yMax = 3, ghostA = 0, yA = 0, prev = performance.now();

// Plot views. The host sends sensitivity; velocity and gain are derived from it:
// velocity = speed × sensitivity, gain = slope of velocity.
const VIEWS = { sens: { title: "SENSITIVITY", unit: "×", dec: 2 }, vel: { title: "OUTPUT SPEED", unit: "counts/ms", dec: 1 }, gain: { title: "GAIN", unit: "×", dec: 2 } };
let view = VIEWS[store.get("ra.view")] ? store.get("ra.view") : "sens";
const vShown = new Float32Array(N), vShownY = new Float32Array(N), vDriver = new Float32Array(N), vTarget = new Float32Array(N), vTargetY = new Float32Array(N);
function toView(src, dst) {
  const dv = maxX / (N - 1);
  if (view === "sens") { dst.set(src); return; }
  if (view === "vel") { for (let i = 0; i < N; i++) dst[i] = i * dv * src[i]; return; }
  for (let i = 0; i < N; i++) { const a = Math.max(i - 1, 0), b = Math.min(i + 1, N - 1); dst[i] = (b * src[b] - a * src[a]) / (b - a); }
}
const base = x => view === "vel" ? x : 1;      // the "no acceleration" reference in each view
function setView(v, persist) {
  view = v; store.set("ra.view", v);
  $("docT").textContent = { sens: "Sensitivity curve", vel: "Velocity curve", gain: "Gain curve" }[v];
  if (persist) rpc("set_app_settings", { plotView: v }).catch(fail);
  $("viewSeg").querySelectorAll("button").forEach(b => b.setAttribute("aria-pressed", b.dataset.v === v));
}
$("viewSeg").querySelectorAll("button").forEach(b => b.onclick = () => setView(b.dataset.v, true));
setView(view, false);
function tag(text, x, y, align) {
  g.font = `500 10.5px ${MONO}`;
  const w = Math.ceil(g.measureText(text).width) + 10, h = 17;
  const rx = align === "c" ? x - w / 2 : x - w, ry = align === "c" ? y : y - h / 2;
  g.fillStyle = col(C.ink); g.fillRect(Math.round(rx), Math.round(ry), w, h);
  g.fillStyle = col(C.sheet); g.textAlign = "left"; g.textBaseline = "middle"; g.fillText(text, Math.round(rx) + 5, Math.round(ry) + h / 2 + .5);
}

function frame(now) { step(now); requestAnimationFrame(frame); }
function step(now) {
  const dt = clamp((now - prev) / 1000, 0, 0.05); prev = now;
  const still = RM.matches;

  if (themeT < 1) {
    themeT = Math.min(1, themeT + dt / 0.35);
    const e = 1 - Math.pow(1 - themeT, 3);
    for (const k in Cto) C[k] = Cto[k].map((v, i) => Cfrom[k][i] + (v - Cfrom[k][i]) * e);
  }

  // Spring each point toward its target: this is what makes the curve morph and settle.
  for (let i = 0; i < N; i++) {
    if (still) { shown[i] = target[i]; vel[i] = 0; shownY[i] = targetY[i]; velY[i] = 0; }
    else {
      const delay = 1 + i / N * 0.6;
      vel[i] += ((target[i] - shown[i]) * 170 / delay - vel[i] * 19) * dt; shown[i] += vel[i] * dt;
      if (hasY || yA > 0.01) { velY[i] += ((targetY[i] - shownY[i]) * 170 / delay - velY[i] * 19) * dt; shownY[i] += velY[i] * dt; }
    }
  }
  toView(shown, vShown); toView(target, vTarget); toView(inDriver, vDriver);
  if (hasY || yA > 0.01) { toView(shownY, vShownY); toView(targetY, vTargetY); }
  let mx = view === "vel" ? maxX : 1.2;
  for (let i = 5; i < N; i++) {   // the first few samples can spike toward infinity (table values near zero speed); they must not set the scale
    if (vTarget[i] > mx) mx = vTarget[i];
    if (hasY && vTargetY[i] > mx) mx = vTargetY[i];
    if (ghostOn && vDriver[i] > mx) mx = vDriver[i];
  }
  const yGoal = Math.min(mx, 1e5) * (view === "vel" ? 1.08 : 1.15);
  yMax = still ? yGoal : yMax + (yGoal - yMax) * Math.min(1, dt * 5);
  ghostA = still ? (ghostOn ? 1 : 0) : ghostA + ((ghostOn ? 1 : 0) - ghostA) * Math.min(1, dt * 8);
  yA = still ? (hasY ? 1 : 0) : yA + ((hasY ? 1 : 0) - yA) * Math.min(1, dt * 8);
  if (now - lastInputAt > 80) speedT *= Math.pow(0.02, dt);
  speed += (speedT - speed) * Math.min(1, dt * 14);
  if (speed < 0.005) speed = 0;
  if (speed >= peak) { peak = speed; peakAt = now; } else if (now - peakAt > 3000) peak = Math.max(speed, peak * Math.pow(0.15, dt));

  const L = 62, R = 20, T = 32, B = 50, pw = W - L - R, ph = H - T - B;
  g.clearRect(0, 0, W, H);
  if (pw < 60 || ph < 60 || !S) return;
  const X = x => L + x / maxX * pw, Y = y => T + ph - y / yMax * ph;
  const bottom = T + ph;
  const hk = col(C.pen, C.ha[0].toFixed(3));
  if (hk !== hatchKey) makeHatch(hk);

  // Grid: minor / major hierarchy, spacing adapts to the plot size and the speed range.
  const xMaj = NICE.find(s => maxX / s <= 10) || 5000;
  const xMinor = pw / (maxX / (xMaj / 5)) >= 7 ? xMaj / 5 : xMaj / 2;
  const yMaj = YSTEPS.find(s => ph / (yMax / s) >= 40) || 20000;
  const yMin = ph / (yMax / yMaj) / 5 >= 7 ? yMaj / 5 : yMaj / 2;
  const isMaj = (v, s) => Math.abs(v / s - Math.round(v / s)) < 1e-6;
  g.lineWidth = 1;
  g.strokeStyle = col(C.grid, C.a1[0]); g.beginPath();
  for (let k = 1; k * xMinor < maxX - 1e-9; k++) { const x = k * xMinor; if (isMaj(x, xMaj)) continue; const p = crisp(X(x)); g.moveTo(p, T); g.lineTo(p, bottom); }
  for (let k = 1; k * yMin <= yMax; k++) { const y = k * yMin; if (isMaj(y, yMaj)) continue; const p = crisp(Y(y)); g.moveTo(L, p); g.lineTo(L + pw, p); }
  g.stroke();
  g.strokeStyle = col(C.grid, C.a2[0]); g.beginPath();
  for (let k = 1; k * xMaj < maxX - 1e-9; k++) { const p = crisp(X(k * xMaj)); g.moveTo(p, T); g.lineTo(p, bottom); }
  for (let k = 1; k * yMaj <= yMax; k++) { const p = crisp(Y(k * yMaj)); g.moveTo(L, p); g.lineTo(L + pw, p); }
  g.stroke();
  g.strokeStyle = col(C.ink, .3); g.strokeRect(crisp(L), crisp(T), Math.round(pw), Math.round(ph));

  // Curve region (clipped to plot).
  g.save(); g.beginPath(); g.rect(L, T, pw, ph); g.clip();

  // Speed histogram under the curve
  if (showHistogram && S && S.histogram && S.histogram.bins && S.histogram.totalSamples > 0) {
    const bins = S.histogram.bins;
    const binW = pw / bins.length;
    let maxPct = 0;
    for (let i = 0; i < bins.length; i++) if (bins[i].percent > maxPct) maxPct = bins[i].percent;
    if (maxPct > 0) {
      g.fillStyle = col(C.pen, 0.25);
      for (let i = 0; i < bins.length; i++) {
        const b = bins[i];
        if (b.percent > 0) {
          const bh = (b.percent / maxPct) * ph * 0.42;
          const bx = L + i * binW;
          g.fillRect(bx, bottom - bh, Math.max(1, binW - 0.5), bh);
        }
      }
    }
  }

  // Pinned reference curve
  if (S && S.pinnedCurve && S.pinnedCurve.x) {
    const pts = S.pinnedCurve.x;
    const vRef = new Float32Array(pts.length);
    toView(pts, vRef);
    g.save();
    g.setLineDash([4, 4]);
    g.lineWidth = 1.6;
    g.strokeStyle = col(C.pen, 0.85);
    g.beginPath();
    for (let i = 0; i < pts.length; i++) {
      const rx = X(i / (pts.length - 1) * maxX), ry = Y(vRef[i]);
      i ? g.lineTo(rx, ry) : g.moveTo(rx, ry);
    }
    g.stroke();
    g.restore();
  }

  const path = arr => { g.beginPath(); for (let i = 0; i < N; i++) { const x = X(i / (N - 1) * maxX), y = Y(arr[i]); i ? g.lineTo(x, y) : g.moveTo(x, y); } };
  path(vShown); g.lineTo(X(maxX), Y(base(maxX))); g.lineTo(X(0), Y(base(0))); g.closePath();
  g.fillStyle = hatch; g.fill();
  g.setLineDash([4, 4]); g.strokeStyle = col(C.ink, .55); g.beginPath();
  if (view === "vel") { g.moveTo(X(0), Y(0)); g.lineTo(X(maxX), Y(maxX)); } else { g.moveTo(L, crisp(Y(1))); g.lineTo(L + pw, crisp(Y(1))); }
  g.stroke(); g.setLineDash([]);
  if (ghostA > 0.01) {
    g.globalAlpha = ghostA; g.setLineDash([6, 4]); g.lineWidth = 1.5; g.strokeStyle = col(C.ink2); path(vDriver); g.stroke(); g.setLineDash([]); g.globalAlpha = 1;
  }
  g.lineJoin = "round"; g.lineCap = "round";
  if (yA > 0.01) { g.globalAlpha = yA; g.lineWidth = 1.75; g.strokeStyle = col(C.ink); path(vShownY); g.stroke(); g.globalAlpha = 1; }
  g.lineWidth = 2.25; g.strokeStyle = col(C.pen); path(vShown); g.stroke();

  // Interactive LUT points
  if (S && S.profile && S.profile[axis] && S.profile[axis].mode === "lut" && S.profile[axis].lut) {
    const pts = S.profile[axis].lut;
    g.save();
    for (let i = 0; i < pts.length; i++) {
      const px = X(pts[i][0]), py = Y(view === "vel" ? pts[i][0] * pts[i][1] : pts[i][1]);
      g.fillStyle = i === lutDragIdx ? col(C.pen) : col(C.ink);
      g.strokeStyle = col(C.sheet);
      g.lineWidth = 2;
      g.beginPath();
      g.arc(px, py, i === lutDragIdx ? 6 : 4.5, 0, Math.PI * 2);
      g.fill();
      g.stroke();
    }
    g.restore();
  }
  // write confirmation: the pen re-traces the curve left to right
  if (sweepT >= 0) {
    sweepT += dt;
    const prog = Math.min(1, sweepT / 0.55), a = sweepT < 0.55 ? 1 : Math.max(0, 1 - (sweepT - 0.55) / 0.45);
    const ie = Math.max(1, Math.round(prog * (N - 1)));
    g.globalAlpha = a; g.lineWidth = 3.5; g.strokeStyle = col(C.ink); g.beginPath();
    for (let i = 0; i <= ie; i++) { const x = X(i / (N - 1) * maxX), y = Y(vShown[i]); i ? g.lineTo(x, y) : g.moveTo(x, y); }
    g.stroke();
    if (prog < 1) { g.lineWidth = 1; g.strokeStyle = col(C.ink, .35); const sx = crisp(X(prog * maxX)); g.beginPath(); g.moveTo(sx, T); g.lineTo(sx, bottom); g.stroke(); }
    g.globalAlpha = 1;
    if (a <= 0) sweepT = -1;
  }
  g.restore();

  // Axes, ticks, labels.
  g.lineWidth = 1; g.strokeStyle = col(C.ink); g.beginPath();
  g.moveTo(crisp(L), T); g.lineTo(crisp(L), crisp(bottom)); g.lineTo(L + pw, crisp(bottom));
  for (let k = 0; k * xMinor <= maxX + 1e-6; k++) { const x = k * xMinor, p = crisp(X(x)); g.moveTo(p, crisp(bottom)); g.lineTo(p, bottom + (isMaj(x, xMaj) ? 6 : 3)); }
  for (let k = 0; k * yMin <= yMax; k++) { const y = k * yMin, p = crisp(Y(y)); g.moveTo(crisp(L), p); g.lineTo(L - (isMaj(y, yMaj) ? 6 : 3), p); }
  g.stroke();
  g.font = `400 10.5px ${MONO}`; g.fillStyle = col(C.ink2); g.textBaseline = "alphabetic";
  g.textAlign = "center";
  for (let k = 0; k * xMaj <= maxX + 1e-6; k++) g.fillText(num(k * xMaj), X(k * xMaj), bottom + 18);
  g.textAlign = "right";
  for (let k = 0; k * yMaj <= yMax; k++) { const y = k * yMaj; if (Y(y) < T + 4) break; g.fillText(y.toFixed(yMaj < 0.5 ? 2 : yMaj < 1 ? 1 : yMaj >= 5 ? 0 : 1), L - 9, Y(y) + 3.5); }
  g.font = `700 10px ${UIF}`; g.fillStyle = col(C.ink); g.letterSpacing = "1.4px";
  g.textAlign = "right"; g.fillText("INPUT SPEED", L + pw - 64, bottom + 38);
  g.letterSpacing = "0px"; g.font = `400 10.5px ${MONO}`; g.fillStyle = col(C.ink3); g.textAlign = "left"; g.fillText("counts/ms", L + pw - 58, bottom + 38);
  g.save(); g.translate(16, T); g.rotate(-Math.PI / 2);
  const V = VIEWS[view];
  g.letterSpacing = "0px"; g.font = `400 10.5px ${MONO}`; g.fillStyle = col(C.ink3); g.textAlign = "right"; g.fillText(V.unit, 0, 0);
  const uw = g.measureText(V.unit).width;
  g.font = `700 10px ${UIF}`; g.fillStyle = col(C.ink); g.letterSpacing = "1.4px"; g.fillText(V.title, -(uw + 7), 0);
  g.letterSpacing = "0px";
  g.restore();

  // Live marker: persistence trail, crosshair to both axes, axis readout tags.
  const shownSpeed = Math.min(speed, maxX);
  const sens = sample(shown, shownSpeed), mark = sample(vShown, shownSpeed);
  if (speed > 0.05) trail.push([shownSpeed, mark, now]);
  while (trail.length && now - trail[0][2] > 1200) trail.shift();
  g.save(); g.beginPath(); g.rect(L, T, pw, ph); g.clip();
  for (const [sx, sy, t] of trail) { g.fillStyle = col(C.ink, (1 - (now - t) / 1200) * .45); g.fillRect(X(sx) - 1, Y(sy) - 1, 2, 2); }
  const dx = X(shownSpeed), dy = clamp(Y(mark), T, bottom);
  g.strokeStyle = col(C.ink, .6); g.lineWidth = 1; g.setLineDash([2, 3]); g.beginPath();
  g.moveTo(crisp(dx), dy); g.lineTo(crisp(dx), bottom); g.moveTo(dx, crisp(dy)); g.lineTo(L, crisp(dy)); g.stroke(); g.setLineDash([]);
  g.restore();
  if (peak > 0.5) {
    const px = X(Math.min(peak, maxX)); g.fillStyle = col(C.pen); g.beginPath(); g.moveTo(px - 4, bottom); g.lineTo(px + 4, bottom); g.lineTo(px, bottom - 7); g.closePath(); g.fill();
  }
  g.lineWidth = 1.5; g.strokeStyle = col(C.ink); g.fillStyle = col(C.sheet);
  g.beginPath(); g.arc(dx, dy, 5, 0, Math.PI * 2); g.fill(); g.stroke();
  g.fillStyle = col(C.pen); g.beginPath(); g.arc(dx, dy, 1.8, 0, Math.PI * 2); g.fill();
  g.font = `500 10.5px ${MONO}`;
  const xt = speed.toFixed(1), xw = g.measureText(xt).width + 10;
  tag(xt, clamp(dx, L + xw / 2, L + pw - xw / 2), bottom + 4, "c");
  tag(mark.toFixed(V.dec), L - 4, clamp(dy, T + 8, bottom - 8), "r");

  setText(sIn, speed.toFixed(1)); setText(sSens, sens.toFixed(2)); setText(sOut, (speed * sens).toFixed(1)); setText(sPk, peak.toFixed(1));
}

// ---------- LUT curve direct point manipulation ----------
let lutDragIdx = -1;
cv.addEventListener("pointerdown", e => {
  if (!S || !S.profile || !S.profile[axis] || S.profile[axis].mode !== "lut" || !S.profile[axis].lut) return;
  const pts = S.profile[axis].lut;
  const r = cv.getBoundingClientRect();
  const mx = e.clientX - r.left, my = e.clientY - r.top;
  const L = 62, R = 20, T = 32, B = 50, pw = W - L - R, ph = H - T - B;
  const X = x => L + x / maxX * pw, Y = y => T + ph - y / yMax * ph;

  for (let i = 0; i < pts.length; i++) {
    const px = X(pts[i][0]), py = Y(view === "vel" ? pts[i][0] * pts[i][1] : pts[i][1]);
    if (Math.hypot(mx - px, my - py) <= 12) {
      lutDragIdx = i;
      cv.setPointerCapture(e.pointerId);
      e.preventDefault();
      return;
    }
  }
});

cv.addEventListener("pointermove", e => {
  if (lutDragIdx < 0 || !S || !S.profile || !S.profile[axis] || !S.profile[axis].lut) return;
  const pts = S.profile[axis].lut;
  const r = cv.getBoundingClientRect();
  const mx = e.clientX - r.left, my = e.clientY - r.top;
  const L = 62, R = 20, T = 32, B = 50, pw = W - L - R, ph = H - T - B;
  let newSpd = clamp((mx - L) / pw * maxX, 0, maxX);
  let newVal = clamp((T + ph - my) / ph * yMax, 0, yMax * 2);
  if (view === "vel" && newSpd > 0.001) newVal = newVal / newSpd;

  const prevSpd = lutDragIdx > 0 ? pts[lutDragIdx - 1][0] + 0.01 : 0;
  const nextSpd = lutDragIdx < pts.length - 1 ? pts[lutDragIdx + 1][0] - 0.01 : maxX;
  newSpd = clamp(newSpd, prevSpd, nextSpd);

  pts[lutDragIdx][0] = Math.round(newSpd * 100) / 100;
  pts[lutDragIdx][1] = Math.round(newVal * 1000) / 1000;

  patchLocal(axis, { lut: pts });
});

const endLutDrag = e => {
  if (lutDragIdx >= 0) {
    lutDragIdx = -1;
    try { cv.releasePointerCapture(e.pointerId); } catch {}
    flushNow();
  }
};
cv.addEventListener("pointerup", endLutDrag);
cv.addEventListener("pointercancel", endLutDrag);

cv.addEventListener("dblclick", e => {
  if (!S || !S.profile || !S.profile[axis] || S.profile[axis].mode !== "lut" || !S.profile[axis].lut) return;
  const pts = S.profile[axis].lut;
  const r = cv.getBoundingClientRect();
  const mx = e.clientX - r.left, my = e.clientY - r.top;
  const L = 62, R = 20, T = 32, B = 50, pw = W - L - R, ph = H - T - B;
  const spd = Math.round(clamp((mx - L) / pw * maxX, 0, maxX) * 100) / 100;
  let val = Math.round(clamp((T + ph - my) / ph * yMax, 0, yMax * 2) * 1000) / 1000;
  if (view === "vel" && spd > 0.001) val = Math.round((val / spd) * 1000) / 1000;

  pts.push([spd, val]);
  pts.sort((a, b) => a[0] - b[0]);
  patchLocal(axis, { lut: pts });
  flushNow();
});

cv.addEventListener("contextmenu", e => {
  if (!S || !S.profile || !S.profile[axis] || S.profile[axis].mode !== "lut" || !S.profile[axis].lut) return;
  const pts = S.profile[axis].lut;
  if (pts.length <= 2) return;
  const r = cv.getBoundingClientRect();
  const mx = e.clientX - r.left, my = e.clientY - r.top;
  const L = 62, R = 20, T = 32, B = 50, pw = W - L - R, ph = H - T - B;
  const X = x => L + x / maxX * pw, Y = y => T + ph - y / yMax * ph;

  for (let i = 0; i < pts.length; i++) {
    const px = X(pts[i][0]), py = Y(view === "vel" ? pts[i][0] * pts[i][1] : pts[i][1]);
    if (Math.hypot(mx - px, my - py) <= 12) {
      e.preventDefault();
      pts.splice(i, 1);
      patchLocal(axis, { lut: pts });
      flushNow();
      return;
    }
  }
});

// ---------- start ----------
setTheme(root.dataset.theme, false, false);
setFont(root.dataset.font, false);
if (document.fonts) FONTS.forEach(f => document.fonts.load(`400 13px "${f.family}"`).catch(() => {}));
document.fonts && document.fonts.ready.then(() => { if (S) { placeSel(); fitTiles(); } });
fit();
requestAnimationFrame(frame);

if (!wv) $("boot").textContent = "This is the interface of Better Raw Accel. Start BetterRawAccel.exe to use it.";
else rpc("ui_state").then(st => {
  applyState(st);
  shown.set(target); if (hasY) shownY.set(targetY);     // first frame is drawn in place, not morphed from flat
  $("boot").classList.add("off");
  requestAnimationFrame(() => { placeSel(); fitTiles(); });
}).catch(e => { $("boot").textContent = "Could not start: " + e.message; });
