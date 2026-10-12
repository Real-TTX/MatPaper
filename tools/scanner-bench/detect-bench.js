// Runs page detectors over the cached synthetic scenes and reports how well they find the sheet.
//   node detect-bench.js [--cats a,b] [--det repo,old.js] [--level 1] [--seeds 1-40] [--json out.json] [--fails dir] [--opts '{"precise":true}']
//                        [--cands dump.json] [--aug gain=0.6,contrast=0.8,tint=1.05:1:0.9,distort=0.1]   (distort: lens distortion, > 0 barrel, < 0 pincushion)
// Detectors: repo = this checkout's wwwroot/js/scanner-core.js; any other value is a path to a scanner-core.js (an older version:
//            git show <commit>:src/MatPaper/wwwroot/js/scanner-core.js > old.js).
// --cands writes the features of all candidates of every scene (for learn2.js); --fails writes sheets of the scenes that went wrong.
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start } = require('./bench-server');
const args = process.argv.slice(2); const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };
const SC = __dirname;
const index = JSON.parse(fs.readFileSync(path.join(SC, 'scenes', 'index.json'), 'utf8'));
const [s0, s1] = opt('seeds', '1-40').split('-').map(Number);
const cats = (opt('cats', '') ? opt('cats').split(',') : [...new Set(Object.keys(index).map(k => k.split('/')[0]))]);
const dets = opt('det', 'repo').split(',');
const level = +opt('level', '1');
const extra = JSON.parse(opt('opts', '{}'));
const failsDir = opt('fails', '');
const jsonOut = opt('json', '');
const candsOut = opt('cands', '');
const aug = opt('aug', ''); // e.g. gain=0.6,contrast=0.8,tint=1.05:1:0.9


