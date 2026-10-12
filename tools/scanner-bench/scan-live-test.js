// The REAL scanner (scanner.js + scanner-core.js) in a browser with a fake camera: a still scene as the "camera", wait for the
// automatic capture, finish, and compare the straightened page with the flat paper the scene was made from.
//   node scan-live-test.js [--scenes easy/1,easy/2,...] [--viewport 1280x800]
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start, listPhotos } = require('./bench-server');
const args = process.argv.slice(2); const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };

const scenes = (opt('scenes', 'easy/1,easy/3,easy/5,easy/7,lightdesk/2,lightdesk/5,shadow/2,shadow/4,occluded/1,occluded/3,clutter/2,small/1,phoneport/2,phoneport/5,lowlight/2,receipt/1')).split(',');
const [vw, vh] = opt('viewport', '1280x800').split('x').map(Number);

// in the helper page: similarity (correlation of grey values) of the scanner output with the flat paper, best of the four turns
const NCC = async ({ out, flat }) => {
  const load = b => new Promise(ok => { const i = new Image(); i.onload = () => ok(i); i.src = 'data:image/png;base64,' + b; });
  const a = await load(out), b = await load(flat);
  const gray = (img, turn) => {
    const c = document.createElement('canvas'); c.width = 150; c.height = 150; const x = c.getContext('2d', { willReadFrequently: true });
    x.imageSmoothingQuality = 'high'; x.save(); x.translate(75, 75); x.rotate(turn * Math.PI / 2); x.drawImage(img, -75, -75, 150, 150); x.restore();
    const d = x.getImageData(0, 0, 150, 150).data, g = new Float32Array(150 * 150); for (let i = 0; i < g.length; i++) g[i] = 0.3 * d[i * 4] + 0.59 * d[i * 4 + 1] + 0.11 * d[i * 4 + 2]; return g;
  };
  const corr = (p, q) => { let mp = 0, mq = 0; for (let i = 0; i < p.length; i++) { mp += p[i]; mq += q[i]; } mp /= p.length; mq /= q.length; let sp = 0, sq = 0, spq = 0; for (let i = 0; i < p.length; i++) { sp += (p[i] - mp) ** 2; sq += (q[i] - mq) ** 2; spq += (p[i] - mp) * (q[i] - mq); } return spq / Math.sqrt(sp * sq + 1e-9); };
  const A = gray(a, 0); let best = -1;
  for (let turn = 0; turn < 4; turn++) best = Math.max(best, corr(A, gray(b, turn)));
  return best;
};

