const $ = id => document.getElementById(id);
const state = { cwd: '', path: null, parentPath: null, apps: [], models: [], levels: [], view: 'home', picker: false, taskId: null, taskSnapshot: null };
let toastTimer;
let terminalSession = null;
let appsGeneration = 0;
let filesGeneration = 0;
const trustStorageKey = 'hlg.trusted-device.v1';
let trustRestoration = null, trustCandidate = null, previousTrustToken = null;
let approvalSerial=0,approvalRunning=false,approvalCandidate=null;
let presenceSocket = null, presenceHeartbeat = null, presenceRetry = null, presenceEnabled = false;
let detailTimer, detailGeneration=0, detailRefresh=null, detailPrevious=null, detailDirty={model:false,mode:false}, homeDraft='';
const conversationDrafts=new Map();
function closeTerminal() { if (!terminalSession) return; const session=terminalSession; terminalSession=null; session.observer?.disconnect(); session.socket?.close(); session.term?.dispose(); }
function toast(message) { $('toast').textContent = message; $('toast').classList.add('visible'); clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').classList.remove('visible'), 3500); }
function readTrustedToken() { try { return localStorage.getItem(trustStorageKey); } catch { return null; } }
async function restoreTrust() {
  if(trustRestoration)return trustRestoration;
  const token=readTrustedToken();if(!token)return false;
  trustRestoration=(async()=>{
    try {
      const response=await fetch('/api/trust/restore',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify({token})});
      if(response.status===401&&readTrustedToken()===token){try{localStorage.removeItem(trustStorageKey);}catch{}}
      return response.ok;
    } catch { return false; }
  })();
  try{return await trustRestoration;}finally{trustRestoration=null;}
}
async function api(path, options = {}) {
  const request={ credentials: 'same-origin', ...options, headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...options.headers } };
  let response = await fetch('/api' + path, request);
  if(response.status===401&&path!=='/pair'&&await restoreTrust())response=await fetch('/api'+path,request);
  if (response.status === 401) { if(path==='/pair')throw new Error('配对码错误');$('pair').classList.remove('hidden'); throw new Error('请先配对'); }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || data.detail || `请求失败 (${response.status})`);
  return data;
}
function element(tag, className, text) { const el = document.createElement(tag); if (className) el.className = className; if (text != null) el.textContent = text; return el; }
function arrowIcon(direction, className='') {
  const paths={left:'M19 12H5m7-7-7 7 7 7',right:'M5 12h14m-7-7 7 7-7 7',up:'M12 19V5m-7 7 7-7 7 7',down:'M12 5v14m-7-7 7 7 7-7',chevron:'m9 18 6-6-6-6',shift:'M12 3 4 11h5v9h6v-9h5L12 3Z',close:'M6 6l12 12M18 6 6 18'};
  const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
  svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('fill','none');svg.setAttribute('stroke','currentColor');svg.setAttribute('stroke-width','1.8');svg.setAttribute('stroke-linecap','round');svg.setAttribute('stroke-linejoin','round');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');svg.classList.add('arrow-icon');if(className)svg.classList.add(className);
  const path=document.createElementNS('http://www.w3.org/2000/svg','path');path.setAttribute('d',paths[direction]);svg.append(path);return svg;
}
function collapseComposer() { $('prompt').blur();$('settings-panel').classList.add('hidden');$('composer').classList.remove('expanded');$('composer-controls').classList.add('hidden');$('send').classList.add('hidden'); }
function leaveTaskDetail() {
  if(state.taskId===null)return;
  conversationDrafts.set(state.taskId,$('prompt').value);$('prompt').value=homeDraft;
  state.taskId=null;state.taskSnapshot=null;detailGeneration++;clearTimeout(detailTimer);detailRefresh=null;document.body.classList.remove('conversation-mode');
  $('prompt').placeholder='输入以唤起智能体';$('directory').disabled=false;$('directory').removeAttribute('title');$('directory').querySelector('span').textContent=state.cwd||'桌面';
  if(detailPrevious){const i=state.models.findIndex(m=>m.provider===detailPrevious.provider&&m.name===detailPrevious.model);if(i>=0){$('model').value=String(i);updateLevels(detailPrevious.intensity);}$('mode').value=detailPrevious.mode;modelSelectionChanged=detailPrevious.changed;detailPrevious=null;}
  collapseComposer();
}
function openPage(title) { filesGeneration++;if(title!=='任务详情')leaveTaskDetail(); closeTerminal(); state.view = title; const picker=title==='选择工作目录';$('back').setAttribute('aria-label',picker?'关闭选择目录':'返回');$('back').replaceChildren(arrowIcon(picker?'close':'left'));$('composer').classList.toggle('hidden', title==='终端'); $('home').classList.add('hidden'); $('page').classList.remove('hidden'); $('toolbar').classList.remove('hidden'); $('page-title').textContent = title; $('title').classList.add('hidden'); $('page').replaceChildren(); }
function home() { filesGeneration++;leaveTaskDetail();closeTerminal(); state.view = 'home'; state.picker = false; $('composer').classList.remove('hidden'); $('home').classList.remove('hidden'); $('page').classList.add('hidden'); $('toolbar').classList.add('hidden'); $('title').classList.remove('hidden'); void loadApps(); }
function row(icon, title, subtitle, action) { const b = element('button','row'), iconEl=element('span','row-icon');if(icon instanceof Node)iconEl.append(icon);else if(icon==='arrow-up')iconEl.append(arrowIcon('up'));else iconEl.textContent=icon;b.append(iconEl); const info=element('span','row-text'); info.append(element('strong','',title),element('small','',subtitle || '')); b.append(info,arrowIcon('chevron','row-arrow')); b.addEventListener('click',action); $('page').append(b); }
function empty(text) { $('page').append(element('div','empty',text)); }
const builtin = [{name:'文件浏览', icon:'▱', action:()=>files(null)}, {name:'终端', icon:'⌘', action:terminal}, {name:'任务列表', icon:'☷', action:tasks}, {name:'当前画面', icon:'▣', action:windows}];
function renderApps() {
  $('grid').replaceChildren();
  for (const item of [...builtin, ...state.apps.map(a=>({name:a.name,icon:'▦',id:a.id,path:a.path,action:()=>launch(a)}))]) {
    const b=element('button','app'),icon=element('span','app-icon'),fallback=element('span','',item.icon);icon.setAttribute('aria-hidden','true');
    icon.append(fallback);
    if(item.id||item.path){
      const img=element('img','app-icon-image');img.alt='';img.loading='lazy';
      img.onload=()=>{fallback.remove();img.classList.add('is-loaded');};img.onerror=()=>img.remove();
      icon.append(img);img.src='/api/apps/icon?'+new URLSearchParams(item.id?{id:item.id}:{path:item.path});
    }
    b.append(icon,element('span','app-name',item.name));b.addEventListener('click',item.action);$('grid').append(b);
  }
}
async function launch(app) { try { const previous=await api('/windows');const old=new Set(previous.map(w=>w.id+':'+w.processId));const result=await api('/apps/open',{method:'POST',body:JSON.stringify(app.id?{id:app.id}:{path:app.path})});void loadApps();toast('正在打开 '+app.name);const name=app.name.toLowerCase();for(let i=0;i<20;i++){await new Promise(r=>setTimeout(r,500));const current=await api('/windows');const matching=current.filter(w=>(result.processId&&w.processId===result.processId)||w.title.toLowerCase().includes(name)||w.application.toLowerCase()===name);const fresh=matching.filter(w=>!old.has(w.id+':'+w.processId));const found=fresh.length===1?fresh[0]:matching.length===1?matching[0]:null;if(found){await api('/windows/activate',{method:'POST',body:JSON.stringify({id:found.id,processId:found.processId})});openRemote(found);return;}}await windows();toast('请选择打开的应用窗口'); } catch(e) { toast(e.message); } }
function unavailable(name) { openPage(name); empty('此功能正在开发中。'); }
async function windows(showLockedList = false) {
  openPage('当前画面');
  try {
    const status=await api('/status');
    if(state.view!=='当前画面')return;
    if(status.locked===true){
      if(!status.unlockAvailable){empty('电脑已锁定。请先在电脑端设置中启用远程解锁并配置 HTTPS。');return;}
      if(showLockedList!==true){openRemote(null,{secure:true});return;}
      row('▣','登录画面','电脑已锁定',()=>openRemote(null,{secure:true}));return;
    }
    const list=await api('/windows');
    if(state.view!=='当前画面')return;
    row('▣','全部画面','查看所有显示器',()=>openRemote(null));
    if(!list.length){empty('没有找到当前任务栏窗口。');return;}
    for(const item of list){
      const icon=element('span','window-icon-fallback'),fallback=element('span','','▤'),img=element('img','window-icon');img.alt='';img.loading='lazy';img.onload=()=>{fallback.remove();img.classList.add('is-loaded');};img.onerror=()=>img.remove();icon.append(fallback,img);img.src='/api/windows/icon?'+new URLSearchParams({id:item.id,processId:String(item.processId)});
      row(icon,item.title,item.application,async()=>{
        if(button.dataset.longPressed==='true'){button.dataset.longPressed='false';return;}
        try{await api('/windows/activate',{method:'POST',body:JSON.stringify({id:item.id,processId:item.processId})});openRemote(item);}catch(e){toast(e.message);}
      });
      const button=$('page').lastElementChild;
      let timer, startX, startY;
      const cancel=()=>{clearTimeout(timer);timer=null;};
      button.addEventListener('pointerdown',event=>{startX=event.clientX;startY=event.clientY;button.dataset.longPressed='false';timer=setTimeout(async()=>{button.dataset.longPressed='true';if(!confirm('确定关闭“'+item.title+'”吗？应用可能会提示保存内容。'))return;try{await api('/windows/close',{method:'POST',body:JSON.stringify({id:item.id,processId:item.processId})});toast('已向窗口发送关闭请求');await windows();}catch(e){toast(e.message);}},650);});
      button.addEventListener('pointermove',event=>{if(Math.hypot(event.clientX-startX,event.clientY-startY)>12)cancel();});
      button.addEventListener('pointerup',cancel);button.addEventListener('pointercancel',cancel);button.addEventListener('contextmenu',event=>event.preventDefault());
    }
  }catch(e){empty(e.message);}
}
async function terminal() {
  openPage('终端');
  if (!window.Terminal || !window.FitAddon) { empty('终端组件未安装。请在电脑端运行 npm install 后重新构建。'); return; }
  try {
    const shells=await api('/terminal/shells');
    if(state.view!=='终端')return;
    if(!shells.length){empty('未找到可用终端。');return;}
    const bar=element('div','terminal-shell'), select=element('select');
    select.setAttribute('aria-label','终端类型');
    for(const shell of shells){const option=element('option','',shell.name);option.value=shell.id;select.append(option);}
    const reconnect=element('button','','重连');bar.append(select,reconnect);
    const frame=element('div','terminal-frame'), keys=element('div','terminal-keys');
    $('page').append(bar,frame,keys,element('p','terminal-note','点击终端输入。选择文本后可使用复制；也可在终端内通过系统菜单粘贴。切换终端会结束当前会话。'));
    const connect=()=>{
      closeTerminal();frame.replaceChildren();
      const term=new Terminal({cursorBlink:true,convertEol:false,scrollback:5000,fontSize:14,theme:{background:'#090b0a',foreground:'#edf0e9',cursor:'#45ACD7'}});
      const fit=new FitAddon.FitAddon();term.loadAddon(fit);term.open(frame);fit.fit();
      const ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/api/terminal/connect?shell='+encodeURIComponent(select.value));
      ws.binaryType='arraybuffer';
      const session={socket:ws,term,observer:null};terminalSession=session;
      const send=(value)=>{if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify(value));};
      const resize=()=>{if(terminalSession!==session)return;try{fit.fit();send({type:'resize',cols:term.cols,rows:term.rows});}catch{}};
      session.observer=new ResizeObserver(resize);session.observer.observe(frame);
      ws.onopen=()=>{resize();term.focus();};
      ws.onmessage=e=>term.write(new Uint8Array(e.data));
      ws.onclose=()=>{if(terminalSession===session)term.write('\r\n\x1b[90m[终端已断开，点击重连可重新打开]\x1b[0m\r\n');};
      ws.onerror=()=>toast('终端连接失败');
      term.onData(data=>send({type:'input',data}));
      keys.replaceChildren();
      for(const [label,data,direction] of [['Esc','\x1b'],['Tab','\t'],['Ctrl+C','\x03'],['Ctrl+D','\x04'],['向上','\x1b[A','up'],['向下','\x1b[B','down'],['向左','\x1b[D','left'],['向右','\x1b[C','right']]){const b=element('button','');if(direction){b.setAttribute('aria-label',label);b.append(arrowIcon(direction));}else b.textContent=label;b.onclick=()=>{send({type:'input',data});term.focus();};keys.append(b);}
      const copy=element('button','','复制');copy.onclick=async()=>{const text=term.getSelection();if(!text){toast('请先选择终端文本');return;}try{await navigator.clipboard.writeText(text);toast('已复制');}catch{prompt('复制选中的文本',text);}};keys.append(copy);
      const paste=element('button','','粘贴');paste.onclick=async()=>{let text;try{text=await navigator.clipboard.readText();}catch{text=prompt('粘贴文本后确认');}if(text)send({type:'input',data:text});term.focus();};keys.append(paste);
    };
    select.onchange=connect;reconnect.onclick=connect;connect();
  }catch(e){if(state.view==='终端')empty(e.message);}
}
async function files(path) {
  const view=state.picker?'选择工作目录':'文件浏览';openPage(view);const generation=filesGeneration;
  try {
    const data=await api('/fs'+(path?'?path='+encodeURIComponent(path):''));
    if(generation!==filesGeneration||state.view!==view)return;
    state.path=path;state.parentPath=data.parent||null;
    if(data.roots) { for(const root of data.roots) row('▤',root.name,root.ready ? root.type : '不可访问',()=>files(root.name)); }
    else {
      if(state.picker) { const pick=element('button','primary','选择此目录'); pick.onclick=()=>{state.cwd=data.path;$('directory').querySelector('span').textContent=data.path;state.picker=false;home();}; $('page').append(pick); }
      row('arrow-up','上一级',data.parent || '所有位置',()=>files(data.parent));
      for(const item of data.entries) row(item.directory?'▱':'▤',item.name,item.directory?'文件夹':'文件',()=>item.directory?files(item.path):toast('文件预览尚未实现'));
    }
  } catch(e){if(generation===filesGeneration&&state.view===view)empty(e.message);}
}
async function tasks() { openPage('任务列表'); try { const data=await api('/tasks'); const list=Array.isArray(data)?data:(data.sessions || data.live || []); if(!Array.isArray(list)||!list.length){empty('还没有可显示的任务。\n发送消息开始一个新任务。');return;} for(const task of list) {const id=task.id||task.key;row('☷',task.title||task.name||id||'未命名任务',task.status||task.cwd||'',()=>id?taskDetail(id):toast('无法读取会话标识'));} } catch(e){empty(e.message);} }
function syncConversationSettings(snapshot) {
  if(!detailDirty.model&&snapshot.provider&&snapshot.model){
    const index=state.models.findIndex(m=>m.provider===snapshot.provider&&m.name===snapshot.model);
    if(index>=0){$('model').value=String(index);updateLevels(snapshot.intensity);modelSelectionChanged=true;}
    else $('model-short').textContent=snapshot.model+'（不可用）';
  }
  if(!detailDirty.mode&&snapshot.agentMode&&[...$('mode').options].some(o=>o.value===snapshot.agentMode))$('mode').value=snapshot.agentMode;
}
function conversationText(value) { return typeof value==='string'?value:value==null?'':JSON.stringify(value,null,2); }
function messageBubble(role, text, streaming=false) {
  const row=element('article','chat-message '+(role==='user'?'chat-message-user':'chat-message-assistant'));
  const content=element('div','chat-content');
  content.append(element('span','chat-author',role==='user'?'你':'Neutrino'));
  const bubble=element('div','chat-bubble'+(streaming?' is-streaming':''));bubble.textContent=text;content.append(bubble);row.append(content);return row;
}
async function taskDetail(id) {
  if(state.taskId!==null&&state.taskId!==id)leaveTaskDetail();
  if(state.taskId===null){const m=state.models[Number($('model').value)];detailPrevious={provider:m?.provider,model:m?.name,intensity:state.levels[Number($('intensity').value)],mode:$('mode').value,changed:modelSelectionChanged};homeDraft=$('prompt').value;}
  openPage('任务详情');state.taskId=id;state.taskSnapshot=null;detailDirty={model:false,mode:false};const generation=++detailGeneration;clearTimeout(detailTimer);
  $('prompt').value=conversationDrafts.get(id)||'';$('prompt').placeholder='输入消息，继续对话';$('directory').disabled=true;$('directory').title='对话的工作目录不能更改';$('settings-panel').classList.add('hidden');document.body.classList.add('conversation-mode');
  const status=element('div','chat-status','正在加载对话…'),approvalArea=element('div','chat-approval'),log=element('div','chat-log');log.setAttribute('role','log');log.setAttribute('aria-live','polite');$('page').append(status,approvalArea,log);
  let previousMessages='',previousApproval='',firstRender=true,inFlight=false,refreshAgain=false;
  async function refresh(){
    if(generation!==detailGeneration||state.taskId!==id)return;
    if(inFlight){refreshAgain=true;return;}
    inFlight=true;clearTimeout(detailTimer);
    try {
      const data=await api('/tasks/'+encodeURIComponent(id));
      if(generation!==detailGeneration||state.taskId!==id)return;
      const snapshot=data.snapshot||data;state.taskSnapshot=snapshot;syncConversationSettings(snapshot);
      if(snapshot.cwd){$('directory').querySelector('span').textContent=snapshot.cwd;$('directory').title='工作目录：'+snapshot.cwd;}
      status.textContent=snapshot.archived?'历史会话 · 发送消息可尝试恢复':snapshot.approval?'等待审批':snapshot.paused?'已暂停':snapshot.queued?'消息排队中':snapshot.busy?'正在运行':'对话就绪';
      if(snapshot.saveError)status.textContent+=' · 保存失败：'+snapshot.saveError;
      const approval=snapshot.archived?null:snapshot.approval,approvalSignature=JSON.stringify(approval||null);
      if(approvalSignature!==previousApproval){previousApproval=approvalSignature;approvalArea.replaceChildren();if(approval){const card=element('div','approval-card'),heading=element('strong','','需要你的确认'),details=element('pre','',JSON.stringify(approval,null,2)),actions=element('div','approval-actions');card.append(heading,details,actions);for(const [label,approved] of [['批准',true],['拒绝',false]]){const b=element('button',approved?'primary':'approval-reject',label);b.onclick=async()=>{b.disabled=true;try{await api('/tasks/'+encodeURIComponent(id)+'/approval/'+encodeURIComponent(approval.id),{method:'POST',body:JSON.stringify({approved})});void refresh();}catch(e){toast(e.message);}finally{b.disabled=false;}};actions.append(b);}approvalArea.append(card);}}
      const messages=Array.isArray(snapshot.messages)?snapshot.messages:[],stream=typeof snapshot.streamingContent==='string'?snapshot.streamingContent:'',signature=JSON.stringify([messages,stream,!!snapshot.busy]);
      if(signature!==previousMessages){
        const atBottom=firstRender||document.documentElement.scrollHeight-window.scrollY-window.innerHeight<120;previousMessages=signature;log.replaceChildren();
        for(const message of messages){if(message.role==='user'||message.role==='assistant'){const text=conversationText(message.content);if(text)log.append(messageBubble(message.role,text));}else if(message.role==='tool'){const tool=element('details','chat-tool'),summary=element('summary','','工具 · '+(message.name||'执行')),body=element('pre','',conversationText(message.result));tool.append(summary,body);log.append(tool);}else if(message.role==='summary'){log.append(element('div','chat-summary',conversationText(message.content)));}}
        if(stream)log.append(messageBubble('assistant',stream,true));else if(snapshot.busy)log.append(element('div','chat-thinking','Neutrino 正在思考…'));
        if(!log.childElementCount)log.append(element('div','chat-empty','对话已准备好，在下方输入消息。'));
        if(atBottom)requestAnimationFrame(()=>{if(generation===detailGeneration)window.scrollTo(0,document.documentElement.scrollHeight);});firstRender=false;
      }
    } catch(e){if(generation===detailGeneration&&state.taskId===id)status.textContent='连接失败：'+e.message;}
    finally {inFlight=false;if(generation===detailGeneration&&state.taskId===id){const delay=refreshAgain?0:2000;refreshAgain=false;detailTimer=setTimeout(refresh,delay);}}
  }
  detailRefresh=refresh;await refresh();
}
let modelSelectionChanged=false, modelsLoading=null;
async function loadModels() {
  if (modelsLoading) return modelsLoading;
  modelsLoading=(async()=>{try {
    const data=await api('/neutrino/models');
    const previous=state.models[Number($('model').value)];
    const previousLevel=state.levels[Number($('intensity').value)];
    const preferred=modelSelectionChanged && previous ? {provider:previous.provider,model:previous.name,intensity:previousLevel} : data.selection;
    state.models=(data.providers||[]).flatMap(p=>(p.models||[]).map(m=>({provider:p.name,...m})));
    $('model').replaceChildren();
    for(const [i,m] of state.models.entries()){const o=element('option','',`${m.provider} / ${m.name}${m.configured?'':'（未配置密钥）'}`);o.value=String(i);$('model').append(o);}
    let index=state.models.findIndex(m=>m.provider===preferred?.provider&&m.name===preferred?.model);
    if(index<0)index=state.models.findIndex(m=>m.provider===data.selection?.provider&&m.name===data.selection?.model);
    if(index<0&&state.models.length)index=0;
    if(index>=0)$('model').value=String(index);
    const selected=state.models[index];
    const level=selected && selected.provider===preferred?.provider && selected.name===preferred?.model ? preferred.intensity : selected && selected.provider===data.selection?.provider && selected.name===data.selection?.model ? data.selection.intensity : undefined;
    $('model').disabled=!state.models.length;
    if(!state.models.length){const option=element('option','','没有可用模型');option.value='';$('model').append(option);}
    updateLevels(level);
    return true;
  } catch(e){state.models=[];state.levels=[];$('model').replaceChildren(element('option','','无法读取模型配置'));$('model').disabled=true;$('intensity').disabled=true;$('intensity-label').textContent='不可用';$('model-short').textContent='模型不可用';renderIntensity();toast('无法读取模型：'+e.message);return false;}})();
  try{return await modelsLoading;}finally{modelsLoading=null;}
}
const intensityNames={Low:'低',Medium:'中',High:'高',Max:'极高',Ultra:'超高'};
function renderIntensity() {
  const range=$('intensity'),slider=$('intensity-slider'),last=Math.max(0,state.levels.length-1),index=Math.min(last,Math.max(0,Number(range.value)||0));
  const selected=state.levels[index],label=selected?(intensityNames[selected]||selected):'不可用';
  slider.style.setProperty('--pro-slider-position',last?index/last:0);
  slider.classList.toggle('is-disabled',range.disabled);
  $('settings-panel').classList.toggle('is-max',last>0&&index===last);
  $('intensity-label').textContent=label;range.setAttribute('aria-valuetext',label);
  const stops=$('intensity-stops'),ticks=$('intensity-ticks');stops.replaceChildren();ticks.replaceChildren();
  state.levels.forEach((level,i)=>{
    const position=last?i/last*100:0,stop=element('span','pro-slider-stop');stop.style.left=`${position}%`;stop.classList.toggle('is-filled',i<=index);stops.append(stop);
    const tick=element('button','',intensityNames[level]||level);tick.type='button';tick.style.left=`${position}%`;tick.disabled=range.disabled;tick.setAttribute('aria-pressed',String(i===index));tick.onclick=()=>{range.value=String(i);range.dispatchEvent(new Event('input',{bubbles:true}));};ticks.append(tick);
  });
}
function updateLevels(preferred) { const m=state.models[Number($('model').value)]; state.levels=m?.intensities||[]; $('intensity').disabled=!state.levels.length; $('intensity').max=String(Math.max(0,state.levels.length-1));const index=state.levels.indexOf(preferred);const fallback=state.levels.indexOf('High');$('intensity').value=String(index>=0?index:fallback>=0?fallback:0);$('model-short').textContent=m?.name||'模型不可用';renderIntensity(); }
async function loadApps() { const generation=++appsGeneration;try { const apps=await api('/apps');if(generation===appsGeneration&&Array.isArray(apps)){state.apps=apps;renderApps();} } catch(e) { if(generation===appsGeneration)toast('无法读取应用：'+e.message); } }
function stopPresence() { presenceEnabled=false;clearTimeout(presenceRetry);clearInterval(presenceHeartbeat);presenceRetry=presenceHeartbeat=null;const ws=presenceSocket;presenceSocket=null;if(ws)ws.close(); }
function startPresence() {
  presenceEnabled=true;if(document.hidden||presenceSocket)return;
  const ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/api/presence/connect');presenceSocket=ws;
  ws.onopen=()=>{if(presenceSocket!==ws)return;ws.send(JSON.stringify({type:'heartbeat'}));presenceHeartbeat=setInterval(()=>{if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify({type:'heartbeat'}));},15000);};
  ws.onclose=()=>{if(presenceSocket!==ws)return;presenceSocket=null;clearInterval(presenceHeartbeat);presenceHeartbeat=null;if(!presenceEnabled)return;presenceRetry=setTimeout(async()=>{if(!presenceEnabled||document.hidden)return;try{await api('/status');startPresence();}catch(e){if(e.message==='请先配对')presenceEnabled=false;else startPresence();}},3000);};
  ws.onerror=()=>ws.close();
}
document.addEventListener('visibilitychange',()=>{if(!document.hidden&&presenceEnabled){if(presenceSocket){const ws=presenceSocket;presenceSocket=null;ws.close();}clearInterval(presenceHeartbeat);presenceHeartbeat=null;startPresence();}});
window.addEventListener('pagehide',stopPresence);
window.addEventListener('pageshow',event=>{if(event.persisted&&$('pair').classList.contains('hidden')&&$('trust-dialog').classList.contains('hidden'))void api('/status').then(()=>startPresence()).catch(()=>{});});
async function init() { try { const status=await api('/status'); state.cwd=status.desktop; $('connection').querySelector('span').textContent=status.machine; startPresence(); } catch(e) { if(e.message!=='请先配对') toast(e.message); return; } await loadApps(); await loadModels(); }
async function approvalCall(path,token){const response=await fetch('/api/trust/approval'+path,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify({token})});if(!response.ok)throw new Error(response.status===429?'已有待处理申请，请在电脑端处理后重试。':'无法发起设备信任申请。');return response.json();}
function cancelApproval(){approvalSerial++;approvalRunning=false;$('trust-approval').textContent='在电脑端确认信任';if(approvalCandidate)void approvalCall('/cancel',approvalCandidate).catch(()=>{});approvalCandidate=null;try{sessionStorage.removeItem('hlg.trust-approval.v1');}catch{}}
$('trust-approval').classList.toggle('hidden',location.protocol!=='https:');
$('trust-approval').onclick=async()=>{
  if(approvalRunning){cancelApproval();$('approval-status').textContent='已取消申请。';return;}
  const serial=++approvalSerial;approvalRunning=true;$('trust-approval').textContent='取消申请';$('pair-error').textContent='';
  try{
    approvalCandidate=sessionStorage.getItem('hlg.trust-approval.v1');
    if(!approvalCandidate){const random=new Uint8Array(32);crypto.getRandomValues(random);approvalCandidate=Array.from(random,b=>b.toString(16).padStart(2,'0')).join('').toUpperCase();sessionStorage.setItem('hlg.trust-approval.v1',approvalCandidate);}
    const token=approvalCandidate;let result=await approvalCall('',token);
    while(serial===approvalSerial&&result.state==='pending'){
      $('approval-status').textContent='请在电脑端的“连接的设备”中允许信任。';
      await new Promise(resolve=>setTimeout(resolve,1500));if(serial!==approvalSerial)return;
      result=await approvalCall('/status',token);
    }
    if(serial!==approvalSerial)return;
    if(result.state!=='approved')throw new Error('申请已过期或被拒绝，请重新申请。');
    localStorage.setItem(trustStorageKey,token);
    if(!await restoreTrust())throw new Error('设备已获批准，但连接恢复失败，请重试。');
    approvalRunning=false;approvalCandidate=null;sessionStorage.removeItem('hlg.trust-approval.v1');$('trust-approval').textContent='在电脑端确认信任';$('approval-status').textContent='';$('pair').classList.add('hidden');startPresence();await init();
  }catch(error){if(serial!==approvalSerial)return;approvalRunning=false;$('trust-approval').textContent='在电脑端确认信任';$('approval-status').textContent=error.message||'无法申请信任。';}
};
$('pair-form').onsubmit=async e=>{e.preventDefault();try{await api('/pair',{method:'POST',body:JSON.stringify({code:$('pair-code').value})});cancelApproval();$('approval-status').textContent='';startPresence();$('pair').classList.add('hidden');$('pair-error').textContent='';$('pair-code').value='';previousTrustToken=readTrustedToken();trustCandidate=null;$('trust-error').textContent='';$('trust-dialog').classList.remove('hidden');}catch(err){$('pair-error').textContent=err.message;}};
function declineTrust() {
  if(trustCandidate&&readTrustedToken()===trustCandidate){try{if(previousTrustToken)localStorage.setItem(trustStorageKey,previousTrustToken);else localStorage.removeItem(trustStorageKey);}catch{}}
  trustCandidate=null;previousTrustToken=null;$('trust-dialog').classList.add('hidden');void init();
}
$('trust-decline').onclick=declineTrust;
$('trust-accept').onclick=async()=>{
  $('trust-error').textContent='';
  try {
    if(!trustCandidate){
      const random=new Uint8Array(32);crypto.getRandomValues(random);
      const token=Array.from(random,b=>b.toString(16).padStart(2,'0')).join('').toUpperCase();
      localStorage.setItem(trustStorageKey,token);trustCandidate=token;
    }
  } catch { $('trust-error').textContent='无法保存设备信任，请检查浏览器存储权限。';return; }
  $('trust-accept').disabled=true;$('trust-decline').disabled=true;
  try {
    await api('/trust',{method:'POST',body:JSON.stringify({token:trustCandidate})});
    trustCandidate=null;previousTrustToken=null;$('trust-dialog').classList.add('hidden');await init();
  } catch(e){$('trust-error').textContent=e.message;}
  finally{$('trust-accept').disabled=false;$('trust-decline').disabled=false;}
};
$('prompt').addEventListener('focus',()=>{$('composer').classList.add('expanded');$('composer-controls').classList.remove('hidden');$('send').classList.remove('hidden');});
function navigateBack() {
  if(!$('trust-dialog').classList.contains('hidden')){if(!$('trust-decline').disabled)declineTrust();return;}
  if(!$('pair').classList.contains('hidden')){toast('请先完成配对');return;}
  if(!$('remote-text-dialog').classList.contains('hidden')){$('remote-text-dialog').classList.add('hidden');return;}
  if(!$('clipboard-dialog').classList.contains('hidden')){$('clipboard-dialog').classList.add('hidden');return;}
  if(!$('remote-view').classList.contains('hidden')){
    if(!$('remote-special').classList.contains('hidden')){$('remote-special').classList.add('hidden');return;}
    if(!$('remote-menu').classList.contains('hidden')){$('remote-menu').classList.add('hidden');return;}
    window.closeRemote();void windows(true);return;
  }
  if(!$('settings-panel').classList.contains('hidden')){$('settings-panel').classList.add('hidden');return;}
  if(state.view==='任务详情'){void tasks();return;}
  if((state.view==='文件浏览'||state.view==='选择工作目录')&&state.path){void files(state.parentPath);return;}
  if(state.view!=='home'){home();return;}
  if($('composer').classList.contains('expanded')){collapseComposer();return;}
  toast('已在首页');
}
$('back').onclick=()=>{if(state.view==='选择工作目录'){home();return;}navigateBack();};
try {
  history.pushState({hlgBackGuard:true},'');
  window.addEventListener('popstate',()=>{navigateBack();history.pushState({hlgBackGuard:true},'');});
} catch { /* History can be unavailable in restricted browser contexts. */ }
$('directory').onclick=()=>{state.picker=true;files(state.cwd||null);};
$('settings').onclick=()=>{const panel=$('settings-panel');panel.classList.toggle('hidden');if(!panel.classList.contains('hidden')){const taskId=state.taskId;void loadModels().then(ok=>{if(ok&&taskId!==null&&state.taskId===taskId&&state.taskSnapshot)syncConversationSettings(state.taskSnapshot);});}};
document.addEventListener('pointerdown',event=>{const panel=$('settings-panel');if(!panel.classList.contains('hidden')&&!panel.contains(event.target)&&!$('settings').contains(event.target))panel.classList.add('hidden');});
document.addEventListener('keydown',event=>{if(event.key==='Escape')$('settings-panel').classList.add('hidden');});
$('model').onchange=()=>{modelSelectionChanged=true;if(state.taskId!==null)detailDirty.model=true;updateLevels();};
$('intensity').oninput=()=>{modelSelectionChanged=true;if(state.taskId!==null)detailDirty.model=true;renderIntensity();};
$('mode').onchange=()=>{if(state.taskId!==null)detailDirty.mode=true;};
async function sendConversation(id,text) {
  const payload={text};let selected;
  if(detailDirty.model){
    const previous=state.models[Number($('model').value)],previousLevel=state.levels[Number($('intensity').value)];
    if(!await loadModels()||state.taskId!==id)return;
    const model=state.models[Number($('model').value)],level=state.levels[Number($('intensity').value)];
    if(!model||!level){toast('请先在 Neutrino 中配置模型');return;}
    if(!previous||model.provider!==previous.provider||model.name!==previous.name||level!==previousLevel){toast('模型配置已更改，请重新选择');return;}
    selected={provider:model.provider,model:model.name,intensity:level};payload.selection=selected;
  }
  const mode=detailDirty.mode?$('mode').value:null;if(mode)payload.agent_mode=mode;
  await api('/tasks/'+encodeURIComponent(id)+'/message',{method:'POST',body:JSON.stringify(payload)});
  if(state.taskId===id&&state.view==='任务详情'){
    if($('prompt').value.trim()===text)$('prompt').value='';
    const current=state.models[Number($('model').value)],level=state.levels[Number($('intensity').value)];
    if(selected&&current?.provider===selected.provider&&current?.name===selected.model&&level===selected.intensity)detailDirty.model=false;
    if(mode&&$('mode').value===mode)detailDirty.mode=false;
    collapseComposer();if(detailRefresh)void detailRefresh();
  }else if(conversationDrafts.get(id)?.trim()===text)conversationDrafts.delete(id);
}
$('send').onclick=async()=>{const text=$('prompt').value.trim();if(!text)return;const id=state.view==='任务详情'?state.taskId:null;const previous=state.models[Number($('model').value)];const previousLevel=state.levels[Number($('intensity').value)];$('send').disabled=true;try{if(id!==null){await sendConversation(id,text);return;}if(!await loadModels())return;const m=state.models[Number($('model').value)];const level=state.levels[Number($('intensity').value)];if(!m||!level){toast('请先在 Neutrino 中配置模型');return;}if(previous&&modelSelectionChanged&&(m.provider!==previous.provider||m.name!==previous.name||level!==previousLevel)){toast('模型配置已更改，请重新选择');return;}const payload={text,cwd:state.cwd,agent_mode:$('mode').value,selection:{provider:m.provider,model:m.name,intensity:level}};const data=await api('/tasks',{method:'POST',body:JSON.stringify(payload)});if($('prompt').value.trim()===text)$('prompt').value='';collapseComposer();if(data.taskId||data.key)await taskDetail(data.taskId||data.key);else await tasks();}catch(e){toast(e.message);}finally{$('send').disabled=false;}};
renderApps();init();
