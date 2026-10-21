const hid = require('node-hid');
const hex = (b) => Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join(' ');
const all = hid.devices();
const t = all.find((d) => d.vendorId === 0x391d && d.productId === 0x2a04 && d.usagePage === 0xff60 && d.usage === 0x61);
const dev = new hid.HID(t.path);

function validFrame(main, sub) {
  const b = new Array(64).fill(0);
  b[0] = 9; b[1] = main; b[2] = sub;
  b[63] = (255 - (b.slice(0, 63).reduce((a, x) => a + x, 0) % 256)) & 0xff;
  return b;
}

const queries = [[0x0d, 0x01], [0x01, 0x01]];
const deadline = Date.now() + 20000;
let sent = 0, replies = 0;
const seen = new Map();

while (Date.now() < deadline) {
  const q = queries[sent % queries.length];
  sent++;
  try { dev.write(validFrame(q[0], q[1])); } catch (e) { console.log('write failed ' + e.message); break; }
  for (let i = 0; i < 4; i++) {
    const r = dev.readTimeout(120);
    if (!r || r.length === 0) break;
    replies++;
    const key = hex(r);
    if (!seen.has(key)) { seen.set(key, true); console.log('reply: ' + hex(r.slice(0, 16)) + ' … ' + hex(r.slice(60))); }
  }
}
console.log('sent=' + sent + ' replies=' + replies + ' distinct=' + seen.size);
process.exit(0);
