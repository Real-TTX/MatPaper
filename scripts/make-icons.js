#!/usr/bin/env node
// Draws the PNG icons and favicon.ico of the app from the SVGs in src/MatPaper/wwwroot/icons, which are the source:
//   logo.svg      level 2, the logo        -> icon-192.png
//   icon.svg      level 1, the illustration -> icon-512.png
//   maskable.svg  full bleed, safe area    -> maskable-512.png and apple-touch-icon.png (180, no transparency)
//   favicon.svg   level 3, the favicon     -> ../favicon.ico (16, 32 and 48 px, PNG entries)
// It needs Node and playwright-core (for example `npm i playwright-core` somewhere on NODE_PATH) and an installed Edge or Chrome: the browser draws the SVGs.
// Run it after a change of one of the SVGs:  node scripts/make-icons.js
const fs = require('fs');
const os = require('os');
const path = require('path');

let chromium;
try {
  ({ chromium } = require('playwright-core'));
} catch {
  console.error('playwright-core is missing: npm i playwright-core (or point NODE_PATH at a folder that has it).');
  process.exit(1);
}

const icons = path.resolve(__dirname, '..', 'src', 'MatPaper', 'wwwroot', 'icons');
const root = path.resolve(icons, '..');

async function launch() {
  for (const channel of ['msedge', 'chrome']) {
    try {
      return await chromium.launch({ channel, headless: true });
    } catch {
      // try the next one
    }
  }
  throw new Error('Neither Edge nor Chrome could be started.');
}

// A page with nothing but the image, so that the screenshot has a transparent background where the SVG has none.
async function render(browser, svgFile, size) {
  const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
  const html = path.join(os.tmpdir(), `matpaper-icon-${process.pid}-${size}.html`);
  const src = 'file:///' + path.join(icons, svgFile).replace(/\\/g, '/');
  fs.writeFileSync(html, `<!doctype html><html style="background:transparent"><body style="margin:0;background:transparent"><img src="${src}" width="${size}" height="${size}" style="display:block"></body></html>`);
  await page.goto('file:///' + html.replace(/\\/g, '/'));
  await page.waitForFunction(() => document.images[0].complete);
  const png = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  await page.close();
  fs.unlinkSync(html);
  return png;
}

// An ICO file that holds PNG images (every browser and Windows since Vista read them).
function ico(images) {
  const head = Buffer.alloc(6);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(images.length, 4);
  const dir = Buffer.alloc(16 * images.length);
  let offset = head.length + dir.length;
  images.forEach(({ size, png }, i) => {
    const at = i * 16;
    dir.writeUInt8(size >= 256 ? 0 : size, at);
    dir.writeUInt8(size >= 256 ? 0 : size, at + 1);
    dir.writeUInt16LE(1, at + 4);
    dir.writeUInt16LE(32, at + 6);
    dir.writeUInt32LE(png.length, at + 8);
    dir.writeUInt32LE(offset, at + 12);
    offset += png.length;
  });
  return Buffer.concat([head, dir, ...images.map((i) => i.png)]);
}

(async () => {
  const browser = await launch();
  const write = (name, buffer) => {
    fs.writeFileSync(path.join(icons, name), buffer);
    console.log(`${name}  ${buffer.length} bytes`);
  };
  write('icon-192.png', await render(browser, 'logo.svg', 192));
  write('icon-512.png', await render(browser, 'icon.svg', 512));
  write('maskable-512.png', await render(browser, 'maskable.svg', 512));
  write('apple-touch-icon.png', await render(browser, 'maskable.svg', 180));
  const small = [];
  for (const size of [16, 32, 48]) {
    small.push({ size, png: await render(browser, 'favicon.svg', size) });
  }
  const file = ico(small);
  fs.writeFileSync(path.join(root, 'favicon.ico'), file);
  console.log(`favicon.ico  ${file.length} bytes (16, 32, 48)`);
  await browser.close();
})().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
