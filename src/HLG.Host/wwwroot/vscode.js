(() => {
  let current=null, consent=null;
  const visible=()=>!$('vscode-license').classList.contains('hidden');
  const valid=(entry,attempt)=>current===entry&&entry.attempt===attempt;
  function finishConsent(accepted){const resolve=consent;consent=null;$('vscode-license').classList.add('hidden');resolve?.(accepted);}
  function leave(){
    if(current){clearTimeout(current.timer);current.controller?.abort();current=null;}
    finishConsent(false);$('vscode-frame-host').replaceChildren();$('vscode-view').classList.add('hidden');document.body.classList.remove('vscode-mode');
  }
  function returnToSource(){
    if(!current)return false;const source=current.source;leave();
    if(source.view==='files'){state.picker=false;void files(source.path).then(()=>{if(state.view==='文件浏览'&&state.path===source.path)window.scrollTo(0,source.scroll);});}
    else void windows(true);
    return true;
  }
  function back(){if(visible()){finishConsent(false);return true;}return returnToSource();}
  function message(title,detail,error=false){
    $('vscode-loading').classList.remove('hidden');$('vscode-loading').classList.toggle('is-error',error);
    $('vscode-status').textContent=title;$('vscode-detail').textContent=detail;
    $('vscode-retry').classList.toggle('hidden',!error);$('vscode-reload').disabled=!error;
  }
  function guardBack(){try{history.pushState({hlgBackGuard:true},'');}catch{}}
  function mount(entry,session,attempt){
    if(entry.frame)return;
    if(!new RegExp('^/api/vscode/web/[a-f0-9]{32}/$').test(session.url)||!session.url.includes('/'+session.id+'/'))throw new Error('无效的 VS Code 网页地址');
    const frame=document.createElement('iframe');entry.frame=frame;frame.title='VS Code · '+session.name;
    frame.allow='clipboard-read; clipboard-write; fullscreen';frame.referrerPolicy='same-origin';
    frame.addEventListener('load',()=>{
      if(!valid(entry,attempt))return;
      try{
        if(!frame.contentDocument?.getElementById('vscode-workbench-web-configuration')){message('编辑器未能加载','请检查配对状态和电脑端日志，然后重试。',true);return;}
        // Child-frame navigation participates in the phone's joint history.
        // Keep a parent guard above it and handle a frame-local back as well.
        frame.contentWindow.addEventListener('popstate',()=>{if(valid(entry,attempt)){returnToSource();guardBack();}});
        guardBack();$('vscode-loading').classList.add('hidden');$('vscode-reload').disabled=false;
      }catch{message('无法载入编辑器','网页地址发生了变化，请重试连接。',true);}
    });
    frame.addEventListener('error',()=>{if(valid(entry,attempt))message('连接失败','请检查电脑网络后重试。',true);});
    frame.src=session.url;$('vscode-frame-host').replaceChildren(frame);
  }
  async function refresh(entry,session,attempt){
    if(!valid(entry,attempt))return;
    entry.session=session;
    $('vscode-title').textContent='VS Code 网页 · '+session.name;
    if(session.state==='error'||session.state==='stopped'){
      entry.frame?.remove();entry.frame=null;message(session.state==='error'?'启动失败':'网页服务已停止',session.message||'点击重试重新启动。',true);return;
    }
    if(session.state==='ready')mount(entry,session,attempt);
    else message(session.message||'正在准备 VS Code…','首次启动需要下载官方组件。你可以返回，稍后从“当前画面”继续。');
    entry.timer=setTimeout(async()=>{
      try{const latest=await api('/vscode/sessions/'+encodeURIComponent(session.id),{signal:entry.controller.signal});if(valid(entry,attempt))await refresh(entry,latest,attempt);}
      catch(error){if(valid(entry,attempt)){entry.frame?.remove();entry.frame=null;message('连接中断',error.message,true);}}
    },2000);
  }
  async function start(entry,openDesktop,accepted=false){
    clearTimeout(entry.timer);entry.controller?.abort();entry.controller=new AbortController();const attempt=++entry.attempt;
    entry.frame=null;$('vscode-frame-host').replaceChildren();message('正在准备 VS Code…','正在连接电脑上的官方 VS Code。');
    try{
      let session;
      try{session=await api('/vscode/open',{method:'POST',signal:entry.controller.signal,body:JSON.stringify({path:entry.path,acceptLicense:accepted,openDesktop})});}
      catch(error){
        if(!valid(entry,attempt))return;
        if(error.status===428&&error.data?.licenseRequired){
          $('vscode-license').classList.remove('hidden');const agree=await new Promise(resolve=>{consent=resolve;});
          if(!valid(entry,attempt))return;if(!agree){returnToSource();return;}await start(entry,openDesktop,true);return;
        }
        throw error;
      }
      if(valid(entry,attempt)){entry.openDesktop=false;await refresh(entry,session,attempt);}
    }catch(error){if(valid(entry,attempt))message('无法打开 VS Code',error.message,true);}
  }
  function show(path,source,session=null){
    openPage('VS Code 网页');state.picker=false;collapseComposer();
    const entry={path,source,attempt:0,session,frame:null,timer:null,controller:null,openDesktop:!session};current=entry;
    $('vscode-title').textContent='VS Code 网页'+(session?' · '+session.name:'');$('vscode-path').textContent=path;
    $('vscode-view').classList.remove('hidden');document.body.classList.add('vscode-mode');resize();
    void start(entry,entry.openDesktop);
  }
  function renderEntries(sessions){
    if(!Array.isArray(sessions))return 0;let count=0;
    for(const session of sessions){
      if(typeof session.path!=='string'||typeof session.id!=='string')continue;count++;
      const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');svg.classList.add('vscode-icon');
      const shape=document.createElementNS('http://www.w3.org/2000/svg','path');shape.setAttribute('d','M17 2 7 11 3 8 1 10v4l2 2 4-3 10 9 6-3V5Zm0 6v8l-6-4Z');svg.append(shape);
      const button=row(svg,'VS Code 网页 · '+session.name,session.path,()=>{if(button.dataset.longPressed==='true'){button.dataset.longPressed='false';return;}show(session.path,{view:'windows'},session);});
      const badge=element('span','vscode-badge','网页');button.insertBefore(badge,button.lastChild);button.dataset.vscodeSession=session.id;
      let timer,startX,startY;const cancel=()=>{clearTimeout(timer);};
      button.addEventListener('pointerdown',event=>{if(event.button!==0)return;startX=event.clientX;startY=event.clientY;button.dataset.longPressed='false';cancel();timer=setTimeout(async()=>{
        button.dataset.longPressed='true';if(!confirm('确定停止“'+session.name+'”的网页服务并移除入口？请先保存网页中未保存的内容。'))return;
        try{await api('/vscode/sessions/'+encodeURIComponent(session.id),{method:'DELETE'});toast('已停止 VS Code 网页服务');if(state.view==='当前画面')void windows(true);}catch(error){toast(error.message);}
      },650);});
      button.addEventListener('pointermove',event=>{if(Math.hypot(event.clientX-startX,event.clientY-startY)>12)cancel();});button.addEventListener('pointerup',cancel);button.addEventListener('pointercancel',cancel);button.addEventListener('contextmenu',event=>event.preventDefault());
    }
    return count;
  }
  function resize(){const viewport=window.visualViewport;$('vscode-view').style.height=(viewport?.height||innerHeight)+'px';$('vscode-view').style.top=(viewport?.offsetTop||0)+'px';}
  window.addEventListener('resize',resize);window.visualViewport?.addEventListener('resize',resize);window.visualViewport?.addEventListener('scroll',resize);
  $('file-menu-vscode').onclick=()=>{const target=fileMenuTarget;if(!target?.directory)return;const source={view:'files',path:state.path,scroll:scrollY};closeFileMenu();show(target.path,source);};
  $('vscode-back').onclick=back;$('vscode-reload').onclick=$('vscode-retry').onclick=()=>{if(current)void start(current,current.openDesktop);};
  $('vscode-license-accept').onclick=()=>finishConsent(true);$('vscode-license-cancel').onclick=()=>finishConsent(false);
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&visible()){event.preventDefault();finishConsent(false);}});
  window.hlgVsCode={leave,back,renderEntries};
})();
