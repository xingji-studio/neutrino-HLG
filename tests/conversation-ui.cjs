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
    const page=await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    const sent=[];let created=0,reject=false,archived=false;
    const snapshot={sessionId:'s1',cwd:'C:\\Desktop',provider:'A',model:'one',intensity:'Low',agentMode:'standard',busy:false,messages:[]};
    await page.route('**/api/**',async route=>{
      const request=route.request(),url=new URL(request.url());let status=200,data={};
      if(url.pathname==='/api/status')data={desktop:'C:\\Desktop',machine:'TEST'};
      else if(url.pathname==='/api/apps')data=[];
      else if(url.pathname==='/api/windows')data=[{id:'123',processId:22,title:'测试应用',application:'example'}];
      else if(url.pathname==='/api/windows/icon'){await route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10" fill="red"/></svg>'});return;}
      else if(url.pathname==='/api/neutrino/models')data={providers:[{name:'A',models:[{name:'one',configured:true,intensities:['Low','High']},{name:'two',configured:true,intensities:['Low','High']}]}],selection:{provider:'A',model:'one',intensity:'Low'}};
      else if(url.pathname==='/api/tasks'&&request.method()==='POST'){created++;snapshot.messages.push({role:'user',content:request.postDataJSON().text},{role:'assistant',content:'第一轮回复 <img src=x onerror=alert(1)>'});data={key:'live-1',taskId:'stable-1'};}
      else if(url.pathname==='/api/tasks')data=created?[{id:'stable-1',key:'live-1',title:'第一条'}]:[];
      else if(url.pathname==='/api/tasks/stable-1/message'){
        const body=request.postDataJSON();sent.push(body);
        if(reject){status=409;data={error:'已有消息排队'};}else{
          archived=false;if(body.selection)Object.assign(snapshot,body.selection);if(body.agent_mode)snapshot.agentMode=body.agent_mode;
          snapshot.messages.push({role:'user',content:body.text},{role:'assistant',content:'继续回复'});data={ok:true,taskId:'stable-1'};
        }
      } else if(url.pathname==='/api/tasks/stable-1')data={...snapshot,archived};
      await route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.waitForFunction(()=>document.querySelectorAll('#model option').length===2);
    await page.locator('#prompt').fill('第一条');
    await Promise.all([page.waitForResponse('**/api/neutrino/models'),page.locator('#settings').click()]);
    assert.equal(await page.locator('#settings-panel').isVisible(),true);
    await page.locator('#intensity-ticks').getByRole('button',{name:'高',exact:true}).click();
    assert.equal(await page.locator('#settings-panel').isVisible(),true,'interacting inside should not close settings');
    await page.locator('#prompt').click();
    assert.equal(await page.locator('#settings-panel').isHidden(),true,'outside click closes settings');
    await Promise.all([page.waitForResponse('**/api/neutrino/models'),page.locator('#settings').click()]);
    await page.keyboard.press('Escape');assert.equal(await page.locator('#settings-panel').isHidden(),true);
    await page.locator('#send').click();
    await page.locator('.chat-message-assistant .chat-bubble').waitFor();
    assert.equal(created,1);assert.equal(await page.locator('.chat-message-user .chat-bubble').textContent(),'第一条');
    assert.equal(await page.locator('.chat-message-user .chat-bubble').evaluate(e=>getComputedStyle(e).backgroundColor),'rgb(69, 172, 215)');
    assert.match(await page.locator('.chat-message-assistant .chat-bubble').textContent(),/<img/);
    assert.equal(await page.locator('.chat-log img').count(),0,'message content rendered as text');
    assert.equal(await page.locator('#directory').isDisabled(),true);
    await page.locator('#prompt').fill('第二条');
    await Promise.all([page.waitForResponse('**/api/neutrino/models'),page.locator('#settings').click()]);
    await page.locator('#model').selectOption('1');
    await page.locator('#mode').selectOption('ptc');
    await page.locator('#send').click();
    await page.getByText('继续回复',{exact:true}).waitFor();
    assert.equal(created,1);assert.equal(sent.length,1);
    assert.deepEqual(sent[0],{text:'第二条',selection:{provider:'A',model:'two',intensity:'High'},agent_mode:'ptc'});
    assert.equal(await page.locator('.chat-message-user').count(),2);
    reject=true;await page.locator('#prompt').fill('保留草稿');await page.locator('#send').click();
    await page.waitForFunction(()=>document.querySelector('#toast').textContent.includes('排队'));
    assert.equal(await page.locator('#prompt').inputValue(),'保留草稿');
    await page.locator('#back').click();await page.getByText('第一条',{exact:true}).click();
    assert.equal(await page.locator('#prompt').inputValue(),'保留草稿');
    reject=false;archived=true;await page.waitForFunction(()=>document.querySelector('.chat-status')?.textContent.includes('历史会话'));
    await page.locator('#prompt').focus();await page.locator('#send').click();
    await page.waitForFunction(()=>document.querySelectorAll('.chat-message-user').length===3);
    assert.equal(created,1);assert.equal(sent.at(-1).text,'保留草稿');
    if(process.env.HLG_TEST_SCREENSHOT)await page.screenshot({path:process.env.HLG_TEST_SCREENSHOT});
    await page.locator('#back').click();await page.locator('#back').click();
    assert.equal(await page.locator('#directory').isEnabled(),true);
    await page.getByText('当前画面',{exact:true}).click();
    await page.locator('.window-icon.is-loaded').waitFor();
    assert.match(await page.locator('.window-icon').getAttribute('src'),/id=123&processId=22/);
    assert.deepEqual(errors,[]);
    console.log('Multi-turn conversation, drafts, settings dismissal and window icons passed.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
