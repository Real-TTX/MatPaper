// Live scanner behaviour over time: a scene is drawn onto a canvas that serves as the camera, with motion (approaching, shaking,
// panning), a hand passing in front, exposure changes. Every analysis the scanner makes is compared with where the page really was.
//   node scan-seq-test.js [--scenes easy/1,...] [--seqs still,approach,...] [--save dir]
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start, listPhotos } = require('./bench-server');
const args = process.argv.slice(2); const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };
const SCANNER = opt('scanner', '');
const scenes = opt('scenes', 'easy/1,easy/5,lightdesk/2,shadow/2').split(',');
const approachScenes = opt('approach-scenes', 'small/1,small/4,small/6,small/9').split(',');
const seqNames = opt('seqs', 'still,approach,shake,pan,hand,exposure').split(',');

// the scripts run in the page: (t seconds) -> { s: scale about the centre, dx, dy (fractions of the frame), rot (radians), gain, hand: {x,y,r}|null }
const SEQS = `({
  still: t => ({ s: 1, dx: 0, dy: 0, rot: 0, gain: 1 }),
  approach: t => { const k = Math.min(1, t / 2.6); const e = k * k * (3 - 2 * k); return { s: 1 + 0.7 * e, dx: 0.0, dy: 0.0, rot: 0.04 * (1 - e), gain: 1 }; },
  shake: t => ({ s: 1 + 0.01 * Math.sin(t * 9), dx: 0.006 * Math.sin(t * 11) + 0.004 * Math.sin(t * 17), dy: 0.006 * Math.cos(t * 13), rot: 0.012 * Math.sin(t * 10), gain: 1 }),
  pan: t => { const k = Math.min(1, t / 2.0); const e = k * k * (3 - 2 * k); return { s: 1.15, dx: -0.05 + 0.10 * e, dy: 0.01 * Math.sin(t * 2), rot: 0, gain: 1 }; },
  hand: t => ({ s: 1, dx: 0, dy: 0, rot: 0, gain: 1, hand: (t > 1.2 && t < 2.1) ? { x: 0.25 + (t - 1.2) * 0.4, y: 0.35, r: 0.12 } : null }),
  exposure: t => ({ s: 1, dx: 0, dy: 0, rot: 0, gain: 1 + 0.25 * Math.sin(t * 3) }),
  blurburst: t => ({ s: 1, dx: 0, dy: 0, rot: 0, gain: 1, blur: (((Math.floor(t * 12) * 2654435761) >>> 0) % 100 < 45) ? 4 : 0 })
})`;

