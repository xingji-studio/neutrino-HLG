const { chromium }=require('playwright-core');
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../src/HLG.Host/wwwroot');
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
    let delayDesktop=false;
    await page.route('**/api/**',async route=>{
      const url=new URL(route.request().url());let data={};
      if(url.pathname==='/api/status')data={desktop:'C:\\Desktop',machine:'TEST'};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      else if(url.pathname==='/api/tasks')data=[{id:'s1',title:'任务一'}];
      else if(url.pathname==='/api/tasks/s1')data={messages:[{role:'user',content:'历史'}],busy:false};
      else if(url.pathname==='/api/fs'){
        const p=url.searchParams.get('path');
        if(!p)data={roots:[{name:'C:\\',ready:true,type:'Fixed'}]};
        else if(p==='C:\\Desktop'){
          if(delayDesktop)await new Promise(resolve=>setTimeout(resolve,350));
          data={path:p,parent:'C:\\',entries:[{name:'桌面子目录',path:'C:\\Desktop\\Child',directory:true}]};
        }
        else if(p==='C:\\Desktop\\Child')data={path:p,parent:'C:\\Desktop',entries:[]};
        else if(p==='C:\\')data={path:p,parent:null,entries:[{name:'甲文件夹',path:'C:\\A',directory:true}]};
        else if(p==='C:\\A')data={path:p,parent:'C:\\',entries:[{name:'乙文件夹',path:'C:\\A\\B',directory:true}]};
        else data={path:p,parent:'C:\\A',entries:[{name:'里边的文件',path:p+'\\x.txt',directory:false}]};
      }
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.locator('#prompt').focus();await page.locator('#settings').click();
    assert.equal(await page.locator('#settings-panel').isVisible(),true);
    await page.evaluate(()=>history.back());await page.locator('#settings-panel').waitFor({state:'hidden'});
    assert.equal(await page.locator('#composer').evaluate(e=>e.classList.contains('expanded')),true);
    await page.evaluate(()=>history.back());await page.waitForFunction(()=>!document.getElementById('composer').classList.contains('expanded'));
    await page.locator('#prompt').fill('保留的草稿');
    const originalDirectory=await page.locator('#directory span').textContent();
    await page.locator('#directory').click();
    await page.getByText('桌面子目录',{exact:true}).click();
    const close=page.getByRole('button',{name:'关闭选择目录'});
    assert.equal(await close.locator('svg path').getAttribute('d'),'M6 6l12 12M18 6 6 18');
    await close.click();await page.locator('#home').waitFor({state:'visible'});
    assert.equal(await page.locator('#prompt').inputValue(),'保留的草稿');
    assert.equal(await page.locator('#directory span').textContent(),originalDirectory);
    delayDesktop=true;
    const delayedResponse=page.waitForResponse(r=>r.url().includes('/api/fs?')&&new URL(r.url()).searchParams.get('path')==='C:\\Desktop');
    await page.locator('#directory').click();
    await page.getByRole('button',{name:'关闭选择目录'}).click();
    await delayedResponse;
    assert.equal(await page.locator('#home').isVisible(),true);
    await page.getByRole('button',{name:'文件浏览'}).click();
    await page.getByText('C:\\',{exact:true}).click();
    await page.getByText('甲文件夹',{exact:true}).click();await page.getByText('乙文件夹',{exact:true}).click();
    await page.getByText('里边的文件',{exact:true}).waitFor();
    const back=page.getByRole('button',{name:'返回',exact:true});
    assert.equal(await back.locator('svg path').getAttribute('d'),'M19 12H5m7-7-7 7 7 7');
    await back.click();await page.getByText('乙文件夹',{exact:true}).waitFor();
    await page.evaluate(()=>history.back());await page.getByText('甲文件夹',{exact:true}).waitFor();
    await page.evaluate(()=>history.back());await page.getByText('C:\\',{exact:true}).waitFor();
    await page.evaluate(()=>history.back());await page.locator('#home').waitFor({state:'visible'});
    await page.getByRole('button',{name:'任务列表'}).click();await page.getByText('任务一',{exact:true}).click();
    await page.getByText('历史',{exact:true}).waitFor();
    await page.evaluate(()=>history.back());await page.getByText('任务一',{exact:true}).waitFor();
    await page.evaluate(()=>history.back());await page.locator('#home').waitFor({state:'visible'});
    await page.evaluate(()=>history.back());await page.waitForFunction(()=>document.getElementById('toast').textContent==='已在首页');
    assert.equal(await page.locator('#home').isVisible(),true);
    assert.deepEqual(errors,[]);
    console.log('Mobile browser back, nested files and task navigation passed.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
