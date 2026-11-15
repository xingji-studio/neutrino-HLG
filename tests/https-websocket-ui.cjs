const { chromium } = require('playwright-core');
const assert = require('node:assert/strict');

const [origin, pairingCode] = process.argv.slice(2);
if (!origin || !pairingCode) throw new Error('HTTPS origin and pairing code are required.');

(async () => {
  const edge = process.env.HLG_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  const browser = await chromium.launch({ executablePath: edge, headless: true });
  try {
    const context = await browser.newContext({
      ignoreHTTPSErrors: true, isMobile: true, hasTouch: true,
      viewport: { width: 390, height: 844 },
      userAgent: 'Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36'
    });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    let redirected = false;
    page.on('response', response => { if (response.url() === origin.replace(/^https:/, 'http:') + '/' && response.status() === 307) redirected = true; });
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    const sockets = new Map(), handshakes = [];
    let screenFrames = 0;
    cdp.on('Network.webSocketCreated', event => sockets.set(event.requestId, event.url));
    cdp.on('Network.webSocketHandshakeResponseReceived', event => handshakes.push({ url: sockets.get(event.requestId), status: event.response.status }));
    cdp.on('Network.webSocketFrameReceived', event => { if (sockets.get(event.requestId)?.includes('/api/screen/connect')) screenFrames++; });

    await page.goto(origin.replace(/^https:/, 'http:') + '/');
    assert(redirected && page.url() === origin + '/', 'HTTP page must automatically redirect to HTTPS on the same port');
    await page.locator('#pair').waitFor({ state: 'visible' });
    await page.locator('#pair-code').fill(pairingCode);
    await page.locator('#pair-form button.primary').click();
    await page.locator('#trust-dialog').waitFor({ state: 'visible' });
    await page.locator('#trust-decline').click();
    await page.waitForFunction(() => document.querySelector('#connection span').textContent === 'HLG TEST');
    await page.waitForFunction(async () => {
      const response = await fetch('/api/test/devices');
      return response.ok && (await response.json()).some(device => device.online && device.name === 'Android 设备');
    }, null, { timeout: 12000 });
    await page.waitForTimeout(500);
    assert(await page.evaluate(async () => (await (await fetch('/api/test/devices')).json()).some(device => device.online)), 'browser device must remain online');

    await page.getByRole('button', { name: '当前画面' }).click();
    await page.getByRole('button', { name: /全部画面/ }).click();
    await page.waitForFunction(() => document.getElementById('remote-image').naturalWidth > 0, null, { timeout: 15000 });
    assert(await page.evaluate(async () => (await (await fetch('/api/test/devices')).json()).some(device => device.online && device.screen)), 'browser screen must be tracked');
    const framesBefore = screenFrames;
    await page.waitForTimeout(11000);
    assert(screenFrames > framesBefore, 'screen must keep streaming after the protocol-detection timeout');
    assert(await page.evaluate(async () => (await (await fetch('/api/test/devices')).json()).some(device => device.online && device.screen)), 'device must remain connected');
    const protocols = await page.evaluate(() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/api/status')).map(entry => entry.nextHopProtocol));
    assert(protocols.includes('h2'), 'the HTTPS page must exercise HTTP/2');
    assert(handshakes.some(item => item.url?.includes('/api/presence/connect') && item.status === 200), 'presence must use an HTTP/2 WebSocket');
    assert(handshakes.some(item => item.url?.includes('/api/screen/connect') && item.status === 200), 'screen must use an HTTP/2 WebSocket');
    assert.deepEqual(errors, []);
    await context.close();
    console.log('Browser HTTP redirect, HTTPS pairing, HTTP/2 presence and live screen passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