(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const helperBrowser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const helper = await (await helperBrowser.newContext()).newPage(); await helper.goto(base + '/bench.html'); await helper.addScriptTag({ url: base + '/scene-lib.js' });
  await helper.evaluate(async (urls) => { window.PHOTOS = await Promise.all(urls.map(u => new Promise((ok, no) => { const im = new Image(); im.onload = () => ok(im); im.onerror = () => no(u); im.src = u; }))); }, listPhotos());
  const results = [];
  for (const id of scenes) {
    const [cat, seed] = id.split('/');
    const jpg = fs.readFileSync(path.join(__dirname, 'scenes', cat, seed + '.jpg'));
    const file = path.join(__dirname, 'scene-live.mjpeg'); fs.writeFileSync(file, Buffer.concat(Array.from({ length: 40 }, () => jpg)));
    const flat = await helper.evaluate(async ({ cat, seed }) => { const sc = await SceneLib.render(cat, +seed, PHOTOS); const c = sc.paper; return c ? c.toDataURL('image/png').split(',')[1] : null; }, { cat, seed });
    const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--use-file-for-fake-video-capture=' + file] });
    const ctx = await browser.newContext({ viewport: { width: vw, height: vh }, permissions: ['camera'] });
    const page = await ctx.newPage(); const errors = []; page.on('pageerror', e => errors.push(e.message)); page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
    await page.goto(base + '/scan-harness.html');
    const t0 = Date.now();
    await page.evaluate(() => { MatPaperScanner.open({ onDone: (files) => { window.__done = files; } }); });
    let captured = true;
    try { await page.waitForFunction(() => { const v = document.querySelectorAll('.scn__view'); return v[1] && !v[1].hidden; }, null, { timeout: 15000 }); } catch (e) { captured = false; }
    const tCapture = Date.now() - t0;
    let out = null;
    if (captured) {
      await page.waitForTimeout(500);
      await page.click('.scn__view:not([hidden]) .scn__action--primary');
      await page.waitForFunction(() => window.__done && window.__done.length, null, { timeout: 15000 }).catch(() => { });
      out = await page.evaluate(async () => { const f = window.__done && window.__done[0]; if (!f) return null; return await new Promise(r => { const fr = new FileReader(); fr.onload = () => r(fr.result.split(',')[1]); fr.readAsDataURL(f); }); });
    }
    const det = await page.evaluate(() => window.__detections.map(d => ({ ms: d.ms, precise: d.precise, hint: d.hint, found: !!d.r, area: d.r && d.r.area, margin: d.r && d.r.margin, score: d.r && d.r.score, quad: d.r && d.r.quad })));
    const ncc = out && flat ? await helper.evaluate(NCC, { out, flat }) : null;
    const live = det.filter(d => !d.precise);
    const meta = JSON.parse(fs.readFileSync(path.join(__dirname, 'scenes', 'index.json'), 'utf8'))[id], gt = meta.gt;
    // does the page stand up the way it was photographed? (the aspect of the output against the aspect of the true page)
    let aspectOk = null;
    if (out && gt.quad) {
      const [W, H] = meta.meta.size, q = gt.quad.map(p => [p[0] * W, p[1] * H]), d = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
      const expect = ((d(q[0], q[1]) + d(q[3], q[2])) / 2) / ((d(q[0], q[3]) + d(q[1], q[2])) / 2);
      const dim = await helper.evaluate(async b64 => { const i = await new Promise(ok => { const im = new Image(); im.onload = () => ok(im); im.src = 'data:image/jpeg;base64,' + b64; }); return [i.naturalWidth, i.naturalHeight]; }, out);
      aspectOk = Math.abs(Math.log((dim[0] / dim[1]) / expect)) < 0.12;
    }
    const iouOf = d => d && d.quad ? helper.evaluate(({ q, poly }) => SceneLib.iou(q, poly), { q: d.quad, poly: gt.poly }) : Promise.resolve(null);
    const lastLive = live.filter(d => d.found).pop(), lastPrecise = det.filter(d => d.precise).pop();
    const iouLive = await iouOf(lastLive), iouPrecise = await iouOf(lastPrecise), r2 = v => v == null ? null : +v.toFixed(2);
    results.push({ id, captured, secs: +(tCapture / 1000).toFixed(1), analyses: live.length, found: live.filter(d => d.found).length, avgMs: +(live.reduce((a, d) => a + d.ms, 0) / Math.max(1, live.length)).toFixed(0), ncc: r2(ncc), iouLive: r2(iouLive), iouPrecise: r2(iouPrecise), area: lastLive ? r2(lastLive.area) : null, margin: lastLive ? r2(lastLive.margin) : null, aspectOk, errors: errors.filter(e => !/404/.test(e)).length });
    console.log(id.padEnd(14), JSON.stringify(results[results.length - 1]));
    if (out && process.argv.includes('--save')) fs.writeFileSync(path.join(__dirname, 'live-out-' + cat + '-' + seed + '.jpg'), Buffer.from(out, 'base64'));
    await browser.close();
  }
  const ok = results.filter(r => r.captured && r.ncc != null && r.ncc >= 0.8).length;
  console.log('scanner: captured ' + results.filter(r => r.captured).length + '/' + results.length + ', crop looks right (NCC>=0.8) ' + ok + '/' + results.length);
  await helperBrowser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
