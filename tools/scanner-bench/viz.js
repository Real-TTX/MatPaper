// Looks at what a detector sees in a few scenes: the Hough lines (cyan = found), the ground truth (green) and the best candidates (red/orange).
//   node viz.js <repo|detector.js> <out.png> cat/seed cat/seed ...      (detect(debug) of the detector is used)
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start } = require('./bench-server');
const [det, out, ...ids] = process.argv.slice(2);
const index = JSON.parse(fs.readFileSync(path.join(__dirname, 'scenes', 'index.json'), 'utf8'));
(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  await page.goto(base + '/bench.html');
  await page.addScriptTag({ url: base + '/scene-lib.js' });
  await page.addScriptTag({ url: base + server.detectorUrl(det) });
  const items = ids.map(id => ({ id, entry: index[id] }));
  const png = await page.evaluate(async ({ items, base }) => {
    const C = 2, tw = 880, th = 560, R = Math.ceil(items.length / C), sheet = document.createElement('canvas'); sheet.width = C * tw; sheet.height = R * th;
    const g = sheet.getContext('2d'); g.fillStyle = '#111'; g.fillRect(0, 0, sheet.width, sheet.height);
    for (let n = 0; n < items.length; n++) {
      const it = items[n], im = await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => no(it.id); i.src = base + '/scenes/' + it.id + '.jpg'; });
      const c = document.createElement('canvas'); c.width = im.naturalWidth; c.height = im.naturalHeight; c.getContext('2d', { willReadFrequently: true }).drawImage(im, 0, 0);
      const d = MatPaperScan.detect(c, { sensitivity: 1, debug: true });
      const W = c.width, H = c.height, s = Math.min((tw - 8) / W, (th - 8) / H), ox = (n % C) * tw + 4, oy = Math.floor(n / C) * th + 4;
      g.drawImage(c, ox, oy, W * s, H * s);
      const poly = (pts, col, lw) => { g.strokeStyle = col; g.lineWidth = lw; g.beginPath(); pts.forEach((p, k) => { const x = ox + p[0] * W * s, y = oy + p[1] * H * s; if (k) g.lineTo(x, y); else g.moveTo(x, y); }); g.closePath(); g.stroke(); };
      if (d.lines) {
        d.lines.forEach((l, k) => { // draw the infinite line clipped to the picture, thinner for weaker lines
          const ux = -l.ny, uy = l.nx, ox2 = l.nx * l.rho, oy2 = l.ny * l.rho; let lo = -1e9, hi = 1e9;
          const clip = (o, u, max) => { if (Math.abs(u) < 1e-9) return; let a = (0 - o) / u, b = (max - o) / u; if (a > b) { const t = a; a = b; b = t; } lo = Math.max(lo, a); hi = Math.min(hi, b); };
          clip(ox2, ux, d.w - 1); clip(oy2, uy, d.h - 1);
          const p = t => [(ox2 + t * ux + 0.5) / d.w, (oy2 + t * uy + 0.5) / d.h];
          const a = p(lo), b = p(hi); g.strokeStyle = 'rgba(0,255,255,' + Math.max(0.25, 1 - k / 35) + ')'; g.lineWidth = 1; g.beginPath(); g.moveTo(ox + a[0] * W * s, oy + a[1] * H * s); g.lineTo(ox + b[0] * W * s, oy + b[1] * H * s); g.stroke();
        });
      }
      if (it.entry.gt.hasSheet) poly(it.entry.gt.poly, '#2aff6a', 2);
      (d.candidates || []).slice(0, 3).forEach((k, i) => poly(k.V, ['#ff3030', '#ff9a00', '#ffe000'][i], 2));
      g.fillStyle = '#ffe066'; g.font = '14px sans-serif';
      const c0 = (d.candidates || [])[0];
      g.fillText(it.id + ' lines ' + (d.lines || []).length + (c0 ? ' top ' + c0.score.toFixed(2) + ' ' + JSON.stringify(c0.f, (k, v) => typeof v === 'number' ? +v.toFixed(2) : v) : ''), ox + 4, oy + 16);
    }
    return sheet.toDataURL('image/png').split(',')[1];
  }, { items, base });
  fs.writeFileSync(out, Buffer.from(png, 'base64')); console.log('wrote', out);
  await browser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
