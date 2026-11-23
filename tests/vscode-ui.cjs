const {chromium}=require('playwright-core');
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../src/HLG.Host/wwwroot');
const server=http.createServer((req,res)=>{
  const file=path.resolve(root,req.url==='/'?'index.html':req.url.split('?')[0].replace(/^[/]+/,''));
  if(!file.startsWith(root+path.sep)){res.writeHead(403).end();return;}
  fs.readFile(file,(error,body)=>{if(error){res.writeHead(404).end();return;}res.writeHead(200,{'Content-Type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[path.extname(file)]||'application/octet-stream'}).end(body);});
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({executablePath:process.env.HLG_TEST_BROWSER||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.addInitScript(()=>{class Socket{static OPEN=1;constructor(){this.readyState=1;setTimeout(()=>this.onopen?.(),0);}send(){}close(){this.readyState=3;this.onclose?.();}}window.WebSocket=Socket;});
    let licensed=false,installed=true,delayStart=false,deleted=0,starting=false,locked=false;
    const opens=[],sessions=[];const id='a'.repeat(32);
    const session=()=>({id,name:'项目',path:'C:/项目',state:starting?'starting':'ready',message:'正在准备官方组件…',url:'/api/vscode/web/'+id+'/'});
    await page.route('**/api/**',async route=>{
      const req=route.request(),url=new URL(req.url());let data={};
      if(url.pathname==='/api/status')data={desktop:'C:/',machine:'TEST',locked,unlockAvailable:true};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/neutrino/models')data={providers:[]};
      else if(url.pathname==='/api/windows')data=[{id:'native',processId:10,title:'VS Code 桌面窗口',application:'Code'}];
      else if(url.pathname==='/api/fs')data={path:'C:/',parent:null,entries:[{name:'项目',path:'C:/项目',directory:true},{name:'文件.txt',path:'C:/文件.txt',directory:false}]};
      else if(url.pathname==='/api/vscode/sessions')data=sessions;
      else if(url.pathname==='/api/vscode/open'){
        const body=req.postDataJSON();opens.push(body);
        if(!licensed&&!body.acceptLicense){await route.fulfill({status:428,contentType:'application/json',body:JSON.stringify({licenseRequired:true,error:'需要许可'})});return;}
        if(!installed){await route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({error:'请先安装官方 VS Code'})});return;}
        licensed=true;data=session();if(!sessions.length)sessions.push(data);
        if(delayStart)await new Promise(resolve=>setTimeout(resolve,700));
      }else if(url.pathname==='/api/vscode/sessions/'+id){
        if(req.method()==='DELETE'){deleted++;sessions.splice(0);data={ok:true};}else data=session();
      }else if(url.pathname.startsWith('/api/vscode/web/')){
        await route.fulfill({contentType:'text/html',body:'<!doctype html><meta id="vscode-workbench-web-configuration"><body style="background:#1f1f1f;color:white">Official workbench fixture<input aria-label="editor"></body>'});return;
      }
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto('http://127.0.0.1:'+server.address().port);
    const enterFiles=async()=>{await page.evaluate(()=>home());await page.getByRole('button',{name:'文件浏览',exact:true}).click();await page.locator('#page').getByText('项目',{exact:true}).waitFor();};
    const openFolder=async()=>{await page.locator('#page').getByText('项目',{exact:true}).dispatchEvent('contextmenu');await page.locator('#file-menu-vscode').click();};
    await enterFiles();await page.getByText('文件.txt',{exact:true}).dispatchEvent('contextmenu');assert.equal(await page.locator('#file-menu-vscode').isVisible(),false);await page.locator('#file-menu-close').click();
    await openFolder();await page.locator('#vscode-license').waitFor({state:'visible'});
    await page.evaluate(()=>history.back());await page.locator('#vscode-view').waitFor({state:'hidden'});
    assert.equal(await page.locator('#page-title').textContent(),'文件浏览');assert.equal(sessions.length,0);
    await openFolder();await page.locator('#vscode-license-accept').click();await page.locator('#vscode-loading').waitFor({state:'hidden'});
    assert.deepEqual(opens.at(-1),{path:'C:/项目',acceptLicense:true,openDesktop:true});
    assert.equal(await page.locator('#composer').isVisible(),false);
    const bounds=await page.locator('#vscode-view').boundingBox();assert.equal(bounds.width,390);assert.equal(bounds.height,844);
    await page.evaluate(()=>history.back());await page.locator('#vscode-view').waitFor({state:'hidden'});assert.equal(deleted,0);assert.equal(sessions.length,1);
    await page.evaluate(()=>home());await page.getByRole('button',{name:'当前画面',exact:true}).click();
    await page.locator('#page').getByText('VS Code 网页 · 项目',{exact:true}).waitFor();assert.equal(await page.getByText('VS Code 桌面窗口',{exact:true}).isVisible(),true);assert.equal(await page.locator('.vscode-badge').textContent(),'网页');
    await page.locator('#page').getByText('VS Code 网页 · 项目',{exact:true}).click();await page.locator('#vscode-loading').waitFor({state:'hidden'});assert.equal(opens.at(-1).openDesktop,false);
    const frame=await page.locator('#vscode-frame-host iframe').elementHandle().then(e=>e.contentFrame());
    await frame.evaluate(()=>{history.pushState({editor:true},'', '#editor');history.back();});
    await page.locator('#vscode-view').waitFor({state:'hidden'});assert.equal(await page.locator('#page-title').textContent(),'当前画面');
    await enterFiles();installed=false;await openFolder();await page.getByText('请先安装官方 VS Code',{exact:true}).waitFor();assert.equal(await page.locator('#vscode-retry').isVisible(),true);
    installed=true;starting=true;await page.locator('#vscode-retry').click();await page.getByText('正在准备官方组件…',{exact:true}).waitFor();starting=false;await page.locator('#vscode-loading').waitFor({state:'hidden'});assert.equal(opens.at(-1).openDesktop,true,'install retry must still open the requested native editor');
    await page.locator('#vscode-reload').click();await page.locator('#vscode-loading').waitFor({state:'hidden'});assert.equal(opens.at(-1).openDesktop,false,'reconnecting a running editor must reuse the web service');await page.locator('#vscode-back').click();
    delayStart=true;await openFolder();await page.locator('#vscode-back').click();await new Promise(resolve=>setTimeout(resolve,850));assert.equal(await page.locator('#vscode-view').isVisible(),false,'slow start must not reopen a view after back');delayStart=false;
    locked=true;await page.evaluate(()=>home());await page.getByRole('button',{name:'当前画面',exact:true}).click();await page.locator('#page').getByText('VS Code 网页 · 项目',{exact:true}).waitFor();assert.equal(await page.getByText('登录画面',{exact:true}).isVisible(),true,'editor entries must remain accessible while locked');
    page.once('dialog',dialog=>dialog.accept());await page.locator('#page').getByText('VS Code 网页 · 项目',{exact:true}).dispatchEvent('pointerdown',{button:0,clientX:50,clientY:150});
    await page.waitForFunction(()=>!document.querySelector('[data-vscode-session]'));assert.equal(deleted,1);
    assert.deepEqual(errors,[]);console.log('VS Code folder menu, license, loading/retry, separate entries, mobile back, slow navigation and stop passed.');
  }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
