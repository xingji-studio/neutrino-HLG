const {chromium}=require('playwright-core');
const fs=require('node:fs/promises');
const path=require('node:path');
const assert=require('node:assert/strict');
const [origin,code,folder,output]=process.argv.slice(2);

(async()=>{
  const browser=await chromium.launch({executablePath:process.env.HLG_TEST_BROWSER||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true,args:['--ignore-certificate-errors']});
  const context=await browser.newContext({viewport:{width:430,height:932},isMobile:true,hasTouch:true,ignoreHTTPSErrors:true});
  const page=await context.newPage();let progress;
  try{
    await page.goto(origin);await page.locator('#pair').waitFor({state:'visible'});
    await page.locator('#pair-code').fill(code);await page.locator('#pair-form .primary').click();
    await page.locator('#trust-decline').click();await page.locator('#pair').waitFor({state:'hidden'});
    await page.getByRole('button',{name:'文件浏览',exact:true}).click();
    await page.getByText(path.basename(folder),{exact:true}).dispatchEvent('contextmenu');
    await page.locator('#file-menu-vscode').click();
    const consent=page.locator('#vscode-license');
    await Promise.race([consent.waitFor({state:'visible',timeout:600000}),page.locator('#vscode-frame-host iframe').waitFor({state:'attached',timeout:600000})]);
    if(await consent.isVisible())await page.locator('#vscode-license-accept').click();
    progress=setInterval(async()=>{try{console.log('Official editor: '+await page.locator('#vscode-status').textContent());}catch{}},15000);
    await page.locator('#vscode-loading').waitFor({state:'hidden',timeout:600000});clearInterval(progress);
    const frame=await page.locator('#vscode-frame-host iframe').elementHandle().then(e=>e.contentFrame());
    await frame.locator('.monaco-workbench').waitFor({timeout:60000});
    await frame.waitForFunction(()=>document.querySelector('.monaco-workbench')?.classList.contains('vs-dark'));
    console.log('Official workbench loaded through HTTPS.');
    const trust=frame.getByRole('button',{name:/Yes, I trust|是，我信任|信任作者/});
    if(await trust.count())await trust.first().click();
    await frame.locator('body').press('Control+p');
    const quick=frame.locator('.quick-input-widget input');await quick.waitFor({state:'visible'});
    await quick.fill(path.join(folder,'hello.txt'));
    await frame.locator('.quick-input-list .monaco-list-row').filter({hasText:'hello.txt'}).first().waitFor({state:'visible'});
    await quick.press('Enter');await quick.waitFor({state:'hidden'});
    await frame.locator('.monaco-editor .view-lines').last().waitFor({state:'visible',timeout:30000});
    if(await frame.locator('.part.sidebar').isVisible())await frame.locator('body').press('Control+b');
    if(await frame.locator('.part.auxiliarybar').isVisible())await frame.locator('body').press('Control+Alt+b');
    await frame.locator('body').press('Control+1');
    await page.keyboard.press('Control+End');await page.keyboard.press('Enter');
    const marker='Saved through HLG official VS Code web';
    await page.keyboard.insertText(marker);await page.keyboard.press('Control+s');
    for(let attempt=0;attempt<50;attempt++){if((await fs.readFile(path.join(folder,'hello.txt'),'utf8')).includes(marker))break;await new Promise(resolve=>setTimeout(resolve,100));}
    assert.ok((await fs.readFile(path.join(folder,'hello.txt'),'utf8')).includes(marker),'web editor did not save to host files');
    const external='Changed on the Windows filesystem';await fs.appendFile(path.join(folder,'hello.txt'),'\n'+external+'\n');
    await frame.waitForFunction(text=>[...document.querySelectorAll('.monaco-editor .view-lines')].some(e=>e.textContent.replaceAll('\u00a0',' ').includes(text)),external);
    await page.screenshot({path:path.join(output,'vscode-mobile.png')});
    console.log('Saved a file from the phone-sized official editor to the host.');
    await page.evaluate(()=>history.back());await page.locator('#vscode-view').waitFor({state:'hidden'});
    assert.equal(await page.locator('#page-title').textContent(),'文件浏览');
    const sessions=await page.evaluate(async()=>await (await fetch('/api/vscode/sessions')).json());
    const session=sessions.find(s=>s.path===folder);assert.equal(session.state,'ready','phone back stopped the service');
    await page.locator('#back').click();await page.locator('#back').click();
    // Navigate directly because the test directory has parents above the file listing.
    await page.evaluate(()=>home());await page.getByRole('button',{name:'当前画面',exact:true}).click();
    const webEntry=page.locator('[data-vscode-session="'+session.id+'"]');await webEntry.waitFor();
    assert.equal(await page.getByText('VS Code 桌面窗口',{exact:true}).isVisible(),true);
    await webEntry.click();
    await page.locator('#vscode-loading').waitFor({state:'hidden',timeout:30000});
    await page.locator('#vscode-back').click();assert.equal(await page.locator('#page-title').textContent(),'当前画面');
    console.log('Official VS Code mobile rendering, file save, independent entries, reopening and browser back passed. Screenshot: '+path.join(output,'vscode-mobile.png'));
  }catch(error){
    clearInterval(progress);await page.screenshot({path:path.join(output,'vscode-error.png')}).catch(()=>{});
    console.error('Visible status:',await page.locator('#vscode-status').textContent().catch(()=>''),await page.locator('#vscode-detail').textContent().catch(()=>''));
    console.error('Editor text:',await page.locator('#vscode-frame-host iframe').elementHandle().then(e=>e?.contentFrame()).then(f=>f?.locator('body').innerText()).catch(()=>''));
    throw error;
  }finally{clearInterval(progress);await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
