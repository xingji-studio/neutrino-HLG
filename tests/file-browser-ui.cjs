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
  fs.readFile(file, (error, body) => { if (error) { res.writeHead(404).end(); return; } res.writeHead(200, { 'Content-Type': { '.html':'text/html', '.js':'text/javascript', '.css':'text/css' }[path.extname(file)] || 'application/octet-stream' }); res.end(body); });
});

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ executablePath:process.env.HLG_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', headless:true });
  try {
    const page = await browser.newPage({ viewport:{ width:390, height:844 }, isMobile:true, hasTouch:true, acceptDownloads:true });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.__phoneCopies=[]; window.__failCopy=false;
      Object.defineProperty(navigator, 'clipboard', { configurable:true, value:{ writeText:async text => { if(window.__failCopy) throw new Error('denied'); window.__phoneCopies.push(text); } } });
      document.execCommand=()=>false;
      class MockWebSocket { static OPEN=1; constructor(){this.readyState=1;setTimeout(()=>this.onopen?.(),0);} send(){} close(){this.readyState=3;this.onclose?.();} }
      window.WebSocket=MockWebSocket;
    });
    const dirs = {
      'C:\\': [ {name:'报告.txt',path:'C:\\报告.txt',directory:false}, {name:'源文件夹',path:'C:\\源文件夹',directory:true}, {name:'空文件夹',path:'C:\\空文件夹',directory:true} ],
      'C:\\源文件夹': [ {name:'嵌套.txt',path:'C:\\源文件夹\\嵌套.txt',directory:false} ],
      'C:\\空文件夹': []
    };
    const requests=[];
    await page.route('**/api/**', async route => {
      const req=route.request(),url=new URL(req.url());let data={};
      if(url.pathname==='/api/status')data={desktop:'C:\\',machine:'TEST'};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      else if(url.pathname==='/api/fs'){
        const current=url.searchParams.get('path');
        data=current?{path:current,parent:current==='C:\\'?null:'C:\\',entries:dirs[current]||[]}:{roots:[{name:'C:\\',ready:true,type:'Fixed'}]};
      } else if(url.pathname==='/api/fs/paste'){
        const request=req.postDataJSON();requests.push(request);const name=request.source.split('\\').at(-1),target=request.directory+'\\'+name;
        if((dirs[request.directory]||[]).some(item=>item.name===name)){await route.fulfill({status:409,contentType:'application/json',body:JSON.stringify({error:'目标目录中已存在同名文件或文件夹。'})});return;}
        const original=Object.values(dirs).flat().find(item=>item.path===request.source);
        if(request.operation==='cut')for(const key of Object.keys(dirs))dirs[key]=dirs[key].filter(item=>item.path!==request.source);
        dirs[request.directory].push({name,path:target,directory:!!original?.directory});data={path:target,operation:request.operation};
      } else if(url.pathname==='/api/fs/download'){
        assert.equal(url.searchParams.get('path'),'C:\\报告.txt');
        await route.fulfill({status:200,headers:{'Content-Type':'application/octet-stream','Content-Disposition':"attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A.txt"},body:'test file'});return;
      }
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
    });
    const hold=async item=>{
      await item.dispatchEvent('pointerdown',{button:0,pointerId:7,pointerType:'touch',clientX:100,clientY:200});
      await page.waitForTimeout(650);
      await item.dispatchEvent('pointerup',{button:0,pointerId:7,pointerType:'touch',clientX:100,clientY:200});
      await item.dispatchEvent('click');
      await page.locator('#file-menu').waitFor({state:'visible'});
    };
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.getByRole('button',{name:'文件浏览'}).click();await page.getByText('C:\\',{exact:true}).click();
    let report=page.locator('.row').filter({hasText:'报告.txt'}),folder=page.locator('.row').filter({hasText:'源文件夹'});
    const touch=await page.context().newCDPSession(page),bounds=await report.boundingBox();
    const point={x:bounds.x+bounds.width/2,y:bounds.y+bounds.height/2};
    await touch.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[point]});
    await page.waitForTimeout(700);
    assert.equal(await page.locator('#file-menu').isVisible(),true,'the menu opens while the touch is held');
    const hit=await page.evaluate(({x,y})=>{
      const card=document.querySelector('.file-menu-card'),action=document.getElementById('file-menu-cut'),r=action.getBoundingClientRect();
      card.style.transform=`translate(${x-r.left-r.width/2}px,${y-r.top-r.height/2}px)`;
      return document.elementFromPoint(x,y)?.closest('button')?.id;
    },point);
    assert.equal(hit,'file-menu-cut','exercise a menu action appearing under the opening touch');
    await touch.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
    assert.equal(await page.locator('#file-menu').isVisible(),true,'releasing a real long-press must not close the menu');
    assert.equal(await page.evaluate(()=>sessionStorage.getItem('hlg.file-clipboard.v1')),null,'releasing a long-press must not trigger an action beneath the touch');
    await page.evaluate(()=>{document.querySelector('.file-menu-card').style.transform='';});
    await page.locator('#file-menu-close').tap();await page.locator('#file-menu').waitFor({state:'hidden'});await touch.detach();
    await hold(report);
    assert.equal(await page.locator('#file-menu-paste').isDisabled(),true);
    assert.equal(await page.locator('#file-menu-download').isVisible(),true);
    await page.locator('#file-menu-copy-path').click();
    await page.waitForFunction(()=>window.__phoneCopies.length===1);
    assert.equal(await page.evaluate(()=>window.__phoneCopies[0]),'C:\\报告.txt');
    await hold(folder);
    assert.equal(await page.locator('#file-menu-copy-path').textContent(),'复制文件夹路径');
    assert.equal(await page.locator('#file-menu-download').isHidden(),true);
    await page.locator('#file-menu-copy-directory').click();
    await page.waitForFunction(()=>window.__phoneCopies.length===2);
    assert.equal(await page.evaluate(()=>window.__phoneCopies[1]),'C:\\');
    await hold(report);
    const download=page.waitForEvent('download');await page.locator('#file-menu-download').click();
    assert.equal((await download).suggestedFilename(),'报告.txt');
    await hold(report);await page.locator('#file-menu-copy').click();
    assert.equal(requests.length,0,'copy must only set the simulated clipboard');
    await page.reload();await page.getByRole('button',{name:'文件浏览'}).click();await page.getByText('C:\\',{exact:true}).click();
    await page.getByText('空文件夹',{exact:true}).click();
    const paste=page.getByRole('button',{name:'粘贴至当前目录',exact:true});
    assert.equal(await paste.isEnabled(),true,'paste must be available in an empty folder after reload');
    await paste.click();await page.getByText('报告.txt',{exact:true}).waitFor();
    assert.deepEqual(requests[0],{source:'C:\\报告.txt',directory:'C:\\空文件夹',operation:'copy'});
    assert.equal(await paste.isEnabled(),true,'copy stays in the simulated clipboard');
    await hold(page.locator('.row').filter({hasText:'报告.txt'}));await page.locator('#file-menu-paste').click();
    await page.waitForFunction(()=>document.getElementById('toast').textContent.includes('已存在同名'));
    await page.getByRole('button',{name:'返回',exact:true}).click();
    folder=page.locator('.row').filter({hasText:'源文件夹'});await hold(folder);await page.locator('#file-menu-cut').click();
    await page.getByText('空文件夹',{exact:true}).click();await hold(page.locator('.row').filter({hasText:'报告.txt'}));await page.locator('#file-menu-paste').click();
    await page.getByText('源文件夹',{exact:true}).waitFor();
    assert.deepEqual(requests.at(-1),{source:'C:\\源文件夹',directory:'C:\\空文件夹',operation:'cut'});
    assert.equal(await page.getByRole('button',{name:'粘贴至当前目录',exact:true}).isDisabled(),true);
    assert.equal(await page.evaluate(()=>sessionStorage.getItem('hlg.file-clipboard.v1')),null,'cut clipboard must clear after a successful move');
    await page.evaluate(()=>{window.__failCopy=true;});
    await hold(page.locator('.row').filter({hasText:'报告.txt'}));await page.locator('#file-menu-copy-path').click();
    await page.locator('#file-copy-dialog').waitFor({state:'visible'});
    assert.equal(await page.locator('#file-copy-text').inputValue(),'C:\\空文件夹\\报告.txt');
    await page.evaluate(()=>history.back());await page.locator('#file-copy-dialog').waitFor({state:'hidden'});
    await hold(page.locator('.row').filter({hasText:'报告.txt'}));
    await page.evaluate(()=>history.back());await page.locator('#file-menu').waitFor({state:'hidden'});
    assert.equal(await page.locator('.row strong').getByText('报告.txt',{exact:true}).isVisible(),true,'back should close the menu before leaving the folder');
    await hold(page.locator('.row').filter({hasText:'报告.txt'}));
    await page.touchscreen.tap(4,4);await page.locator('#file-menu').waitFor({state:'hidden'});
    const row=page.locator('.row').filter({hasText:'报告.txt'});
    await row.dispatchEvent('pointerdown',{button:0,pointerId:8,pointerType:'touch',clientX:100,clientY:200});
    await row.dispatchEvent('pointermove',{pointerId:8,pointerType:'touch',clientX:100,clientY:235});
    await page.waitForTimeout(650);await row.dispatchEvent('pointerup',{button:0,pointerId:8,pointerType:'touch',clientX:100,clientY:235});
    assert.equal(await page.locator('#file-menu').isHidden(),true,'scrolling must not open the long-press menu');
    assert.deepEqual(errors,[]);
    console.log('File long-press menu, simulated clipboard, downloads, mobile path copying and empty-folder paste passed.');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode=1; });
