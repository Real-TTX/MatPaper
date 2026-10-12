// For failing scenes: what the model preferred and what the right candidate looked like.
//   node why.js <repo|detector.js> cat/seed [cat/seed ...]
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start } = require('./bench-server');
const [det, ...ids] = process.argv.slice(2);
const index = JSON.parse(fs.readFileSync(path.join(__dirname, 'scenes', 'index.json'), 'utf8'));
(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const page = await (await browser.newContext()).newPage();
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  await page.goto(base + '/bench.html');
  await page.addScriptTag({ url: base + '/scene-lib.js' });
  await page.addScriptTag({ url: base + server.detectorUrl(det) });
  for (const id of ids) {
    const r = await page.evaluate(async ({ id, gt, base }) => {
      const im = await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => no(id); i.src = base + '/scenes/' + id + '.jpg'; });
      const c = document.createElement('canvas'); c.width = im.naturalWidth; c.height = im.naturalHeight; c.getContext('2d', { willReadFrequently: true }).drawImage(im, 0, 0);
      const d = MatPaperScan.detect(c, { sensitivity: 1, debug: true, debugAll: true });
      const all = d.candidates.map((k, i) => ({ rank: i, z: k.score, iou: gt.hasSheet ? SceneLib.iou(k.V, gt.poly) : 0, f: k.f }));
      const short = k => ({ rank: k.rank, z: +k.z.toFixed(2), iou: +k.iou.toFixed(2), src: k.f.src, edge: [k.f.bMin, k.f.bMean, k.f.mMin, k.f.mMean].map(v => +v.toFixed(2)), dark: k.f.dark, nF: k.f.nFrame, vis: +k.f.vis.toFixed(2), area: +k.f.area.toFixed(3), rect: +k.f.rect.toFixed(2), ratio: +k.f.ratio.toFixed(2), white: Math.round(k.f.white), calm: +k.f.calm.toFixed(2), plain: Math.round(k.f.plain), busy: +k.f.busy.toFixed(2), grid: k.f.grid, per: +(k.f.perMean||0).toFixed(2), tMean: +(k.f.tMean||0).toFixed(2), ring: Math.round(k.f.ring), ringM: Math.round(k.f.ringM) });
      return { n: all.length, top: all.slice(0, 3).map(short), best: all.slice().sort((a, b) => b.iou - a.iou).slice(0, 2).map(short) };
    }, { id, gt: index[id].gt, base });
    console.log('== ' + id + ' (' + r.n + ' candidates)');
    r.top.forEach(k => console.log('  top ', JSON.stringify(k)));
    r.best.forEach(k => console.log('  best', JSON.stringify(k)));
  }
  await browser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
