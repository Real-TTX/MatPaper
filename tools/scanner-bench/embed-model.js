// Writes model.json (from learn2.js --emit) into the MODEL block of scanner-core.js.
//   node embed-model.js [model.json] [scanner-core.js]
const fs = require('fs');
const m = JSON.parse(fs.readFileSync(process.argv[2] || 'model.json', 'utf8'));
const f = process.argv[3] || require('path').join(__dirname, '..', '..', 'src', 'MatPaper', 'wwwroot', 'js', 'scanner-core.js');
let s = fs.readFileSync(f, 'utf8');
const a = s.indexOf('    // MODEL-BEGIN'), b = s.indexOf('    // MODEL-END');
if (a < 0 || b < 0) throw new Error('markers');
const num = x => +x.toFixed(4);
const block = [
  '    // MODEL-BEGIN',
  '    var MODEL = {',
  '        names: ' + JSON.stringify(m.names) + ',',
  '        w: ' + JSON.stringify(m.w.map(num)) + ',',
  '        b: ' + num(m.b),
  '    };',
  ''
].join('\n');
s = s.slice(0, a) + block + s.slice(b);
fs.writeFileSync(f, s);
console.log('embedded', m.names.length, 'weights into', f);
