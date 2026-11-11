(() => {
  const el = id => document.getElementById(id);
  const view=el('remote-view'), canvas=el('remote-canvas'), img=el('remote-image'), pad=el('trackpad');
  const cursor=el('remote-cursor');
  const keyboard=el('remote-keyboard'), special=el('remote-special'), menu=el('remote-menu');
  const modifiers=new Set();
  let socket=null, zoom=1, panX=0, panY=0, shift=false, numeric=false, chinese=false, fn=false, liveUrl=null, heartbeat=null, cursorPosition=null;
  let viewGeneration=0;
  function send(message, disposable=false) { if(socket?.readyState===WebSocket.OPEN && (!disposable || socket.bufferedAmount < 131072)) socket.send(JSON.stringify(message)); }
  function status(text){el('remote-status').textContent=text;}
  function fit(){img.style.transform=`translate(${panX}px,${panY}px) scale(${zoom})`;updateCursor();}
  function sizeImage(){
    if(!img.naturalWidth||!img.naturalHeight||!canvas.clientWidth||!canvas.clientHeight)return;
    const scale=Math.min(canvas.clientWidth/img.naturalWidth,canvas.clientHeight/img.naturalHeight);
    img.style.width=(img.naturalWidth*scale)+'px';img.style.height=(img.naturalHeight*scale)+'px';
    updateCursor();
  }
  function updateCursor(){
    if(!cursorPosition||view.classList.contains('hidden')||!img.naturalWidth){cursor.classList.add('hidden');return;}
    const area=canvas.getBoundingClientRect(),frame=img.getBoundingClientRect();
    if(!area.width||!area.height||!frame.width||!frame.height){cursor.classList.add('hidden');return;}
    const x=frame.left-area.left+cursorPosition.x*frame.width,y=frame.top-area.top+cursorPosition.y*frame.height;
    const outside=cursorPosition.x<0||cursorPosition.x>1||cursorPosition.y<0||cursorPosition.y>1||x<0||x>area.width||y<0||y>area.height;
    cursor.classList.remove('hidden');cursor.classList.toggle('is-edge',outside);
    if(!outside){cursor.style.left=x+'px';cursor.style.top=y+'px';cursor.style.transform='translate(-1px,-1px)';return;}
    const margin=12,limitX=Math.max(margin,area.width-margin),limitY=Math.max(margin,area.height-margin);
    const ox=Math.max(margin,Math.min(limitX,frame.left-area.left+frame.width/2)),oy=Math.max(margin,Math.min(limitY,frame.top-area.top+frame.height/2));
    const dx=x-ox,dy=y-oy;
    const tx=dx>0?(limitX-ox)/dx:dx<0?(margin-ox)/dx:Infinity;
    const ty=dy>0?(limitY-oy)/dy:dy<0?(margin-oy)/dy:Infinity;
    const t=Math.min(tx,ty);
    if(!Number.isFinite(t)){cursor.classList.add('hidden');return;}
    cursor.style.left=(ox+dx*t)+'px';cursor.style.top=(oy+dy*t)+'px';
    cursor.style.transform=`translate(-50%,-50%) rotate(${Math.atan2(dy,dx)*180/Math.PI}deg)`;
  }
  new ResizeObserver(sizeImage).observe(canvas);
  function key(name, extra=[]){send({type:'key',key:name,modifiers:[...new Set([...modifiers,...extra])]});}
  function click(button='left'){send({type:'button',button,down:true});send({type:'button',button,down:false});}
  function showMenu(value){menu.classList.toggle('hidden',!value);if(!value)special.classList.add('hidden');}
  function closeRemote(){
    viewGeneration++; clearInterval(heartbeat);heartbeat=null; if(socket){socket.close();socket=null;} if(liveUrl){URL.revokeObjectURL(liveUrl);liveUrl=null;}
    view.classList.add('hidden');el('app').classList.remove('hidden');el('clipboard-dialog').classList.add('hidden');el('remote-text-dialog').classList.add('hidden');
    img.removeAttribute('src'); img.dataset.loading='';cursorPosition=null;cursor.classList.add('hidden');modifiers.clear();showMenu(false);
    el('remote-keyboard-toggle').classList.remove('active');keyboard.classList.add('hidden');pad.classList.remove('hidden');
    try{screen.orientation?.unlock();}catch{}
  }
  window.openRemote = function(target){
    closeRemote();const generation=++viewGeneration;zoom=1;panX=panY=0;fit();shift=numeric=chinese=fn=false;
    view.classList.remove('hidden');el('app').classList.add('hidden');status('正在连接画面…');
    const query=target?'id='+encodeURIComponent(target.id)+'&pid='+encodeURIComponent(target.processId):'all=1';
    const ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/api/screen/connect?'+query);socket=ws;ws.binaryType='arraybuffer';
    heartbeat=setInterval(()=>send({type:'heartbeat'}),5000);
    ws.onopen=()=>{if(generation===viewGeneration)status('等待画面…');};
    ws.onmessage=event=>{
      if(generation!==viewGeneration)return;
      if(typeof event.data==='string') {try{const info=JSON.parse(event.data);if(info.type==='error'||info.type==='focus')status(info.message);else if(info.type==='frame'){cursorPosition=Number.isFinite(info.cursor?.x)&&Number.isFinite(info.cursor?.y)?info.cursor:null;updateCursor();}}catch{}return;}
      if(img.dataset.loading==='1')return;
      img.dataset.loading='1';const url=URL.createObjectURL(new Blob([event.data],{type:'image/jpeg'}));liveUrl=url;
      img.onload=()=>{URL.revokeObjectURL(url);if(liveUrl===url)liveUrl=null;img.dataset.loading='';if(generation===viewGeneration){sizeImage();if(el('remote-status').textContent==='等待画面…')status('');}};
      img.onerror=()=>{URL.revokeObjectURL(url);if(liveUrl===url)liveUrl=null;img.dataset.loading='';};img.src=url;
    };
    ws.onclose=()=>{if(generation===viewGeneration){clearInterval(heartbeat);heartbeat=null;socket=null;status('连接已断开，请返回后重试。');}};
    ws.onerror=()=>{if(generation===viewGeneration)status('画面连接失败。');};
    renderKeyboard();renderSpecial();
    screen.orientation?.lock?.('landscape').catch(()=>{});
  };
  window.closeRemote=closeRemote;
  el('remote-back').onclick=()=>{closeRemote();windows();};
  el('remote-keyboard-toggle').onclick=()=>{const show=keyboard.classList.contains('hidden');keyboard.classList.toggle('hidden',!show);pad.classList.toggle('hidden',show);el('remote-keyboard-toggle').classList.toggle('active',show);};
  el('remote-special-toggle').onclick=()=>special.classList.toggle('hidden');
  function renderSpecial(){special.replaceChildren();for(const name of ['Ctrl','Alt','Win','Shift','Fn']){const b=document.createElement('button');b.textContent=name;b.classList.toggle('active',name==='Fn'?fn:modifiers.has(name));b.onclick=()=>{if(name==='Fn')fn=!fn;else if(modifiers.has(name))modifiers.delete(name);else modifiers.add(name);renderSpecial();renderKeyboard();};special.append(b);}}
  function renderKeyboard(){
    keyboard.replaceChildren();
    const make=(label,action,cls='',ariaLabel='')=>{const b=document.createElement('button');b.type='button';if(typeof label==='string')b.textContent=label;else b.append(label);if(cls)b.className=cls;if(ariaLabel)b.setAttribute('aria-label',ariaLabel);b.onclick=action;keyboard.append(b);};
    const chars=fn?Array.from({length:12},(_,i)=>'F'+(i+1)):Array.from(numeric?'1234567890-/:;()$&@.,?!\'"#%*+=':'qwertyuiopasdfghjklzxcvbnm');
    for(const c of chars){const name=shift&&!numeric&&!fn?c.toUpperCase():c;make(name,()=>{if(fn)key(c);else if(/^[a-z0-9]$/i.test(c))key(c,shift?['Shift']:[]);else if(modifiers.size)toast('组合键暂不支持此符号');else send({type:'text',text:c});});}
    make(arrowIcon('shift'),()=>{shift=!shift;renderKeyboard();},'wide'+(shift?' active':''),shift?'关闭大写':'开启大写');
    make(numeric?'ABC':'123',()=>{numeric=!numeric;fn=false;renderKeyboard();renderSpecial();},'wide');
    make(chinese?'中文':'英文',()=>{key('Shift');chinese=!chinese;renderKeyboard();toast('切换依赖电脑当前输入法');},'wide');
    make('空格',()=>key('Space'),'space');make('删除',()=>key('Backspace'),'wide');make('回车',()=>key('Enter'),'wide');
    make('手机输入法',()=>{el('remote-text-dialog').classList.remove('hidden');el('remote-text').focus();},'wide');
  }
  el('remote-clipboard').onclick=()=>{el('clipboard-dialog').classList.remove('hidden');};
  el('clipboard-close').onclick=()=>el('clipboard-dialog').classList.add('hidden');
  el('clipboard-read').onclick=async()=>{try{key('C',['Ctrl']);await new Promise(r=>setTimeout(r,250));const result=await api('/clipboard');el('clipboard-text').value=result.text||'';}catch(e){toast(e.message);}};
  el('clipboard-copy').onclick=async()=>{try{await navigator.clipboard.writeText(el('clipboard-text').value);toast('已复制到手机');}catch{el('clipboard-text').focus();el('clipboard-text').select();toast('请使用系统菜单复制');}};
  el('clipboard-paste').onclick=async()=>{try{await api('/clipboard',{method:'POST',body:JSON.stringify({text:el('clipboard-text').value})});key('V',['Ctrl']);el('clipboard-dialog').classList.add('hidden');}catch(e){toast(e.message);}};
  el('remote-text-send').onclick=()=>{const text=el('remote-text').value;if(text.length>4096){toast('每次最多发送 4096 个字符');return;}send({type:'text',text});el('remote-text').value='';el('remote-text-dialog').classList.add('hidden');};
  el('remote-text-close').onclick=()=>el('remote-text-dialog').classList.add('hidden');
  let dragBall=null;
  const ball=el('remote-ball'),floating=el('remote-float');
  ball.onpointerdown=e=>{e.preventDefault();ball.setPointerCapture(e.pointerId);const r=floating.getBoundingClientRect();dragBall={x:e.clientX,y:e.clientY,left:r.left,top:r.top,moved:false};};
  ball.onpointermove=e=>{if(!dragBall)return;const dx=e.clientX-dragBall.x,dy=e.clientY-dragBall.y;if(Math.hypot(dx,dy)>5)dragBall.moved=true;if(dragBall.moved){floating.style.left=Math.max(0,Math.min(innerWidth-44,dragBall.left+dx))+'px';floating.style.top=Math.max(0,Math.min(innerHeight-44,dragBall.top+dy))+'px';floating.style.bottom='auto';}};
  ball.onpointerup=()=>{if(dragBall&&!dragBall.moved)showMenu(menu.classList.contains('hidden'));dragBall=null;};ball.onpointercancel=()=>{dragBall=null;};
  const touches=new Map();let gesture=null;
  function mapped(x,y,clamp=false){const r=img.getBoundingClientRect();if(!r.width||!r.height||(!clamp&&(x<r.left||x>r.right||y<r.top||y>r.bottom)))return null;return {x:Math.max(0,Math.min(1,(x-r.left)/r.width)),y:Math.max(0,Math.min(1,(y-r.top)/r.height))};}
  function moveImage(x,y,clamp=false,disposable=false){const p=mapped(x,y,clamp);if(!p)return false;send({type:'move',...p},disposable);return true;}
  canvas.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();canvas.setPointerCapture(e.pointerId);touches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(touches.size===1)gesture={startX:e.clientX,startY:e.clientY,drag:false,ignored:false};else if(touches.size===2){if(gesture?.drag)send({type:'button',button:'left',down:false});const a=[...touches.values()];gesture={pinch:true,dist:Math.hypot(a[0].x-a[1].x,a[0].y-a[1].y),zoom,midX:(a[0].x+a[1].x)/2,midY:(a[0].y+a[1].y)/2,panX,panY};}};
  canvas.onpointermove=e=>{if(!touches.has(e.pointerId))return;e.preventDefault();touches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(touches.size>=2&&gesture?.pinch){const a=[...touches.values()];zoom=Math.max(1,Math.min(5,gesture.zoom*Math.hypot(a[0].x-a[1].x,a[0].y-a[1].y)/Math.max(1,gesture.dist)));panX=zoom===1?0:gesture.panX+(a[0].x+a[1].x)/2-gesture.midX;panY=zoom===1?0:gesture.panY+(a[0].y+a[1].y)/2-gesture.midY;fit();}else if(touches.size===1&&gesture&&!gesture.pinch&&!gesture.ignored){if(!gesture.drag&&Math.hypot(e.clientX-gesture.startX,e.clientY-gesture.startY)>8){if(moveImage(gesture.startX,gesture.startY)){send({type:'button',button:'left',down:true});gesture.drag=true;}else gesture.ignored=true;}if(gesture.drag)moveImage(e.clientX,e.clientY,true,true);}};
  function imageUp(e,cancelled){if(!touches.has(e.pointerId))return;touches.delete(e.pointerId);if(gesture?.pinch){gesture={ignored:true};return;}if(!gesture?.ignored){if(gesture?.drag)send({type:'button',button:'left',down:false});else if(!cancelled&&moveImage(e.clientX,e.clientY))click();}if(!touches.size)gesture=null;}
  canvas.onpointerup=e=>imageUp(e,false);canvas.onpointercancel=e=>imageUp(e,true);
  const padTouches=new Map();let padGesture=null;
  pad.onpointerdown=e=>{if(e.target.closest('.trackpad-buttons'))return;e.preventDefault();pad.setPointerCapture(e.pointerId);padTouches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(padTouches.size===1)padGesture={x:e.clientX,y:e.clientY,movement:0,multiple:false,remX:0,remY:0};else if(padTouches.size===2){const a=[...padTouches.values()];padGesture={multiple:true,mid:(a[0].y+a[1].y)/2};}};
  pad.onpointermove=e=>{if(!padTouches.has(e.pointerId))return;e.preventDefault();const previous=padTouches.get(e.pointerId);padTouches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(padTouches.size===1&&padGesture&&!padGesture.multiple){const dx=e.clientX-previous.x,dy=e.clientY-previous.y;padGesture.movement+=Math.hypot(dx,dy);padGesture.remX+=dx*2/zoom;padGesture.remY+=dy*2/zoom;const x=Math.trunc(padGesture.remX),y=Math.trunc(padGesture.remY);if(x||y){send({type:'relative',dx:x,dy:y},true);padGesture.remX-=x;padGesture.remY-=y;}}else if(padTouches.size===2){const a=[...padTouches.values()],mid=(a[0].y+a[1].y)/2;if(padGesture?.multiple){const delta=mid-padGesture.mid;if(Math.abs(delta)>1){send({type:'scroll',amount:Math.round(-delta*8)},true);padGesture.mid=mid;}}}};
  function padUp(e,cancelled){if(!padTouches.has(e.pointerId))return;padTouches.delete(e.pointerId);if(!padTouches.size){if(!cancelled&&padGesture&&!padGesture.multiple&&padGesture.movement<8)click();padGesture=null;}}
  pad.onpointerup=e=>padUp(e,false);pad.onpointercancel=e=>padUp(e,true);
  for(const [id,button] of [['mouse-left','left'],['mouse-right','right']]){const b=el(id);b.onpointerdown=e=>{e.preventDefault();e.stopPropagation();b.setPointerCapture(e.pointerId);send({type:'button',button,down:true});};const up=e=>{e.preventDefault();e.stopPropagation();send({type:'button',button,down:false});};b.onpointerup=up;b.onpointercancel=up;}
  window.addEventListener('pagehide',closeRemote);
  document.addEventListener('visibilitychange',()=>{if(document.hidden&&!view.classList.contains('hidden'))closeRemote();});
})();
