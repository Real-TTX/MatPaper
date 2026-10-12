// Renders synthetic scenes (scene-lib.js) to disk, or a contact sheet to look at them.
//   node gen-scenes.js sheet <cat> <seed0> <count> <out.png> [cols]
//   node gen-scenes.js all <seed0> <count> [cat,cat,...]       (writes scenes/<cat>/<seed>.jpg + scenes/index.json)
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start, listPhotos } = require('./bench-server');
const OUT = path.join(__dirname, 'scenes');

async function open() {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  page.on('console', m => { if (m.type() === 'error') console.log('console.error', m.text()); });
  await page.goto(base + '/bench.html');
  await page.addScriptTag({ url: base + '/scene-lib.js' });
  await page.evaluate(async (urls) => {
    window.PHOTOS = await Promise.all(urls.map(u => new Promise((res, rej) => { const im = new Image(); im.onload = () => res(im); im.onerror = () => rej(u); im.src = u; })));
  }, listPhotos());
  return { server, browser, page, base };
}

async function main() {
  const [mode, ...a] = process.argv.slice(2);
  const { server, browser, page } = await open();
  try {
    if (mode === 'sheet') {
      const [cat, seed0, count, out, cols] = a;
      const t0 = Date.now();
      const url = await page.evaluate(async ({ cat, seed0, count, cols }) => {
        const n = +count, C = +(cols || 4), tw = 440, th = 300, R = Math.ceil(n / C);
        const sheet = document.createElement('canvas'); sheet.width = C * tw; sheet.height = R * th; const g = sheet.getContext('2d'); g.fillStyle = '#111'; g.fillRect(0, 0, sheet.width, sheet.height);
        for (let i = 0; i < n; i++) {
          const sc = await SceneLib.render(cat, +seed0 + i, PHOTOS);
          const w = sc.canvas.width, h = sc.canvas.height, s = Math.min((tw - 6) / w, (th - 6) / h), ox = (i % C) * tw + 3, oy = Math.floor(i / C) * th + 3;
          g.drawImage(sc.canvas, ox, oy, w * s, h * s);
          if (sc.gt.quad) { g.strokeStyle = '#ff2a2a'; g.lineWidth = 2; g.beginPath(); sc.gt.poly.forEach((p, k) => { const x = ox + p[0] * w * s, y = oy + p[1] * h * s; if (k) g.lineTo(x, y); else g.moveTo(x, y); }); g.closePath(); g.stroke(); }
          g.fillStyle = '#ffe066'; g.font = '12px sans-serif'; g.fillText(sc.meta.seed + ' ' + sc.meta.bg + ' ' + sc.meta.style, ox + 4, oy + 14);
        }
        return sheet.toDataURL('image/png');
      }, { cat, seed0, count, cols });
      fs.writeFileSync(out, Buffer.from(url.split(',')[1], 'base64'));
      console.log('sheet', out, ((Date.now() - t0) / 1000).toFixed(1) + 's');
    } else if (mode === 'all') {
      const [seed0, count, cats] = a;
      const list = cats ? cats.split(',') : await page.evaluate(() => SceneLib.categories);
      fs.mkdirSync(OUT, { recursive: true });
      const indexFile = path.join(OUT, 'index.json'); const index = fs.existsSync(indexFile) ? JSON.parse(fs.readFileSync(indexFile, 'utf8')) : {};
      for (const cat of list) {
        fs.mkdirSync(path.join(OUT, cat), { recursive: true });
        const t0 = Date.now();
        for (let i = 0; i < +count; i++) {
          const seed = +seed0 + i;
          const r = await page.evaluate(async ({ cat, seed }) => {
            const sc = await SceneLib.render(cat, seed, PHOTOS);
            return { gt: sc.gt, meta: sc.meta, jpg: sc.canvas.toDataURL('image/jpeg', 0.95).split(',')[1] };
          }, { cat, seed });
          fs.writeFileSync(path.join(OUT, cat, seed + '.jpg'), Buffer.from(r.jpg, 'base64'));
          index[cat + '/' + seed] = { gt: r.gt, meta: r.meta };
        }
        fs.writeFileSync(indexFile, JSON.stringify(index));
        console.log(cat, count, ((Date.now() - t0) / 1000).toFixed(1) + 's');
      }
    }
  } finally { await browser.close(); server.close(); }
}
main().catch(e => { console.error(e); process.exit(1); });
