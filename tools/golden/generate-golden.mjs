// Pulls the formatting/sorting logic straight out of the handoff's own source
// and evaluates it, so the C# test expectations are derived from the design
// rather than from a hand transcription of it.
const fs = require('fs');
const dir = process.argv[2];
const jsx = fs.readFileSync(dir + '/mini-ios2.jsx', 'utf8');
const app = fs.readFileSync(dir + '/mini-ios2-app.jsx', 'utf8');

function grab(src, pattern, label) {
  const m = src.match(pattern);
  if (!m) throw new Error('could not extract ' + label);
  return m[0];
}

// single-line consts
const mbSrc  = grab(jsx, /^const mb = .*$/m, 'mb');
const spdSrc = grab(jsx, /^const spd = .*$/m, 'spd');
const etaSrc = grab(jsx, /^const eta = .*$/m, 'eta');
const stateSrc = grab(jsx, /^const STATE_CN = .*$/m, 'STATE_CN');
// multi-line: T(...) factory and the SEED array
const tSrc = grab(jsx, /^const T = \([\s\S]*?^\}\);$/m, 'T');
const seedSrc = grab(jsx, /^const SEED = \[[\s\S]*?^\];$/m, 'SEED');
// the sort map lives in App2
const sortSrc = grab(app, /const SORT = \{[\s\S]*?\};/, 'SORT');

// `const` inside a direct eval stays in the eval's own scope, so build a
// function whose body is the extracted source and have it hand the bindings back.
const { mb, spd, eta, STATE_CN, SEED, SORT } = new Function(
  [mbSrc, spdSrc, etaSrc, stateSrc, tSrc, seedSrc, sortSrc,
   'return { mb, spd, eta, STATE_CN, SEED, SORT };'].join('\n'))();

const MB = 1024 * 1024, KB = 1024;
const out = {};

// ── mb(): sizes. Values are MB in the prototype, bytes in the app. ─────────
out.bytes = [0, 1, 14, 62, 86, 412, 740, 1023, 1024, 1180, 3210, 5940, 6042, 10240]
  .map(v => ({ mb: v, bytes: Math.round(v * MB), expected: mb(v) }));

// ── spd(): speeds. KB/s in the prototype, bytes/s in the app. ─────────────
out.speeds = [0, 1, 60, 214, 500, 742, 1023, 1024, 1180, 2048, 4096]
  .map(k => ({ kbs: k, bytesPerSecond: Math.round(k * KB), expected: spd(k) }));

// ── eta(): seconds remaining ──────────────────────────────────────────────
out.etas = [0, 1, 30, 59, 60, 61, 90, 599, 3600, 5940, 5941, 99999]
  .map(t => ({ seconds: t, expected: eta(t) }));

// eta as the row computes it: (size - got) * 1024 / kbs, in prototype units
out.etaFromTask = SEED.map(t => ({
  id: t.id,
  remainingBytes: Math.round((t.size - t.got) * MB),
  bytesPerSecond: Math.round(t.kbs * KB),
  expected: eta((t.size - t.got) * 1024 / (t.kbs || 1)),
}));

out.stateNames = Object.entries(STATE_CN).map(([k, v]) => ({ state: k, expected: v }));

// ── Row(): the sub line and the trailing readout, per state ───────────────
// Transcribed from Row() in mini-ios2.jsx; kept next to the source it mirrors.
const sub = (t) => {
  const done = t.state === 'done';
  return done ? `${mb(t.size)}${t.checksum ? ' · ' + t.checksum : ''}`
    : t.state === 'error' ? t.err + ` · 已重试 ${t.retries} 次`
    : t.state === 'queued' ? '排队中，等待空闲通道'
    : t.state === 'paused' ? `已暂停 · ${mb(t.got)} / ${mb(t.size)}`
    : `${spd(t.kbs)} · ${eta((t.size - t.got) * 1024 / (t.kbs || 1))}`;
};
const trailing = (t) => {
  const pct = Math.min(100, (t.got / t.size) * 100);
  return t.state === 'done' ? '完成'
    : t.state === 'error' ? '失败'
    : t.state === 'queued' ? '—'
    : `${pct.toFixed(0)}%`;
};
out.rows = SEED.map(t => ({
  id: t.id, name: t.name, state: t.state,
  sizeBytes: Math.round(t.size * MB), doneBytes: Math.round(t.got * MB),
  speedBytesPerSecond: Math.round(t.kbs * KB),
  checksum: t.checksum, err: t.err, retries: t.retries,
  subText: sub(t), trailingText: trailing(t),
  percent: Math.min(100, (t.got / t.size) * 100),
}));

// ── ring caption: `${mb(got)} / ${mb(size)} · ${done ? '已完成' : eta(...)}` ─
out.ringCaptions = SEED.map(t => ({
  id: t.id,
  expected: `${mb(t.got)} / ${mb(t.size)} · ` +
    (t.state === 'done' ? '已完成' : eta((t.size - t.got) * 1024 / (t.kbs || 1))),
}));

// ── sorting: App2's shown[] pipeline, per key and direction ───────────────
const cats = ['all', 'soft', 'video', 'doc', 'music', 'bt'];
const tabs = ['all', 'active', 'done'];
out.sorting = [];
for (const key of Object.keys(SORT)) {
  for (const dir of ['asc', 'desc']) {
    const shown = SEED.slice().sort((a, b) => (dir === 'asc' ? 1 : -1) * SORT[key](a, b));
    out.sorting.push({ sortKey: key, sortDirection: dir, ids: shown.map(t => t.id) });
  }
}
out.filtering = [];
for (const tab of tabs) {
  for (const cat of cats) {
    const scoped = SEED.filter(t => cat === 'all' || t.cat === cat);
    const active = scoped.filter(t => t.state !== 'done');
    const done = scoped.filter(t => t.state === 'done');
    const shown = (tab === 'active' ? active : tab === 'done' ? done : scoped)
      .slice().sort((a, b) => SORT.added(a, b));
    out.filtering.push({ tab, category: cat, activeCount: active.length, doneCount: done.length, ids: shown.map(t => t.id) });
  }
}
out.searching = ['', 'ubuntu', 'PDF', 'zip', 'nothing-matches'].map(q => ({
  query: q,
  ids: SEED.filter(t => !q || t.name.toLowerCase().includes(q.toLowerCase()))
           .slice().sort((a, b) => SORT.added(a, b)).map(t => t.id),
}));