(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  page.on('console', m => { const t = m.text(); if (m.type() === 'error' && !/404/.test(t)) console.log('console.error', t); else if (t.startsWith('>>')) console.log(t); });
  await page.goto(base + '/bench.html');
  await page.addScriptTag({ url: base + '/scene-lib.js' });
  // load each detector under its own name
  for (const d of dets) {
    await page.addScriptTag({ url: base + server.detectorUrl(d) });
    await page.evaluate(n => { window.DET = window.DET || {}; window.DET[n] = window.MatPaperScan; delete window.MatPaperScan; }, d);
  }
  const items = [];
  for (const cat of cats) for (let s = s0; s <= s1; s++) if (index[cat + '/' + s]) items.push({ cat, seed: s, entry: index[cat + '/' + s] });
  if (!items.length) { console.log('no scenes'); process.exit(1); }

  const out = await page.evaluate(async ({ items, dets, level, extra, base, wantFails, wantCands, aug }) => {
    const res = [];
    const loadImg = u => new Promise((ok, no) => { const im = new Image(); im.onload = () => ok(im); im.onerror = () => no(u); im.src = u; });
    const fails = {}; // cat -> list of {canvas thumb data}
    for (const it of items) {
      const im = await loadImg(base + '/scenes/' + it.cat + '/' + it.seed + '.jpg');
      const c = document.createElement('canvas'); c.width = im.naturalWidth; c.height = im.naturalHeight;
      c.getContext('2d', { willReadFrequently: true }).drawImage(im, 0, 0);
      if (aug) {
        const o = {}; aug.split(',').forEach(kv => { const [k, v] = kv.split('='); o[k] = v; });
        const gain = +(o.gain || 1), contrast = +(o.contrast || 1), tint = (o.tint || '1:1:1').split(':').map(Number);
        const cx = c.getContext('2d', { willReadFrequently: true }), id = cx.getImageData(0, 0, c.width, c.height), d = id.data;
        for (let p = 0; p < d.length; p += 4) for (let ch = 0; ch < 3; ch++) d[p + ch] = (((d[p + ch] * tint[ch]) - 128) * contrast + 128) * gain;
        cx.putImageData(id, 0, 0);
      }
      let gt = it.entry.gt;
      if (aug && /distort=/.test(aug)) {
        // lens distortion: the picture is re-sampled radially (k > 0 barrel, k < 0 pincushion); the true corners move with it
        const k = +aug.match(/distort=(-?[0-9.]+)/)[1], cw = c.width, ch = c.height, hd = Math.hypot(cw, ch) / 2, sc = 1 / (1 + Math.max(k, 0));   // barrel: look at the middle part, so no black corners
        const src = c.getContext('2d', { willReadFrequently: true }).getImageData(0, 0, cw, ch), sd = src.data, out = new ImageData(cw, ch), od = out.data;
        for (let y = 0; y < ch; y++) for (let x = 0; x < cw; x++) {
          const ux = sc * (x + 0.5 - cw / 2) / hd, uy = sc * (y + 0.5 - ch / 2) / hd, f = 1 + k * (ux * ux + uy * uy);
          const sx = ux * f * hd + cw / 2 - 0.5, sy = uy * f * hd + ch / 2 - 0.5, x0 = Math.floor(sx), y0 = Math.floor(sy), fx = sx - x0, fy = sy - y0, o = (y * cw + x) * 4;
          if (x0 < 0 || y0 < 0 || x0 >= cw - 1 || y0 >= ch - 1) { od[o] = od[o + 1] = od[o + 2] = 0; od[o + 3] = 255; continue; }
          const i00 = (y0 * cw + x0) * 4, i10 = i00 + 4, i01 = i00 + cw * 4, i11 = i01 + 4;
          for (let ch2 = 0; ch2 < 3; ch2++) od[o + ch2] = sd[i00 + ch2] * (1 - fx) * (1 - fy) + sd[i10 + ch2] * fx * (1 - fy) + sd[i01 + ch2] * (1 - fx) * fy + sd[i11 + ch2] * fx * fy;
          od[o + 3] = 255;
        }
        c.getContext('2d').putImageData(out, 0, 0);
        const fwd = p => { // source point -> where it appears: solve u (1 + k |u|^2) = us
          const usx = (p[0] * cw - cw / 2) / hd, usy = (p[1] * ch - ch / 2) / hd; let ux = usx, uy = usy;
          for (let it2 = 0; it2 < 30; it2++) { const f = 1 + k * (ux * ux + uy * uy); ux = usx / f; uy = usy / f; }
          return [(ux / sc * hd + cw / 2) / cw, (uy / sc * hd + ch / 2) / ch];
        };
        gt = Object.assign({}, gt, { quad: gt.quad && gt.quad.map(fwd), poly: gt.poly && gt.poly.map(fwd) });
      }
      const W = c.width, H = c.height, row = { cat: it.cat, seed: it.seed, meta: it.entry.meta, hasSheet: gt.hasSheet, det: {} };
      if (wantCands) {
        const dd = DET[dets[0]].detect(c, Object.assign({ sensitivity: level }, extra, { debug: true, debugAll: true }));
        const all = dd.candidates.map((k, i) => ({ f: k.f, score: k.score, iou: gt.hasSheet ? SceneLib.iou(k.V, gt.poly) : 0, i }));
        // keep: the best by the current score, the ones that are right (best IoU first) and a spread of the rest
        const keep = new Set(); all.slice(0, 60).forEach(k => keep.add(k.i));
        all.filter(k => k.iou >= 0.75).sort((a, b2) => b2.iou - a.iou).slice(0, 50).forEach(k => keep.add(k.i));
        let seed = 12345 + it.seed; const rnd = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296; };
        const rest = all.filter(k => !keep.has(k.i)); for (let q = 0; q < 70 && rest.length; q++) { const j = Math.floor(rnd() * rest.length); keep.add(rest[j].i); rest.splice(j, 1); }
        row.cands = all.filter(k => keep.has(k.i)); row.total = all.length;
        row.W = W; row.H = H; res.push(row); continue;
      }
      for (const d of dets) {
        const t0 = performance.now(); let r = null, err = null;
        try { r = DET[d].detect(c, Object.assign({ sensitivity: level }, extra)); } catch (e) { err = String(e && e.stack || e); }
        const ms = performance.now() - t0;
        const o = { ms, found: !!r, err };
        if (r) {
          o.quad = r.quad; o.score = r.score; o.source = r.source;
          if (gt.hasSheet) {
            o.iou = SceneLib.iou(r.quad, gt.poly);
            const dq = SceneLib.orderQuad(r.quad.map(p => [p[0] * W, p[1] * H])), gq = SceneLib.orderQuad(gt.quad.map(p => [p[0] * W, p[1] * H]));
            let m = 0; for (let k = 0; k < 4; k++) m = Math.max(m, Math.hypot(dq[k][0] - gq[k][0], dq[k][1] - gq[k][1]));
            o.cornerErr = m / Math.hypot(W, H);
          }
        }
        row.det[d] = o;
      }
      res.push(row);
      if (wantFails) {
        // keep a thumbnail with both outlines for scenes some detector got wrong
        const bad = dets.some(d => { const o = row.det[d]; return gt.hasSheet ? (!o.found || o.iou < 0.85) : o.found; });
        if (bad) {
          const tw = 440, th = 300, s = Math.min((tw - 6) / W, (th - 6) / H), t = document.createElement('canvas'); t.width = tw; t.height = th;
          const g = t.getContext('2d'); g.fillStyle = '#111'; g.fillRect(0, 0, tw, th); g.drawImage(c, 3, 3, W * s, H * s);
          const poly = (pts, col, lw) => { g.strokeStyle = col; g.lineWidth = lw; g.beginPath(); pts.forEach((p, k) => { const x = 3 + p[0] * W * s, y = 3 + p[1] * H * s; if (k) g.lineTo(x, y); else g.moveTo(x, y); }); g.closePath(); g.stroke(); };
          if (gt.hasSheet) poly(gt.poly, '#2aff6a', 2);
          const cols = ['#ff3030', '#30a0ff', '#ffb000', '#ff30ff'];
          dets.forEach((d, k) => { if (row.det[d].found) poly(row.det[d].quad, cols[k], 2); });
          g.fillStyle = '#ffe066'; g.font = '12px sans-serif';
          g.fillText(it.seed + ' ' + (it.entry.meta.bg || '') + ' ' + dets.map(d => row.det[d].found ? (row.det[d].iou != null ? row.det[d].iou.toFixed(2) : 'FP') : '--').join(' / '), 6, 15);
          (fails[it.cat] = fails[it.cat] || []).push(t.toDataURL('image/png').split(',')[1]);
        }
      }
    }
    return { res, fails };
  }, { items, dets, level, extra, base, wantFails: !!failsDir, wantCands: !!candsOut, aug });
  if (candsOut) { fs.writeFileSync(candsOut, JSON.stringify(out.res)); console.log('wrote', candsOut, out.res.length, 'scenes'); await browser.close(); server.close(); return; }

  // ---- report
  const pct = (a, b) => b ? (100 * a / b).toFixed(0).padStart(3) + '%' : '  - ';
  const lines = []; const head = 'category'.padEnd(11) + ' n  ' + dets.map(d => ('[' + d + ']').padEnd(40)).join('');
  lines.push(head); lines.push(''.padEnd(head.length, '-'));
  const agg = {}; dets.forEach(d => agg[d] = { good: 0, ok: 0, wrong: 0, miss: 0, n: 0, iou: 0, fp: 0, nn: 0, ms: 0, msn: 0, err: 0 });
  const byCat = {};
  out.res.forEach(r => (byCat[r.cat] = byCat[r.cat] || []).push(r));
  for (const cat of cats) {
    const rows = byCat[cat] || []; if (!rows.length) continue; let line = cat.padEnd(11) + String(rows.length).padStart(2) + ' ';
    for (const d of dets) {
      const a = { good: 0, ok: 0, wrong: 0, miss: 0, fp: 0, iou: 0, ms: 0 };
      rows.forEach(r => { const o = r.det[d]; a.ms += o.ms; if (o.err) a.err = (a.err || 0) + 1;
        if (!r.hasSheet) { if (o.found) a.fp++; return; }
        if (!o.found) { a.miss++; return; }
        a.iou += o.iou; if (o.iou >= 0.93) a.good++; if (o.iou >= 0.85) a.ok++; else a.wrong++; });
      const n = rows.length, g = agg[d];
      if (rows[0].hasSheet) {
        line += (' good ' + pct(a.good, n) + ' ok ' + pct(a.ok, n) + ' wrong ' + pct(a.wrong, n) + ' miss ' + pct(a.miss, n) + ' iou ' + (a.iou / n).toFixed(2) + ' ' + (a.ms / n).toFixed(0) + 'ms').padEnd(40);
        g.good += a.good; g.ok += a.ok; g.wrong += a.wrong; g.miss += a.miss; g.n += n; g.iou += a.iou;
      } else {
        line += (' false positives ' + a.fp + '/' + n + ' (' + pct(a.fp, n).trim() + ')  ' + (a.ms / n).toFixed(0) + 'ms').padEnd(40); g.fp += a.fp; g.nn += n;
      }
      g.ms += a.ms; g.msn += n;
      if (a.err) line += ' ERR x' + a.err;
    }
    lines.push(line);
  }
  lines.push(''.padEnd(head.length, '-'));
  let tot = 'ALL sheets '.padEnd(14);
  for (const d of dets) { const g = agg[d]; tot += (' good ' + pct(g.good, g.n) + ' ok ' + pct(g.ok, g.n) + ' wrong ' + pct(g.wrong, g.n) + ' miss ' + pct(g.miss, g.n) + ' iou ' + (g.iou / g.n).toFixed(3) + '  FP ' + g.fp + '/' + g.nn).padEnd(40); }
  lines.push(tot);
  console.log(lines.join('\n'));
  if (jsonOut) fs.writeFileSync(jsonOut, JSON.stringify(out.res));
  if (failsDir) {
    fs.mkdirSync(failsDir, { recursive: true });
    // one sheet per category from the thumbnails (done by the browser again)
    const p2 = await (await browser.newContext()).newPage(); await p2.goto(base + '/bench.html');
    for (const cat of Object.keys(out.fails)) {
      const png = await p2.evaluate(async (list) => {
        const C = 4, tw = 440, th = 300, R = Math.ceil(list.length / C), s = document.createElement('canvas'); s.width = C * tw; s.height = R * th; const g = s.getContext('2d');
        for (let i = 0; i < list.length; i++) { const im = await new Promise(r => { const x = new Image(); x.onload = () => r(x); x.src = 'data:image/png;base64,' + list[i]; }); g.drawImage(im, (i % C) * tw, Math.floor(i / C) * th); }
        return s.toDataURL('image/png').split(',')[1];
      }, out.fails[cat].slice(0, 16));
      fs.writeFileSync(path.join(failsDir, cat + '.png'), Buffer.from(png, 'base64'));
    }
  }
  await browser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
