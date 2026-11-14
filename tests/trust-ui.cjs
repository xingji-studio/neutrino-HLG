const { chromium } = require('playwright-core');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../src/HLG.Host/wwwroot');
const server = http.createServer((req,res)=>{
  const name=req.url==='/'?'index.html':decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '');
  const file=path.resolve(root,name);
  if(!file.startsWith(root+path.sep)){res.writeHead(403).end();return;}
  fs.readFile(file,(err,body)=>{if(err){res.writeHead(404).end();return;}res.writeHead(200,{'Content-Type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(body);});
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({executablePath:process.env.HLG_TEST_BROWSER||'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',headless:true});
  try {
    const origin=`http://127.0.0.1:${server.address().port}/`;
    const page=await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>{
      window.__presenceSockets=[];
      class MockWebSocket { static OPEN=1; constructor(url){this.url=url;this.readyState=1;window.__presenceSockets.push(this);setTimeout(()=>this.onopen?.(),0);} send(){} close(){this.readyState=3;this.onclose?.();} }
      window.WebSocket=MockWebSocket;
    });
    let session=false,storedToken=null,failTrust=true,restoreUnavailable=false,restores=0,trustCalls=[];
    await page.route('**/api/**',async route=>{
      const req=route.request(),url=new URL(req.url());let status=200,data={};
      if(url.pathname==='/api/pair'){
        if(req.postDataJSON().code!=='123456')status=401;
        else{session=true;data={ok:true};}
      } else if(url.pathname==='/api/trust/restore'){
        restores++;
        if(restoreUnavailable){status=503;data={detail:'记录暂不可用'};}
        else if(!storedToken||req.postDataJSON().token!==storedToken)status=401;
        else{session=true;data={ok:true};}
      } else if(!session)status=401;
      else if(url.pathname==='/api/trust'){
        const token=req.postDataJSON().token;trustCalls.push(token);
        if(failTrust){status=503;data={detail:'保存失败'};}
        else{storedToken=token;data={ok:true};}
      } else if(url.pathname==='/api/status')data={desktop:'C:\\Desktop',machine:'TEST'};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      await route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto(origin);await page.locator('#pair').waitFor({state:'visible'});
    await page.locator('#pair-code').fill('000000');await page.locator('#pair-form button').click();
    await page.waitForFunction(()=>document.getElementById('pair-error').textContent.includes('配对码错误'));
    await page.locator('#pair-code').fill('123456');await page.locator('#pair-form button').click();
    await page.locator('#trust-dialog').waitFor({state:'visible'});
    assert.equal(await page.locator('#pair').isHidden(),true);
    assert.match(await page.evaluate(()=>window.__presenceSockets[0].url),/\/api\/presence\/connect$/);
    await page.locator('#trust-accept').click();
    await page.waitForFunction(()=>document.getElementById('trust-error').textContent.includes('保存失败'));
    assert.equal(await page.locator('#trust-dialog').isVisible(),true);
    assert.match(trustCalls[0],/^[A-F0-9]{64}$/);
    failTrust=false;await page.locator('#trust-accept').click();
    await page.locator('#trust-dialog').waitFor({state:'hidden'});
    assert.equal(trustCalls.length,2);assert.equal(trustCalls[0],trustCalls[1]);
    assert.equal(await page.evaluate(()=>localStorage.getItem('hlg.trusted-device.v1')),storedToken);
    session=false;await page.reload();
    await page.waitForFunction(()=>document.querySelector('#connection span').textContent==='TEST');
    assert.equal(await page.locator('#pair').isHidden(),true);assert.equal(restores,1);
    restoreUnavailable=true;session=false;await page.reload();await page.locator('#pair').waitFor({state:'visible'});
    assert.equal(await page.evaluate(()=>localStorage.getItem('hlg.trusted-device.v1')),storedToken,'temporary error must preserve token');
    restoreUnavailable=false;session=false;await page.reload();
    await page.waitForFunction(()=>document.querySelector('#connection span').textContent==='TEST');
    storedToken=null;session=false;await page.reload();await page.locator('#pair').waitFor({state:'visible'});
    assert.equal(await page.evaluate(()=>localStorage.getItem('hlg.trusted-device.v1')),null,'revoked token must be discarded');
    await page.locator('#pair-code').fill('123456');await page.locator('#pair-form button').click();
    await page.locator('#trust-dialog').waitFor({state:'visible'});await page.locator('#trust-decline').click();
    await page.locator('#trust-dialog').waitFor({state:'hidden'});
    session=false;const before=restores;await page.reload();await page.locator('#pair').waitFor({state:'visible'});
    assert.equal(restores,before,'declining trust must not create a token');
    assert.deepEqual(errors,[]);
    console.log('Trust prompt, refusal, retry, restart restoration and revocation passed.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
