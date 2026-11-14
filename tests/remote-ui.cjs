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
    const ballResult=await page.evaluate(()=>{
      const floating=document.getElementById('remote-float'),ball=document.getElementById('remote-ball'),menu=document.getElementById('remote-menu');
      floating.style.left=(innerWidth-54)+'px';floating.style.top=(innerHeight-54)+'px';floating.style.bottom='auto';
      const before=ball.getBoundingClientRect(),x=before.left+before.width/2,y=before.top+before.height/2;
      ball.dispatchEvent(new PointerEvent('pointerdown',{pointerId:31,pointerType:'touch',button:0,clientX:x,clientY:y,bubbles:true}));
      ball.dispatchEvent(new PointerEvent('pointerup',{pointerId:31,pointerType:'touch',button:0,clientX:x,clientY:y,bubbles:true}));
      const hiddenBeforeClick=menu.classList.contains('hidden'),target=document.elementFromPoint(x,y);target.click();
      const after=ball.getBoundingClientRect(),bar=menu.getBoundingClientRect();
      return {hiddenBeforeClick,target:target.id,ballMoved:Math.hypot(before.left-after.left,before.top-after.top),menuVisible:!menu.classList.contains('hidden'),menuLeft:bar.left,menuRight:bar.right,viewport:innerWidth};
    });
    assert.equal(ballResult.hiddenBeforeClick,true,'menu must wait for the original click to avoid activating Back');
    assert.equal(ballResult.target,'remote-ball');assert.equal(ballResult.menuVisible,true);assert(ballResult.ballMoved<1,'floating ball must not move when the toolbar opens');
    assert(ballResult.menuLeft>=0&&ballResult.menuRight<=ballResult.viewport,'toolbar must remain visible near the right edge');
    assert.equal(await page.locator('#remote-view').isVisible(),true,'opening the toolbar must not navigate back');
    await page.locator('#remote-ball').click();
    const ballBounds=await page.locator('#remote-ball').boundingBox();
    await page.touchscreen.tap(ballBounds.x+ballBounds.width/2,ballBounds.y+ballBounds.height/2);
    assert.equal(await page.locator('#remote-menu').isVisible(),true,'a real touch opens the toolbar');
    assert.equal(await page.locator('#remote-view').isVisible(),true,'a real touch must not navigate back');
    await page.touchscreen.tap(ballBounds.x+ballBounds.width/2,ballBounds.y+ballBounds.height/2);
    assert.equal(await page.locator('#remote-menu').isHidden(),true,'a second touch closes the toolbar');
    const ballDrag=await page.evaluate(()=>{
      const ball=document.getElementById('remote-ball'),menu=document.getElementById('remote-menu'),r=ball.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
      const emit=(type,px)=>ball.dispatchEvent(new PointerEvent(type,{pointerId:32,pointerType:'touch',button:0,clientX:px,clientY:y,bubbles:true}));
      emit('pointerdown',x);emit('pointermove',x-50);emit('pointerup',x-50);ball.click();
      return {hidden:menu.classList.contains('hidden'),moved:r.left-ball.getBoundingClientRect().left};
    });
    assert.equal(ballDrag.hidden,true,'dragging the floating ball must not open the toolbar');
    assert(ballDrag.moved>30,'floating ball remains draggable');
    await page.evaluate(()=>{document.getElementById('remote-float').style.left=(innerWidth-54)+'px';});
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
    const movementAtOne=await page.evaluate(()=>{
      const t=document.getElementById('trackpad'),r=t.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2,start=window.__sent.length;
      for(const [type,dx] of [['pointerdown',0],['pointermove',100],['pointerup',100]])t.dispatchEvent(new PointerEvent(type,{pointerId:7,pointerType:'touch',clientX:x+dx,clientY:y,bubbles:true,button:0}));
      return window.__sent.slice(start).find(m=>m.type==='relative').dx;
    });
    assert.equal(movementAtOne,200,'original pointer speed stays unchanged at 1x zoom');
    const pinchResult=await page.evaluate(async () => {
      const t=document.querySelector('#remote-canvas'),r=t.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
      const emit=(type,id,px)=>t.dispatchEvent(new PointerEvent(type,{pointerId:id,pointerType:'touch',clientX:px,clientY:y,bubbles:true,button:0}));
      const before=window.__sent.filter(m=>m.type==='button').length;
      emit('pointerdown',1,x-50);emit('pointermove',1,x-38);
      await new Promise(resolve=>setTimeout(resolve,110));
      const beforeSecond=window.__sent.filter(m=>m.type==='button').length;
      emit('pointerdown',2,x+50);emit('pointermove',2,x+138);emit('pointerup',2,x+138);emit('pointerup',1,x-38);
      return {before,beforeSecond,after:window.__sent.filter(m=>m.type==='button').length};
    });
    assert.equal(pinchResult.beforeSecond,pinchResult.before,'first finger movement must not start a drag before the second finger joins');
    assert.equal(pinchResult.after,pinchResult.before,'pinch zoom must not send mouse selection events');
    assert.match(await page.locator('#remote-image').getAttribute('style'), /scale\(2\)/);
    assert.equal(await page.locator('#remote-cursor').evaluate(el=>el.classList.contains('is-edge')),true,'cursor leaving the zoomed viewport becomes an edge arrow');
    const singleFinger=await page.evaluate(async()=>{
      const t=document.getElementById('remote-canvas'),r=t.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
      const emit=(type,id,px)=>t.dispatchEvent(new PointerEvent(type,{pointerId:id,pointerType:'touch',clientX:px,clientY:y,bubbles:true,button:0}));
      const buttons=()=>window.__sent.filter(m=>m.type==='button').map(m=>m.down);
      let start=buttons().length;emit('pointerdown',10,x);emit('pointerup',10,x);const tap=buttons().slice(start);
      start=buttons().length;emit('pointerdown',11,x);emit('pointermove',11,x+20);await new Promise(resolve=>setTimeout(resolve,220));const held=buttons().slice(start);emit('pointerup',11,x+20);const drag=buttons().slice(start);
      start=buttons().length;emit('pointerdown',12,x);emit('pointermove',12,x+20);const beforeQuickRelease=buttons().slice(start);emit('pointerup',12,x+20);const quickDrag=buttons().slice(start);
      start=buttons().length;emit('pointerdown',13,x);emit('pointermove',13,x+20);emit('pointercancel',13,x+20);await new Promise(resolve=>setTimeout(resolve,200));const cancelled=buttons().slice(start);
      return {tap,held,drag,beforeQuickRelease,quickDrag,cancelled};
    });
    assert.deepEqual(singleFinger.tap,[true,false],'a single touch still clicks');
    assert.deepEqual(singleFinger.held,[true],'a held drag starts after the second-finger window');
    assert.deepEqual(singleFinger.drag,[true,false],'a held drag releases the mouse');
    assert.deepEqual(singleFinger.beforeQuickRelease,[],'a quick gesture waits for a second finger');
    assert.deepEqual(singleFinger.quickDrag,[true,false],'a quick single-finger swipe still drags');
    assert.deepEqual(singleFinger.cancelled,[],'a cancelled gesture must not click');
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
    const movementAtTwo=await page.evaluate(({x,y})=>{const t=document.querySelector('#trackpad'),start=window.__sent.length;for(const [type,dx] of [['pointerdown',0],['pointermove',100],['pointerup',100]])t.dispatchEvent(new PointerEvent(type,{pointerId:8,clientX:x+dx,clientY:y,bubbles:true,button:0}));return window.__sent.slice(start).find(m=>m.type==='relative').dx;},{x:p.x+p.width/2,y:p.y+p.height/2});
    assert(movementAtTwo>=114&&movementAtTwo<=115,'2x zoom retains slightly more pointer speed than inverse scaling');
    await page.evaluate(()=>{
      const t=document.querySelector('#remote-canvas'),r=t.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
      const emit=(type,id,px)=>t.dispatchEvent(new PointerEvent(type,{pointerId:id,pointerType:'touch',clientX:px,clientY:y,bubbles:true,button:0}));
      emit('pointerdown',21,x-40);emit('pointerdown',22,x+40);emit('pointermove',22,x+160);emit('pointerup',22,x+160);emit('pointerup',21,x-40);
    });
    assert.match(await page.locator('#remote-image').getAttribute('style'), /scale\(5\)/);
    const movementAtFive=await page.evaluate(({x,y})=>{const t=document.querySelector('#trackpad'),start=window.__sent.length;for(const [type,dx] of [['pointerdown',0],['pointermove',100],['pointerup',100]])t.dispatchEvent(new PointerEvent(type,{pointerId:8,clientX:x+dx,clientY:y,bubbles:true,button:0}));return window.__sent.slice(start).find(m=>m.type==='relative').dx;},{x:p.x+p.width/2,y:p.y+p.height/2});
    assert(movementAtFive>=63&&movementAtFive<=64,'5x zoom slows the pointer but stays faster than inverse scaling');
    assert(movementAtFive-40>movementAtTwo-100,'the speed improvement grows with zoom');
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