(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const index = JSON.parse(fs.readFileSync(path.join(__dirname, 'scenes', 'index.json'), 'utf8'));
  const rows = [];
  for (const id0 of scenes.concat(approachScenes)) {
    const [cat, seed] = id0.split('/'); const id = id0; if (!index[id]) { continue; } const gtQuad = index[id].gt.quad, size = index[id].meta.size;
    for (const seq of seqNames) {
      if ((seq === 'approach') !== approachScenes.includes(id0)) { continue; }
      const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      const page = await ctx.newPage(); const errors = []; page.on('pageerror', e => { errors.push(e.message); console.log('PAGEERROR', e.message); });
      await page.goto(base + '/scan-harness.html?canvas=1&w=' + size[0] + '&h=' + size[1] + (SCANNER ? '&scanner=/' + SCANNER.replace(/^\//, '') : ''));
      await page.evaluate(n => { window.__seqName = n; }, seq);
      await page.evaluate(async ({ url, seqCode, gtQuad }) => {
        const img = await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = url; });
        const S = eval(seqCode)[window.__seqName]; const t0 = performance.now();
        const W = window.__cam.width, H = window.__cam.height;
        const state = () => S((performance.now() - t0) / 1000);
        const xf = (p, st) => { let x = (p[0] - 0.5) * W, y = (p[1] - 0.5) * H; x *= st.s; y *= st.s; const c = Math.cos(st.rot), s = Math.sin(st.rot); const rx = x * c - y * s, ry = x * s + y * c; return [(rx + W / 2 + st.dx * W) / W, (ry + H / 2 + st.dy * H) / H]; };
        window.__gtNow = () => { const st = state(); return gtQuad.map(p => xf(p, st)); };
        window.__drawFn = (now, cam) => {
          const st = state(), x = cam.getContext('2d');
          x.setTransform(1, 0, 0, 1, 0, 0); x.fillStyle = '#000'; x.fillRect(0, 0, W, H);
          x.setTransform(st.s * Math.cos(st.rot), st.s * Math.sin(st.rot), -st.s * Math.sin(st.rot), st.s * Math.cos(st.rot), W / 2 + st.dx * W, H / 2 + st.dy * H);
          x.filter = (st.blur ? 'blur(' + st.blur + 'px) ' : '') + (st.gain !== 1 ? 'brightness(' + st.gain + ')' : '') || 'none'; x.drawImage(img, -W / 2, -H / 2, W, H); x.filter = 'none'; x.setTransform(1, 0, 0, 1, 0, 0);
          if (st.hand) { x.fillStyle = '#d9a07c'; x.beginPath(); x.ellipse(st.hand.x * W, st.hand.y * H, st.hand.r * W * 0.6, st.hand.r * H * 1.6, 0.6, 0, Math.PI * 2); x.fill(); }
        };
      }, { url: base + '/scenes/' + cat + '/' + seed + '.jpg', seqCode: SEQS, gtQuad });
      const t0 = Date.now();
      await page.evaluate(() => { MatPaperScanner.open({ onDone: files => { window.__done = files; } }); });
      let captured = true;
      try { await page.waitForFunction(() => { const v = document.querySelectorAll('.scn__view'); return v[1] && !v[1].hidden; }, null, { timeout: 12000 }); } catch (e) { captured = false; }
      const tCap = Date.now() - t0;
      let outSharp = null;
      if (captured) {
        await page.waitForTimeout(400); await page.click('.scn__view:not([hidden]) .scn__action--primary'); await page.waitForFunction(() => window.__done && window.__done.length, null, { timeout: 15000 }).catch(() => { });
        outSharp = await page.evaluate(async () => { const f = window.__done && window.__done[0]; if (!f) return null; const bmp = await createImageBitmap(f); const w = 800, h = Math.round(800 * bmp.height / bmp.width); const c = document.createElement('canvas'); c.width = w; c.height = h; const x = c.getContext('2d', { willReadFrequently: true }); x.drawImage(bmp, 0, 0, w, h); const d = x.getImageData(0, 0, w, h).data, g = new Float32Array(w * h); for (let i = 0; i < g.length; i++) g[i] = 0.3 * d[i * 4] + 0.59 * d[i * 4 + 1] + 0.11 * d[i * 4 + 2]; let s = 0, n = 0; for (let y = 20; y < h - 20; y++) for (let xx = 20; xx < w - 20; xx++) { const i = y * w + xx; s += Math.abs(4 * g[i] - g[i - 1] - g[i + 1] - g[i - w] - g[i + w]); n++; } return s / n; });
      }
      const det = await page.evaluate(() => window.__detections.map(d => ({ t: d.t, ms: d.ms, precise: d.precise, found: !!d.r, quad: d.r && d.r.quad, gt: d.gt, area: d.r && d.r.area })));
      await page.addScriptTag({ url: base + '/scene-lib.js' });
      const iou = await page.evaluate(det => det.map(d => (d.quad && d.gt) ? SceneLib.iou(d.quad, d.gt) : null), det);
      const live = det.map((d, i) => ({ d, iou: iou[i] })).filter(x => !x.d.precise);
      const found = live.filter(x => x.d.found), good = found.filter(x => x.iou >= 0.9);
      let flickers = 0; for (let i = 1; i < live.length; i++) if (live[i - 1].d.found && !live[i].d.found) flickers++;
      const wrong = found.filter(x => x.iou < 0.6).length;
      const precise = det.map((d, i) => ({ d, iou: iou[i] })).filter(x => x.d.precise).pop();
      // was the capture taken while the page was where the outline said it was? (GT at capture time vs the used quad: the precise detection)
      rows.push({ id, seq, captured, capSec: +(tCap / 1000).toFixed(1), analyses: live.length, foundPct: Math.round(100 * found.length / Math.max(1, live.length)), goodPct: Math.round(100 * good.length / Math.max(1, live.length)), meanIou: found.length ? +(found.reduce((a, x) => a + x.iou, 0) / found.length).toFixed(2) : null, flickers, wrongFrames: wrong, capIou: precise && precise.iou != null ? +precise.iou.toFixed(2) : null, outSharp: outSharp == null ? null : +outSharp.toFixed(2), errors: errors.length });
      console.log((id + ' ' + seq).padEnd(22), JSON.stringify(rows[rows.length - 1]));
      await ctx.close();
    }
  }
  const avg = k => (rows.reduce((a, r) => a + (r[k] || 0), 0) / rows.length).toFixed(1);
  console.log('mean: found ' + avg('foundPct') + '%, good ' + avg('goodPct') + '%, flickers ' + avg('flickers') + ', wrong frames ' + avg('wrongFrames') + ', captured ' + rows.filter(r => r.captured).length + '/' + rows.length);
  await browser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
