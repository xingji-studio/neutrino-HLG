const { chromium } = require('playwright-core');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname,'../src/HLG.Host/wwwroot');
const server=http.createServer((req,res)=>{
  const name=req.url==='/'?'index.html':decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '');
  const file=path.resolve(root,name);
  if(!file.startsWith(root+path.sep)){res.writeHead(403).end();return;}
  fs.readFile(file,(err,body)=>{if(err){res.writeHead(404).end();return;}res.writeHead(200,{'Content-Type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[path.extname(file)]||'application/octet-stream'});res.end(body);});
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({executablePath:process.env.HLG_TEST_BROWSER||'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>{
      class MockWebSocket { static OPEN=1; constructor(url){this.url=url;this.readyState=1;this.bufferedAmount=0;window.__socket=this;setTimeout(()=>this.onopen?.(),0);}send(){}close(){this.readyState=3;this.onclose?.();} }
      window.WebSocket=MockWebSocket;
    });
    const first={name:'普通应用',id:'shortcut-id',path:'C:\\Menu\\one.lnk',kind:'shortcut'};
    const second={name:'商店应用',id:'shell-id',path:null,kind:'shell'};
    let opened=false,activate=0;
    await page.route('**/api/**',async route=>{
      const req=route.request(),url=new URL(req.url());let data={};
      if(url.pathname==='/api/status')data={desktop:'C:\\Desktop',machine:'TEST'};
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      else if(url.pathname==='/api/apps')data=opened?[second,first]:[first,second];
      else if(url.pathname==='/api/apps/icon'){
        if(!['shortcut-id','shell-id'].includes(url.searchParams.get('id')))throw new Error('unexpected app icon ID');
        await route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect width="20" height="20" fill="red"/></svg>'});return;
      } else if(url.pathname==='/api/apps/open'){
        assert.deepEqual(req.postDataJSON(),{id:'shell-id'});opened=true;data={ok:true,processId:null};
      } else if(url.pathname==='/api/windows')data=opened?[{id:'42',processId:12,title:'商店应用',application:'store-app'}]:[];
      else if(url.pathname==='/api/windows/activate'){assert.deepEqual(req.postDataJSON(),{id:'42',processId:12});activate++;data={ok:true};}
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.getByRole('button',{name:'商店应用'}).locator('.app-icon-image.is-loaded').waitFor();
    let labels=await page.locator('#grid .app-name').allTextContents();
    assert.deepEqual(labels,['文件浏览','终端','任务列表','当前画面','普通应用','商店应用']);
    await page.getByRole('button',{name:'商店应用'}).click();
    await page.locator('#remote-view').waitFor({state:'visible'});
    assert.equal(activate,1);assert.match(await page.evaluate(()=>window.__socket.url),/screen\/connect\?id=42&pid=12$/);
    await page.waitForFunction(()=>[...document.querySelectorAll('#grid .app-name')].map(x=>x.textContent).join(',').endsWith('商店应用,普通应用'));
    labels=await page.locator('#grid .app-name').allTextContents();
    assert.deepEqual(labels,['文件浏览','终端','任务列表','当前画面','商店应用','普通应用']);
    assert.deepEqual(errors,[]);
    console.log('App icons, shell launch and refreshed frequency order passed.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
