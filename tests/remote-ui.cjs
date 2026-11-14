const { chromium } = require('playwright-core');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

const root = path.resolve(__dirname, '../src/HLG.Host/wwwroot');
const server = http.createServer((req, res) => {
  const name = req.url === '/' ? 'index.html' : decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '');
  const file = path.resolve(root, name);
  if (!file.startsWith(root + path.sep)) { res.writeHead(403).end(); return; }
  const ext = path.extname(file);
  const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' }[ext] || 'application/octet-stream';
  fs.readFile(file, (err, body) => { if (err) { res.writeHead(404).end(); return; } res.writeHead(200, { 'Content-Type': mime }); res.end(body); });
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const edge = process.env.HLG_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  const browser = await chromium.launch({ executablePath: edge, headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 844, height: 390 }, isMobile: true, hasTouch: true });
    const errors = [];
    page.on('pageerror', error => errors.push(error.stack || error.message));
    await page.addInitScript(() => {
      window.__sent = []; window.__sockets = [];
      class MockWebSocket {
        static OPEN=1;
        constructor(url){this.url=url;this.readyState=1;this.bufferedAmount=0;window.__sockets.push(this);setTimeout(()=>this.onopen?.(),0);}
        send(data){window.__sent.push(JSON.parse(data));}
        close(){this.readyState=3;this.onclose?.();}
      }
      window.WebSocket=MockWebSocket;
      Element.prototype.setPointerCapture=function(){};
    });
    let activations=0,denyActivation=true,locked=false;
    await page.route('**/api/**', async route => {
      const url = new URL(route.request().url());
      let data = {};
      if (url.pathname === '/api/status') data = { desktop:'C:\\Users\\Test\\Desktop', machine:'TEST', locked, unlockAvailable:true };
      else if (url.pathname === '/api/apps') data = [{name:'有图标应用',path:'C:\\Start Menu\\One.lnk'},{name:'缺少图标应用',path:'C:\\Start Menu\\Two.lnk'}];
      else if (url.pathname === '/api/apps/icon') {
        if(url.searchParams.get('path').endsWith('One.lnk'))await route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="red"/></svg>'});
        else await route.fulfill({status:404});
        return;
      }
      else if (url.pathname === '/api/neutrino/models') data = { providers:[] };
      else if (url.pathname === '/api/terminal/shells') data = [{ id:'cmd', name:'CMD' }];
      else if (url.pathname === '/api/windows') data = [{ id:'100', processId:1, title:'测试窗口', application:'test' }];
      else if (url.pathname === '/api/windows/activate') {
        assert.deepEqual(route.request().postDataJSON(),{id:'100',processId:1});activations++;
        if(denyActivation){await route.fulfill({status:409,contentType:'application/json',body:JSON.stringify({error:'激活失败'})});return;}
      }
      else if (url.pathname === '/api/clipboard') data = { text:'示例文字' };
      await route.fulfill({ status:200, contentType:'application/json', body:JSON.stringify(data) });
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    assert.equal(await page.evaluate(() => typeof window.Terminal), 'function');
    const appWithIcon=page.getByRole('button',{name:'有图标应用'}),appWithoutIcon=page.getByRole('button',{name:'缺少图标应用'});
    await appWithIcon.locator('.app-icon-image.is-loaded').waitFor();
    assert.equal(await appWithIcon.locator('.app-icon').textContent(),'');
    await appWithoutIcon.scrollIntoViewIfNeeded();
    await appWithoutIcon.locator('.app-icon-image').waitFor({state:'detached'});
    assert.equal(await appWithoutIcon.locator('.app-icon').textContent(),'▦');
    await page.getByRole('button', { name:'当前画面' }).click();
    await page.getByRole('button', { name:/测试窗口/ }).click();
    await page.waitForFunction(()=>document.getElementById('toast').textContent.includes('激活失败'));
    assert.equal(await page.locator('#remote-view').isHidden(),true);
    denyActivation=false;
    await page.getByRole('button', { name:/测试窗口/ }).click();
    await page.locator('#remote-view').waitFor({ state:'visible' });
    assert.equal(activations,2);
    assert.match(await page.evaluate(()=>window.__sockets.at(-1).url),/screen\/connect\?id=100&pid=1$/);
    locked=true;
    await page.waitForFunction(()=>window.__sockets.at(-1).url.endsWith('/api/screen/secure/connect'));
    assert.equal(await page.locator('#remote-lock-state').isVisible(),true);
    assert.equal(await page.locator('#remote-clipboard').isDisabled(),true);
    const beforeManual=await page.evaluate(()=>window.__sent.filter(m=>m.type==='button').length);
    await page.locator('#remote-manual').click();
    assert(await page.evaluate(()=>window.__sent.some(m=>m.type==='manual')));
    assert.equal(await page.evaluate(()=>window.__sent.filter(m=>m.type==='button').length),beforeManual,'manual control must not click the remote login screen');
    assert.equal(await page.locator('#remote-keyboard').isVisible(),true);
    await page.locator('#remote-ball').click();
    await page.locator('#remote-special-toggle').click();
    await page.locator('#remote-special').getByRole('button',{name:'Ctrl+Alt+Del'}).click();
    assert(await page.evaluate(()=>window.__sent.some(m=>m.type==='sas')));
    locked=false;
    await page.evaluate(()=>window.__sockets.at(-1).onmessage({data:JSON.stringify({type:'desktop',locked:false})}));
    await page.waitForFunction(()=>window.__sockets.at(-1).url.endsWith('/api/screen/connect?id=100&pid=1'));
    assert.equal(await page.locator('#remote-lock-state').isHidden(),true);
    await page.evaluate(async () => {
      const c=document.createElement('canvas');c.width=80;c.height=60;c.getContext('2d').fillRect(0,0,80,60);
      const blob=await new Promise(resolve=>c.toBlob(resolve,'image/jpeg'));
      window.__sockets.at(-1).onmessage({data:await blob.arrayBuffer()});
    });
    await page.waitForFunction(()=>document.getElementById('remote-image').naturalWidth===80);
    const smallFrame=await page.locator('#remote-image').boundingBox();
    const screenFrame=await page.locator('#remote-canvas').boundingBox();
    assert(smallFrame.width>80,'small selected window fills the available display area');
    assert(Math.abs(smallFrame.width/smallFrame.height-80/60)<0.01,'selected window aspect ratio preserved');
    assert(smallFrame.width<=screenFrame.width+1&&smallFrame.height<=screenFrame.height+1);
    await page.evaluate(()=>window.__sockets.at(-1).onmessage({data:JSON.stringify({type:'frame',width:80,height:60,cursor:{x:.25,y:.5}})}));
    assert.equal(await page.locator('#remote-cursor').isVisible(),true);
    assert.equal(await page.locator('#remote-cursor').evaluate(el=>el.classList.contains('is-edge')),false);
    const pointer=await page.locator('#remote-cursor').boundingBox();
    assert(Math.abs(pointer.x-(smallFrame.x+smallFrame.width*.25))<3,'cursor follows position in selected window');
    await page.evaluate(()=>window.__sockets.at(-1).onmessage({data:JSON.stringify({type:'frame',width:80,height:60,cursor:{x:2,y:.5}})}));
    assert.equal(await page.locator('#remote-cursor').evaluate(el=>el.classList.contains('is-edge')),true);
    const edge=await page.locator('#remote-cursor').boundingBox();
    assert(Math.abs(edge.x+edge.width/2-(screenFrame.x+screenFrame.width-12))<3,'out-of-frame cursor points from screen edge');
    assert.match(await page.locator('#remote-cursor').getAttribute('style'),/rotate\(0deg\)/);
    await page.evaluate(async () => {
      const c=document.createElement('canvas');c.width=120;c.height=300;c.getContext('2d').fillRect(0,0,120,300);
      const blob=await new Promise(resolve=>c.toBlob(resolve,'image/jpeg'));
      window.__sockets.at(-1).onmessage({data:await blob.arrayBuffer()});
    });
    await page.waitForFunction(()=>document.getElementById('remote-image').naturalWidth===120);
    const resizedFrame=await page.locator('#remote-image').boundingBox();
    assert(Math.abs(resizedFrame.width/resizedFrame.height-120/300)<0.01,'window resize updates frame aspect ratio');
    await page.evaluate(()=>history.back());
    await page.locator('#remote-view').waitFor({state:'hidden'});
    await page.getByRole('button', { name:/全部画面/ }).click();
    await page.locator('#remote-view').waitFor({ state:'visible' });
    await page.evaluate(async () => {
      const c=document.createElement('canvas');c.width=800;c.height=600;c.getContext('2d').fillRect(0,0,800,600);
      const blob=await new Promise(resolve=>c.toBlob(resolve,'image/jpeg'));
      window.__sockets.at(-1).onmessage({data:await blob.arrayBuffer()});
    });
    await page.waitForFunction(() => document.getElementById('remote-image').naturalWidth === 800);
    const beforeBlankTap=await page.evaluate(() => window.__sent.filter(m=>m.type==='button').length);
    await page.evaluate(() => {
      const t=document.querySelector('#remote-canvas'),r=t.getBoundingClientRect(),x=r.left+1,y=r.top+r.height/2;
      for(const type of ['pointerdown','pointerup'])t.dispatchEvent(new PointerEvent(type,{pointerId:9,clientX:x,clientY:y,bubbles:true,button:0}));
    });
    assert.equal(await page.evaluate(() => window.__sent.filter(m=>m.type==='button').length), beforeBlankTap);
    await page.evaluate(()=>window.__sockets.at(-1).onmessage({data:JSON.stringify({type:'frame',width:800,height:600,cursor:{x:.9,y:.5}})}));
    assert.equal(await page.locator('#remote-cursor').evaluate(el=>el.classList.contains('is-edge')),false);
    await page.evaluate(() => {
      const t=document.querySelector('#remote-canvas'),r=t.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
      const emit=(type,id,px)=>t.dispatchEvent(new PointerEvent(type,{pointerId:id,clientX:px,clientY:y,bubbles:true,button:0}));
      emit('pointerdown',1,x-50);emit('pointerdown',2,x+50);emit('pointermove',2,x+150);emit('pointerup',2,x+150);emit('pointerup',1,x-50);
    });
    assert.match(await page.locator('#remote-image').getAttribute('style'), /scale\(2\)/);
    assert.equal(await page.locator('#remote-cursor').evaluate(el=>el.classList.contains('is-edge')),true,'cursor leaving the zoomed viewport becomes an edge arrow');
    await page.locator('#remote-ball').click();
    await page.locator('#remote-keyboard-toggle').click();
    assert.equal(await page.locator('#remote-keyboard').isVisible(), true);
    await page.locator('#remote-special-toggle').click();
    await page.locator('#remote-special').getByRole('button', { name:'Ctrl' }).click();
    await page.locator('#remote-keyboard').getByRole('button', { name:'a', exact:true }).click();
    assert(await page.evaluate(() => window.__sent.some(m=>m.type==='key' && m.key==='a' && m.modifiers.includes('Ctrl'))));
    assert.equal(await page.locator('#remote-keyboard').getByRole('button', { name:'开启大写' }).locator('svg').count(),1);
    await page.locator('#remote-keyboard').getByRole('button', { name:'开启大写' }).click();
    assert.equal(await page.locator('#remote-keyboard').getByRole('button', { name:'关闭大写' }).locator('svg').count(),1);
    await page.locator('#remote-keyboard').getByRole('button', { name:'手机输入法' }).click();
    await page.locator('#remote-text').fill('中文测试');
    await page.locator('#remote-text-send').click();
    assert(await page.evaluate(() => window.__sent.some(m=>m.type==='text' && m.text==='中文测试')));
    await page.locator('#remote-keyboard-toggle').click();
    assert.equal(await page.locator('#trackpad').isVisible(), true);
    const p=await page.locator('#trackpad').boundingBox();
    await page.evaluate(({x,y})=>{const t=document.querySelector('#trackpad');for(const [type,dx] of [['pointerdown',0],['pointermove',10],['pointerup',10]])t.dispatchEvent(new PointerEvent(type,{pointerId:8,clientX:x+dx,clientY:y,bubbles:true,button:0}));},{x:p.x+p.width/2,y:p.y+p.height/2});
    assert(await page.evaluate(() => window.__sent.some(m=>m.type==='relative' && m.dx===10)));
    await page.locator('#remote-clipboard').click();
    await page.locator('#clipboard-read').click();
    await page.waitForFunction(() => document.getElementById('clipboard-text').value === '示例文字');
    assert.equal(await page.locator('#clipboard-text').inputValue(), '示例文字');
    await page.locator('#clipboard-close').click();
    await page.evaluate(()=>history.back());await page.locator('#remote-special').waitFor({state:'hidden'});
    await page.evaluate(()=>history.back());await page.locator('#remote-menu').waitFor({state:'hidden'});
    await page.evaluate(()=>history.back());await page.locator('#remote-view').waitFor({state:'hidden'});
    assert.equal(await page.locator('#remote-view').isHidden(), true);
    assert.equal(await page.locator('#back svg').count(),1);
    await page.locator('#back').click();
    locked=true;
    await page.getByRole('button', { name:'当前画面' }).click();
    await page.waitForFunction(()=>window.__sockets.at(-1).url.endsWith('/api/screen/secure/connect'));
    await page.locator('#remote-ball').click();await page.locator('#remote-back').click();
    await page.getByRole('button',{name:/登录画面/}).waitFor();
    assert.equal(await page.locator('#remote-view').isHidden(),true);
    locked=false;
    await page.locator('#back').click();
    await page.getByRole('button', { name:'终端' }).click();
    for(const [label,sequence] of [['向上','\x1b[A'],['向下','\x1b[B'],['向左','\x1b[D'],['向右','\x1b[C']]) {
      const button=page.getByRole('button',{name:label});
      await button.waitFor({state:'visible'});
      assert.equal(await button.locator('svg').count(),1);
      await button.click();
      assert(await page.evaluate(data=>window.__sent.some(m=>m.type==='input'&&m.data===data),sequence));
    }
    assert.deepEqual(errors, []);
    console.log('手机画面、键盘组合键、触控板和剪切板界面测试通过。');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode=1; });
