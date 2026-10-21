const hid = require('node-hid');
const hex = (b) => Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join(' ');
const all = hid.devices();
const t = all.find((d) => d.vendorId === 0x391d && d.productId === 0x2a04 && d.usagePage === 0xff60 && d.usage === 0x61);
const dev = new hid.HID(t.path);

const sum = (arr) => arr.reduce((a, b) => a + b, 0);
function build(bytes, cksumAt) {
  const buf = new Array(64).fill(0);
  bytes.forEach((b, i) => { if (i < 64) buf[i] = b; });
  if (cksumAt >= 0) buf[cksumAt] = (255 - (sum(buf.slice(0, cksumAt)) % 256)) & 0xff;
  return buf;
}

const variants = [
  { name: 'A flags=0x20 cksum@63', buf: build([9, 0x0d, 0x01, 0x20, 0x00], 63) },
  { name: 'B flags=0x00 cksum@63', buf: build([9, 0x0d, 0x01, 0x00, 0x00], 63) },
  { name: 'C 14B frame cksum@12', buf: build([9, 0x0d, 0x01, 0x20, 0x00], 12) },
  { name: 'D init 0xFD cksum@63', buf: build([9, 0xfd, 0x00, 0x20, 0x00], 63) },
  { name: 'E sub=0x03 cksum@63', buf: build([9, 0x0d, 0x03, 0x20, 0x00], 63) },
  { name: 'F no id, flags=0x20', buf: build([0x0d, 0x01, 0x20, 0x00], 63) },
];

for (const v of variants) {
  let out = [];
  try {
    dev.write(v.buf);
    for (let i = 0; i < 5; i++) {
      const r = dev.readTimeout(300);
      if (!r || r.length === 0) break;
      out.push(hex(r).slice(0, 60) + (r.length > 20 ? ' …' : ''));
    }
  } catch (e) { console.log(v.name + '  write failed: ' + e.message); continue; }
  console.log('== ' + v.name + '  sent: ' + hex(v.buf.slice(0, 6)) + '…cksum=' + hex([v.buf[63]]));
  if (out.length === 0) console.log('   (no reply)');
  out.forEach((line) => console.log('   ' + line));
}
process.exit(0);
