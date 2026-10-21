const path = require('path');
const hid = require('node-hid');

const REPORT_ID = 9;
const TOTAL = 64;
const hex = (b) => Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join(' ');

const all = hid.devices();
const target = all.find((d) => d.vendorId === 0x391d && d.productId === 0x2a04 && d.usagePage === 0xff60 && d.usage === 0x61)
  || all.find((d) => d.vendorId === 0x391d && d.productId === 0x2a04);
console.log('real device:', target.path);
const realDevice = new hid.HID(target.path);

const fake = {
  vendorId: target.vendorId,
  productId: target.productId,
  productName: target.product,
  opened: true,
  listeners: [],
  collections: [{
    usagePage: 0xff60, usage: 0x61,
    inputReports: [{ reportId: REPORT_ID, items: [{ reportId: REPORT_ID, reportCount: TOTAL - 1, reportSize: 8 }] }],
    outputReports: [{ reportId: REPORT_ID, items: [{ reportId: REPORT_ID, reportCount: TOTAL - 1, reportSize: 8 }] }],
    featureReports: [],
  }],
  addEventListener(type, handler) { this.listeners.push(handler); },
  removeEventListener() {},
  async open() { this.opened = true; },
  async close() {},
  async sendReport(reportId, data) {
    const payload = Array.from(data);
    console.log('  >> sendReport(id=' + reportId + ', len=' + payload.length + ') : ' + hex(payload));
    try { realDevice.write([REPORT_ID].concat(payload.slice(0, TOTAL - 1))); }
    catch (e) { console.log('  write failed:', e.message); }
    for (let attempt = 0; attempt < 2; attempt++) {
      const reply = realDevice.readTimeout(400);
      if (!reply || reply.length === 0) break;
      console.log('  << input(len=' + reply.length + ') : ' + hex(reply));
      const event = { reportId: reply[0], data: new DataView(Uint8Array.from(reply.slice(1)).buffer) };
      this.listeners.forEach((handler) => { try { handler(event); } catch (e) { console.log('  handler error:', e.message); } });
    }
  },
  async sendFeatureReport(reportId, data) { console.log('  >> sendFeatureReport(id=' + reportId + ') : ' + hex(data)); },
  async receiveFeatureReport() { return new DataView(new ArrayBuffer(TOTAL)); },
};

Object.defineProperty(globalThis, 'navigator', {
  configurable: true,
  value: { hid: { getDevices: async () => [fake], requestDevice: async () => [fake], addEventListener() {}, removeEventListener() {} } },
});
globalThis.window = globalThis;

const sdkPath = path.join(__dirname, 'node_modules', '@sparklinkplayjoy', 'sdk-keyboard-v2', 'dist', 'cjs', 'index.cjs');
const Keyboard = require(sdkPath).XDKeyboardV2;
const keyboard = new Keyboard({ configs: [{ vendorId: target.vendorId, productId: target.productId, usagePage: 0xff60, usage: 0x61 }], usage: 0x61, usagePage: 0xff60 });

(async () => {
  const devices = await keyboard.getDevices();
  console.log('devices found:', devices.length);
  const initResult = await keyboard.init(devices[0].id);
  console.log('init:', JSON.stringify(initResult));

  for (const method of ['getBatteryStatus', 'getThreeModeBasicInfo', 'getDeviceInfo', 'getProtocolVersion', 'getDeviceFeature']) {
    if (typeof keyboard[method] !== 'function') { console.log(method + ': 不存在'); continue; }
    try { console.log(method + ' ->', JSON.stringify(await keyboard[method]())); }
    catch (e) { console.log(method + ' 失败:', e.message); }
  }
})().then(() => process.exit(0)).catch((e) => { console.error('FATAL', e && e.message); process.exit(1); });
