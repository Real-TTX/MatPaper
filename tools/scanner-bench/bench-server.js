// A tiny static server for the benchmark pages:
//   /            this folder (bench.html, scene-lib.js, the scenes/ cache ...)
//   /repo/       the app's wwwroot/js (scanner-core.js, scanner.js)
//   /css/        the app's wwwroot/css
//   /photos/     real photographs used as backgrounds (optional, see listPhotos)
//   /det/<n>     any other detector file (an older scanner-core.js) registered with detectorUrl()
const http = require('http'), fs = require('fs'), path = require('path');
const HERE = __dirname, WWWROOT = path.join(HERE, '..', '..', 'src', 'MatPaper', 'wwwroot');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png', '.css': 'text/css' };

// Photographs for backgrounds with "natural" texture: every JPEG over 200 KB in the folder in SCANNER_BENCH_PHOTOS (one level of
// sub-folders is looked into); without it the Windows wallpapers are used when they exist. No photos is fine: those scenes then
// get a plain desk instead.
function photoRoot() {
  if (process.env.SCANNER_BENCH_PHOTOS) { return process.env.SCANNER_BENCH_PHOTOS; }
  return fs.existsSync('C:/Windows/Web/Wallpaper') ? 'C:/Windows/Web/Wallpaper' : '';
}
function listPhotos() {
  const root = photoRoot(), urls = [];
  if (!root || !fs.existsSync(root)) { return urls; }
  const take = (dir, rel) => fs.readdirSync(dir).forEach(f => {
    const p = path.join(dir, f), st = fs.statSync(p);
    if (st.isDirectory() && rel === '') { take(p, f + '/'); } else if (/\.jpe?g$/i.test(f) && st.size > 200000) { urls.push('/photos/' + rel + f); }
  });
  take(root, '');
  return urls;
}

function start(port) {
  const detectors = [];
  const server = http.createServer((req, res) => {
    const url = decodeURIComponent(req.url.split('?')[0]);
    let file = null;
    if (url.startsWith('/det/')) { file = detectors[+url.slice(5)]; }
    else if (url.startsWith('/photos/')) { file = path.join(photoRoot(), url.slice(8)); }
    else if (url.startsWith('/repo/')) { file = path.join(WWWROOT, 'js', url.slice(6)); }
    else if (url.startsWith('/css/')) { file = path.join(WWWROOT, 'css', url.slice(5)); }
    else { file = path.join(HERE, url); }
    if (file && fs.existsSync(file) && fs.statSync(file).isFile()) {
      res.writeHead(200, { 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', 'Cache-Control': 'no-store' });
      return fs.createReadStream(file).pipe(res);
    }
    res.writeHead(404); res.end('not found');
  });
  // "repo" = the scanner-core.js of this checkout; anything else is a path to a file (e.g. an older version from git show)
  server.detectorUrl = spec => {
    if (!spec || spec === 'repo' || spec === 'new') { return '/repo/scanner-core.js'; }
    const file = path.resolve(spec);
    if (!fs.existsSync(file)) { throw new Error('detector not found: ' + spec); }
    detectors.push(file); return '/det/' + (detectors.length - 1);
  };
  return new Promise(r => server.listen(port || 0, '127.0.0.1', () => r(server)));
}
module.exports = { start, listPhotos };
