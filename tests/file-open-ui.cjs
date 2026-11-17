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
    const errors=[];page.on('pageerror', error=>errors.push(error.message));
    await page.addInitScript(() => {
      class MockWebSocket { static OPEN=1; constructor(){this.readyState=1;setTimeout(()=>this.onopen?.(),0);} send(){} close(){this.readyState=3;this.onclose?.();} }
      window.WebSocket=MockWebSocket;
    });
    const dirs={
      'C:\\':[{name:'工具',path:'C:\\工具',directory:true},{name:'报告.txt',path:'C:\\报告.txt',directory:false},{name:'未知文件.nope',path:'C:\\未知文件.nope',directory:false},{name:'出错.fail',path:'C:\\出错.fail',directory:false}],
      'C:\\工具':[{name:'子目录',path:'C:\\工具\\子目录',directory:true},{name:'编辑器.EXE',path:'C:\\工具\\编辑器.EXE',directory:false},{name:'错误.exe',path:'C:\\工具\\错误.exe',directory:false},{name:'不要显示.cmd',path:'C:\\工具\\不要显示.cmd',directory:false}],
      'C:\\工具\\子目录':[]
    };
    const opened=[];let delayToolDirectory=false;
    await page.route('**/api/**',async route=>{
      const req=route.request(),url=new URL(req.url());let data={};
      if(url.pathname==='/api/status')data={desktop:'C:\\',machine:'TEST'};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      else if(url.pathname==='/api/fs'){
        const current=url.searchParams.get('path');
        if(delayToolDirectory&&current==='C:\\工具')await new Promise(resolve=>setTimeout(resolve,500));
        data=current?{path:current,parent:current==='C:\\'?null:current==='C:\\工具'?'C:\\':'C:\\工具',entries:dirs[current]||[]}:{roots:[{name:'C:\\',ready:true,type:'Fixed'}]};
      } else if(url.pathname==='/api/fs/open-options'){
        if(url.searchParams.get('path').endsWith('.fail')){await route.fulfill({status:500,contentType:'application/json',body:JSON.stringify({error:'读取关联失败'})});return;}
        data=url.searchParams.get('path').endsWith('.txt')?{defaultApp:{id:'default',name:'默认编辑器'},recommended:[{id:'rec1',name:'推荐编辑器'},{id:'rec2',name:'其他推荐'}]}:{defaultApp:null,recommended:[]};
      } else if(url.pathname==='/api/fs/open'){
        const body=req.postDataJSON();opened.push(body);
        if(body.executable?.endsWith('错误.exe')){await route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({error:'应用程序无法启动'})});return;}
        data={ok:true};
      } else if(url.pathname==='/api/fs/download'){
        assert.equal(url.searchParams.get('path'),'C:\\报告.txt');
        await route.fulfill({status:200,headers:{'Content-Type':'application/octet-stream','Content-Disposition':"attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A.txt"},body:'test file'});return;
      }
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.getByRole('button',{name:'文件浏览'}).click();await page.getByText('C:\\',{exact:true}).click();
    const openReport=async()=>{await page.getByText('报告.txt',{exact:true}).click();await page.locator('#file-open-menu').waitFor({state:'visible'});};
    await openReport();await page.getByText('推荐编辑器',{exact:true}).waitFor();
    assert.deepEqual(await page.locator('#file-open-actions > button, #file-open-actions > a').evaluateAll(nodes=>nodes.map(n=>n.querySelector('strong')?.textContent)),['默认编辑器','下载至手机','推荐编辑器','其他推荐','其他应用']);
    await page.getByText('默认编辑器',{exact:true}).click();await page.locator('#file-open-menu').waitFor({state:'hidden'});
    assert.deepEqual(opened.at(-1),{path:'C:\\报告.txt',kind:'default'});
    await openReport();await page.getByText('推荐编辑器',{exact:true}).click();await page.locator('#file-open-menu').waitFor({state:'hidden'});
    assert.deepEqual(opened.at(-1),{path:'C:\\报告.txt',kind:'recommended',handlerId:'rec1'});
    await openReport();const download=page.waitForEvent('download');await page.getByText('下载至手机',{exact:true}).click();
    assert.equal((await download).suggestedFilename(),'报告.txt');
    await openReport();await page.evaluate(()=>history.back());await page.locator('#file-open-menu').waitFor({state:'hidden'});
    assert.equal(await page.getByText('报告.txt',{exact:true}).isVisible(),true,'back must close the menu first');
    await page.getByText('未知文件.nope',{exact:true}).click();await page.getByText('未设置默认应用',{exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'未设置默认应用 默认应用'}).isDisabled(),true);
    assert.deepEqual(await page.locator('#file-open-actions > button, #file-open-actions > a').evaluateAll(nodes=>nodes.map(n=>n.querySelector('strong')?.textContent)),['未设置默认应用','下载至手机','其他应用']);
    await page.locator('#file-open-close').click();await page.getByText('出错.fail',{exact:true}).click();
    await page.getByText('读取关联失败',{exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'无法读取默认应用 默认应用'}).isDisabled(),true);
    assert.equal(await page.getByText('其他应用',{exact:true}).isVisible(),true,'association errors must not prevent choosing another app');
    await page.locator('#file-open-close').click();await openReport();await page.getByText('其他应用',{exact:true}).click();
    assert.equal(await page.locator('#page-title').textContent(),'选择应用程序');
    assert.equal(await page.getByText('报告.txt',{exact:true}).count(),0,'picker must hide non-executable files');
    await page.getByText('工具',{exact:true}).click();await page.getByText('编辑器.EXE',{exact:true}).waitFor();
    assert.equal(await page.getByText('不要显示.cmd',{exact:true}).count(),0,'picker must only accept .exe files');
    await page.getByText('错误.exe',{exact:true}).click();await page.waitForFunction(()=>document.getElementById('toast').textContent.includes('无法启动'));
    assert.equal(await page.locator('#page-title').textContent(),'选择应用程序','failed launch must leave the picker open');
    await page.getByText('编辑器.EXE',{exact:true}).click();await page.getByText('报告.txt',{exact:true}).waitFor();
    assert.deepEqual(opened.at(-1),{path:'C:\\报告.txt',kind:'executable',executable:'C:\\工具\\编辑器.EXE'});
    assert.equal(await page.locator('#page-title').textContent(),'文件浏览');
    delayToolDirectory=true;await openReport();await page.getByText('其他应用',{exact:true}).click();await page.getByText('工具',{exact:true}).click();
    await page.evaluate(()=>history.back());await page.getByText('工具',{exact:true}).waitFor();
    assert.equal(await page.locator('#page-title').textContent(),'选择应用程序');
    await page.getByText('取消选择应用',{exact:true}).click();await page.locator('#file-open-menu').waitFor({state:'visible'});
    assert.equal(await page.getByText('报告.txt',{exact:true}).isVisible(),true,'cancel must return to the original directory');
    assert.deepEqual(errors,[]);
    console.log('File open choices, phone download, executable picker and mobile back passed.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