// ── the five-row fold ─────────────────────────────────────────────────────
const FOLD = 5;
out.folding = [0, 1, 5, 6, 8, 12].flatMap(total => [true, false].map(open => {
  const list = Array.from({ length: total }, (_, i) => i);
  const vis = open ? list : list.slice(0, FOLD);
  return { total, expanded: open, shown: vis.length, hidden: list.length - vis.length, canFold: total > FOLD };
}));

// ── magnetic docking: nearest() and the dock positions ────────────────────
const MW = 536, MH = 680, SNAP = 18;
const dockPos = (d, m) => ({
  right: { x: m.x + MW, y: m.y }, left: { x: m.x - MW, y: m.y },
  bottom: { x: m.x, y: m.y + MH }, top: { x: m.x, y: m.y - MH },
}[d]);
const nearest = (sp, m) => {
  let best = null;
  for (const d of ['right', 'left', 'bottom', 'top']) {
    const p = dockPos(d, m);
    const dx = Math.abs(sp.x - p.x), dy = Math.abs(sp.y - p.y);
    if (dx <= SNAP && dy <= SNAP && (!best || dx + dy < best.dist)) best = { d, dist: dx + dy };
  }
  return best ? best.d : null;
};
const main = { x: 100, y: 200 };
out.dockPositions = ['right', 'left', 'bottom', 'top'].map(d => ({ side: d, ...dockPos(d, main) }));
out.nearest = [
  [0, 0], [18, 0], [19, 0], [0, 18], [0, 19], [18, 18], [10, 10], [-18, -18], [-19, 0], [40, 0],
].map(([dx, dy]) => {
  const cases = {};
  for (const d of ['right', 'left', 'bottom', 'top']) {
    const p = dockPos(d, main);
    cases[d] = nearest({ x: p.x + dx, y: p.y + dy }, main);
  }
  return { offsetX: dx, offsetY: dy, fromRight: cases.right, fromLeft: cases.left, fromBottom: cases.bottom, fromTop: cases.top };
});

// ── the tick's growth curve, at the extremes of the random factor ─────────
out.speedCurve = [[300, 0], [300, 1], [60, 0], [1000, 0.5]].map(([kbs, r]) => ({
  speedBytesPerSecond: Math.round(kbs * KB),
  random: r,
  expected: Math.max(60, kbs * (0.93 + r * 0.15)) * KB,
}));

// ── the seed itself, in the units the app stores ─────────────────────────
const STATE = { run: 'Downloading', paused: 'Paused', done: 'Completed', error: 'Error', queued: 'Queued' };
const KIND = { disc: 'Disc', film: 'Film', zip: 'Zip', music: 'Music', doc: 'Doc' };
out.seed = SEED.map(t => ({
  id: t.id, name: t.name, host: t.host, kind: KIND[t.kind], category: t.cat, tint: t.tint,
  sizeBytes: Math.round(t.size * MB), doneBytes: Math.round(t.got * MB),
  speedBytesPerSecond: Math.round(t.kbs * KB), status: STATE[t.state], connections: t.conns,
  checksum: t.checksum, error: t.err, retries: t.retries,
  priority: t.priority === 'high' ? 'High' : t.priority === 'low' ? 'Low' : 'Normal',
  peers: t.peers ?? null, seeds: t.seeds ?? null, ratio: t.ratio ?? null,
  uploadBytesPerSecond: Math.round((t.up || 0) * KB),
  newerVersion: t.newer ? { version: t.newer.ver, sizeBytes: Math.round(t.newer.size * MB), published: t.newer.date } : null,
}));

// ── mid-point rounding, where JS and .NET disagree by default ─────────────
out.midpoints = {
  bytes: [0.5, 1.5, 2.5, 62.5, 1023.5].map(v => ({ mb: v, bytes: Math.round(v * MB), expected: mb(v) })),
  speeds: [0.5, 1.5, 2.5, 1023.5].map(k => ({ kbs: k, bytesPerSecond: Math.round(k * KB), expected: spd(k) })),
  percents: [0.5, 1.5, 2.5, 49.5, 99.5].map(p => ({ percent: p, expected: p.toFixed(0) })),
};

// ── cubic-bezier(.32,.72,0,1), solved by bisection rather than Newton, so the
//    C# implementation is checked against an independent method ─────────────
const bez = (u, p1, p2) => (((1 - 3 * p2 + 3 * p1) * u + (3 * p2 - 6 * p1)) * u + 3 * p1) * u;
const easeBisect = (t) => {
  if (t <= 0) return 0;
  if (t >= 1) return 1;
  let lo = 0, hi = 1;
  for (let i = 0; i < 80; i++) {
    const mid = (lo + hi) / 2;
    if (bez(mid, 0.32, 0.0) < t) lo = mid; else hi = mid;
  }
  return bez((lo + hi) / 2, 0.72, 1.0);
};
out.easing = [0, 0.05, 0.1, 0.25, 0.4, 0.5, 0.6, 0.75, 0.9, 0.95, 1]
  .map(t => ({ t, expected: easeBisect(t) }));

console.log(JSON.stringify(out, null, 2));
