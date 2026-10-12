// Listwise scorer: per scene, a softmax over the candidates plus a "no sheet" option; trained so the candidate with the best IoU
// gets the probability mass (or "no sheet" for a scene without one). Emits raw weights for the detector.
//   node learn2.js cands.json [--l2 0.01] [--cv] [--emit model.json] [--tt 0.02] [--nw 3] [--drop feat,feat]
const fs = require('fs');
const args = process.argv.slice(2); const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };
const file = args[0], L2 = +opt('l2', 0.01), TT = +opt('tt', 0.02), NW = +opt('nw', 3), EP = +opt('ep', 400);
const drop = (opt('drop', '') || '').split(',').filter(Boolean);
// several dumps (comma separated) are merged scene by scene; identical candidates are kept once
// groups separated by '+' are independent sets of scenes (e.g. different exposure conditions); inside a group files are merged scene by scene
function loadRows(spec) { return spec.split('+').flatMap(loadGroup); }
function loadGroup(files) {
  const by = new Map();
  for (const fl of files.split(',')) for (const r of JSON.parse(fs.readFileSync(fl, 'utf8'))) {
    const key = r.cat + '/' + r.seed, cur = by.get(key);
    if (!cur) { by.set(key, { cat: r.cat, seed: r.seed, hasSheet: r.hasSheet, cands: r.cands.slice(), seen: new Set(r.cands.map(k => JSON.stringify(k.f))) }); continue; }
    for (const k of r.cands) { const sig = JSON.stringify(k.f); if (!cur.seen.has(sig)) { cur.seen.add(sig); cur.cands.push(k); } }
  }
  return [...by.values()];
}
const rows = loadRows(file);
const sat = x => x < 0 ? 0 : x > 1 ? 1 : x;

const ALL = {
  tMean: f => f.tMean || 0, tMin: f => f.tMin || 0, mMean: f => f.mMean, mMin: f => f.mMin, bMean: f => f.bMean, bMin: f => f.bMin, dark: f => f.dark / 4, vis: f => f.vis,
  dark1: f => f.dark === 1 ? 1 : 0, dark2: f => f.dark === 2 ? 1 : 0, dark3: f => f.dark >= 3 ? 1 : 0,
  area: f => Math.sqrt(Math.min(1, f.area)), areaSmall: f => f.area < 0.1 ? 1 - f.area / 0.1 : 0,
  rect: f => f.rect, ratioHi: f => Math.max(0, Math.log(f.ratio / 2.1)), ratioLo: f => f.ratio < 1.12 ? 1 : 0,
  white: f => sat((f.white - 60) / 120), calm: f => f.calm, plain: f => 1 - sat(f.plain / 80), busy: f => Math.min(1, f.busy * 4),
  ring: f => Math.max(-1, Math.min(1, f.ring / 40)), ringNeg: f => f.ring < 0 ? 1 : 0, ringM: f => Math.max(-1, Math.min(1, f.ringM / 60)),
  rank: f => f.rank || 0, ringS: f => Math.max(-1, Math.min(1, (f.ringS || 0) / 60)),
  off: f => Math.min(f.off, 0.35) / 0.35,
  grid: f => Math.min(f.grid || 0, 6) / 6, perSides: f => f.perSides || 0, perMean: f => f.perMean || 0, nFrame: f => (f.nFrame || 0) / 2, frameCont: f => f.frameCont || 0, frameWeak: f => f.frameWeak || 0
};
const names = Object.keys(ALL).filter(n => !drop.includes(n));
const feat = f => names.map(n => ALL[n](f));

// training scenes: features per candidate and the target distribution
function prepare(sub) {
  const sc = [];
  for (const r of sub) {
    if (!r.cands.length) continue;
    const X = r.cands.map(k => feat(k.f));
    let t, nullT = 0;
    if (!r.hasSheet) { t = r.cands.map(() => 0); nullT = 1; }
    else {
      const best = Math.max(...r.cands.map(k => k.iou));
      if (best < 0.8) continue; // the generator did not make a usable candidate: nothing to learn from
      t = r.cands.map(k => Math.exp((k.iou - best) / TT) * (k.iou >= 0.5 ? 1 : 0)); const s = t.reduce((a, b) => a + b, 0); t = t.map(v => v / s);
    }
    sc.push({ X, t, nullT, weight: r.hasSheet ? 1 : NW });
  }
  return sc;
}

