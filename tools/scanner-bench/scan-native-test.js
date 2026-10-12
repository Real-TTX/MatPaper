// The "Camera app" way of the REAL scanner: photos are handed to the scanner (like the file the phone's camera app returns),
// the page is found in each, the review shows it. Checks the quad that was found against the truth and the notes that are shown.
//   node scan-native-test.js [--scenes easy/1,...]
const { chromium } = require('playwright-core'); const fs = require('fs'); const path = require('path');
const { start, listPhotos } = require('./bench-server');
const args = process.argv.slice(2); const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };

const scenes = opt('scenes', 'easy/2,easy/4,easy/6,lightdesk/1,lightdesk/3,shadow/1,shadow/3,occluded/2,clutter/1,clutter/3,cut/1,cut/2,cut/4,small/2,receipt/2,lowlight/1,highres/1,highres/2,highres/3,nosheet/1,nosheet/2,nosheet/3').split(',');

(async () => {
  const server = await start(0), base = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: process.env.SCANNER_BENCH_BROWSER || 'msedge', headless: true });
  const index = JSON.parse(fs.readFileSync(path.join(__dirname, 'scenes', 'index.json'), 'utf8'));
  const rows = [];
  for (const id of scenes) {
    const [cat, seed] = id.split('/'); const entry = index[id];
    const ctx = await browser.newContext({ viewport: { width: 420, height: 860 }, deviceScaleFactor: 2 });
    const page = await ctx.newPage(); const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(base + '/scan-harness.html');
    await page.addScriptTag({ url: base + '/scene-lib.js' });
    await page.evaluate(() => { localStorage.setItem('matpaper.scan.mode', 'native'); MatPaperScanner.open({ onDone: files => { window.__done = files; } }); });
    const t0 = Date.now();
    await page.setInputFiles('input[type=file][capture]', path.join(__dirname, 'scenes', cat, seed + '.jpg'));
    let reviewed = true;
    try { await page.waitForFunction(() => { const v = document.querySelectorAll('.scn__view'); return v[1] && !v[1].hidden; }, null, { timeout: 15000 }); } catch (e) { reviewed = false; }
    const ms = Date.now() - t0;
    await page.waitForTimeout(400);
    const noteShown = await page.evaluate(() => { const n = document.querySelector('.scn__view:not([hidden]) .scn__hint:not([hidden])'); return n ? n.textContent : ''; });
    const det = await page.evaluate(() => window.__detections.filter(d => d.r || true).map(d => ({ precise: d.precise, ms: d.ms, found: !!d.r, quad: d.r && d.r.quad, cut: undefined })));
    const last = det.filter(d => d.found).pop();
    const iou = last && entry.gt.hasSheet ? await page.evaluate(({ q, poly }) => SceneLib.iou(q, poly), { q: last.quad, poly: entry.gt.poly }) : null;
    // the crop the review shows: take a snapshot of the review canvas size for a sanity check
    const canvasSize = await page.evaluate(() => { const c = document.querySelector('.scn__view:not([hidden]) canvas.scn__page'); return c ? [c.width, c.height] : null; });
    rows.push({ id, reviewed, secs: +(ms / 1000).toFixed(1), found: !!last, iou: iou == null ? null : +iou.toFixed(2), detMs: last ? +last.ms.toFixed(0) : null, hasSheet: entry.gt.hasSheet, note: noteShown, errors: errors.length });
    console.log(id.padEnd(14), JSON.stringify(rows[rows.length - 1]));
    await ctx.close();
  }
  const sheets = rows.filter(r => r.hasSheet), nos = rows.filter(r => !r.hasSheet);
  console.log('with a sheet - found ' + sheets.filter(r => r.found).length + '/' + sheets.length + ', right (IoU>=.9) ' + sheets.filter(r => r.iou >= 0.9).length + '; without a sheet - nothing found ' + nos.filter(r => !r.found).length + '/' + nos.length + ' (the note says so: ' + nos.filter(r => /No page|Keine Seite/.test(r.note)).length + ')');
  await browser.close(); server.close();
})().catch(e => { console.error(e); process.exit(1); });