function train(sc) {
  const d = names.length, n = sc.reduce((a, s) => a + s.X.length, 0);
  const mu = new Array(d).fill(0), sd = new Array(d).fill(0);
  sc.forEach(s => s.X.forEach(x => x.forEach((v, j) => mu[j] += v / n)));
  sc.forEach(s => s.X.forEach(x => x.forEach((v, j) => sd[j] += (v - mu[j]) ** 2 / n)));
  for (let j = 0; j < d; j++) sd[j] = Math.sqrt(sd[j]) || 1;
  const Z = sc.map(s => s.X.map(x => x.map((v, j) => (v - mu[j]) / sd[j])));
  const w = new Array(d).fill(0); let b = 0; const m = new Array(d + 1).fill(0), v = new Array(d + 1).fill(0);
  for (let ep = 1; ep <= EP; ep++) {
    const g = new Array(d + 1).fill(0); let loss = 0;
    sc.forEach((s, si) => {
      const zs = Z[si].map(x => { let z = b; for (let j = 0; j < d; j++) z += w[j] * x[j]; return z; });
      const mx = Math.max(0, ...zs); let den = Math.exp(-mx); zs.forEach(z => den += Math.exp(z - mx));
      const p = zs.map(z => Math.exp(z - mx) / den), pn = Math.exp(-mx) / den;
      s.t.forEach((t, i) => { if (t > 0) loss -= s.weight * t * Math.log(p[i] + 1e-12); });
      if (s.nullT) loss -= s.weight * s.nullT * Math.log(pn + 1e-12);
      zs.forEach((z, i) => { const e = (p[i] - s.t[i]) * s.weight; for (let j = 0; j < d; j++) g[j] += e * Z[si][i][j]; g[d] += e; });
    });
    for (let j = 0; j < d; j++) g[j] = g[j] / sc.length + L2 * w[j]; g[d] /= sc.length;
    for (let j = 0; j <= d; j++) { m[j] = 0.9 * m[j] + 0.1 * g[j]; v[j] = 0.999 * v[j] + 0.001 * g[j] * g[j]; const mh = m[j] / (1 - 0.9 ** ep), vh = v[j] / (1 - 0.999 ** ep); const st = 0.05 * mh / (Math.sqrt(vh) + 1e-8); if (j < d) w[j] -= st; else b -= st; }
  }
  const wr = w.map((x, j) => x / sd[j]); let br = b; for (let j = 0; j < d; j++) br -= w[j] * mu[j] / sd[j];
  return { names, w: wr, b: br };
}

const zOf = (M, f) => { const x = feat(f); let z = M.b; for (let j = 0; j < x.length; j++) z += M.w[j] * x[j]; return z; };
function evaluate(M, sub, thr) {
  const per = {};
  for (const r of sub) {
    const c = per[r.cat] = per[r.cat] || { n: 0, good: 0, ok: 0, wrong: 0, miss: 0, fp: 0, iou: 0 };
    c.n++;
    let best = null, bz = -1e9; r.cands.forEach(k => { const z = zOf(M, k.f); if (z > bz) { bz = z; best = k; } });
    const found = best && bz > thr;
    if (!r.hasSheet) { if (found) c.fp++; continue; }
    if (!found) { c.miss++; continue; }
    c.iou += best.iou; if (best.iou >= 0.93) c.good++; if (best.iou >= 0.85) c.ok++; else c.wrong++;
  }
  return per;
}
function report(per, label, detail) {
  const tot = { n: 0, good: 0, ok: 0, wrong: 0, miss: 0, iou: 0 }; let fp = 0, nn = 0; const lines = [];
  for (const [k, c] of Object.entries(per)) {
    if (k === 'nosheet') { fp += c.fp; nn += c.n; lines.push(k.padEnd(10) + ' FP ' + c.fp + '/' + c.n); continue; }
    for (const q of ['n', 'good', 'ok', 'wrong', 'miss', 'iou']) tot[q] += c[q];
    lines.push(k.padEnd(10) + ' good ' + (100 * c.good / c.n).toFixed(0).padStart(3) + '% ok ' + (100 * c.ok / c.n).toFixed(0).padStart(3) + '% wrong ' + (100 * c.wrong / c.n).toFixed(0).padStart(3) + '% miss ' + (100 * c.miss / c.n).toFixed(0).padStart(3) + '% iou ' + (c.iou / c.n).toFixed(2));
  }
  console.log(label + ': good ' + (100 * tot.good / tot.n).toFixed(1) + '% ok ' + (100 * tot.ok / tot.n).toFixed(1) + '% wrong ' + (100 * tot.wrong / tot.n).toFixed(1) + '% miss ' + (100 * tot.miss / tot.n).toFixed(1) + '% iou ' + (tot.iou / tot.n).toFixed(3) + ' FP ' + fp + '/' + nn);
  if (detail) console.log(lines.join('\n'));
}

const M = train(prepare(rows));
console.log('features:', names.join(','));
console.log('weights:', names.map((n, j) => n + '=' + M.w[j].toFixed(2)).join(' '), 'b=' + M.b.toFixed(2));
for (const thr of [-1, 0, 1, 2]) report(evaluate(M, rows, thr), 'threshold ' + thr, thr === +opt('detail', 0));
if (args.includes('--cv')) {
  const cats = [...new Set(rows.map(r => r.cat))], agg = {}; const thr = +opt('thr', 0);
  for (const cat of cats) { const Mc = train(prepare(rows.filter(r => r.cat !== cat))); agg[cat] = evaluate(Mc, rows.filter(r => r.cat === cat), thr)[cat]; }
  report(agg, 'leave-one-category-out (threshold ' + thr + ')', true);
}
if (opt('emit', '')) fs.writeFileSync(opt('emit'), JSON.stringify(M));
if (opt('test', '')) { // evaluate this model on another candidate dump
  const T = JSON.parse(fs.readFileSync(opt('test'), 'utf8'));
  for (const thr of [-1, 0, 1, 2]) report(evaluate(M, T, thr), 'holdout threshold ' + thr, thr === +opt('detail', 0));
}
