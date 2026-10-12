'use strict';
(() => {
  const config=window.BISTUDENT_CONFIG, $=id=>document.getElementById(id);
  const root=`https://firestore.googleapis.com/v1/projects/${encodeURIComponent(config.projectId)}/databases/(default)/documents/groups/${encodeURIComponent(config.groupId)}`;
  let invite=null, code='', stream=null, photos=[], busy=false, opening=false, generation=0;
  const poses=['Снимок 1: смотрите прямо в камеру. В кадре должно быть только ваше лицо.','Снимок 2: слегка поверните голову влево.','Снимок 3: слегка поверните голову вправо.'];
  function notice(text){$('enrollment-notice').textContent=text;$('enrollment-notice').hidden=!text;}
  function stop(){generation++;if(stream)stream.getTracks().forEach(track=>track.stop());stream=null;$('face-video').srcObject=null;$('capture-photo').disabled=true;}
  async function request(url,options={}) {
    const controller=new AbortController(), timeout=setTimeout(()=>controller.abort(),config.requestTimeoutMs||12000);
    try{const response=await fetch(url,{...options,signal:controller.signal,cache:'no-store'});if(!response.ok){const error=new Error(response.status===403||response.status===404?'Код недействителен, истёк или уже использован. Попросите новый код у преподавателя.':response.status===409?'Этот код уже использован.':'Firebase не ответил. Проверьте интернет и попробуйте снова.');error.status=response.status;throw error;}return await response.json();}finally{clearTimeout(timeout);}
  }
  function reset(){photos=[];$('photos').replaceChildren();update();}
  function update(){ $('photo-count').textContent=`${photos.length} / 3`;$('pose-guide').textContent=poses[photos.length]||'Все три снимка готовы. Отправьте их преподавателю.';$('capture-photo').disabled=!stream||photos.length===3||busy||opening;$('send-photos').disabled=photos.length!==3||busy;$('reset-photos').disabled=!photos.length||busy; }
  $('code-form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy)return;const value=$('enrollment-code').value.trim().toLowerCase().replace(/\s/g,'');
    if(!/^[a-f0-9]{40}$/.test(value)){notice('Вставьте полный код, который выдал преподаватель.');return;}
    stop();reset();invite=null;$('capture-section').hidden=true;busy=true;$('check-code').disabled=true;notice('Проверяем код…');
    try{const doc=await request(`${root}/faceInvites/${value}`), fields=doc.fields;
      if(fields.active.integerValue!=='1'||Date.parse(fields.expiresAt.timestampValue)<=Date.now())throw new Error('Срок кода закончился. Попросите новый код.');
      invite={studentId:fields.studentId.stringValue,name:fields.studentName.stringValue};code=value;
      $('student-name').textContent=invite.name;$('capture-section').hidden=false;$('code-form').hidden=true;$('step-code').classList.remove('current');$('step-camera').classList.add('current');notice('');
    }catch(error){notice(error.name==='AbortError'?'Проверка заняла слишком долго. Повторите.':error.message);}finally{busy=false;$('check-code').disabled=false;update();}
  });
  async function openCamera(deviceId=''){
    if(!invite||busy||opening)return;opening=true;stop();const run=generation;
    $('camera-loading').hidden=false;$('start-camera').disabled=true;$('camera-select').disabled=true;notice('');
    try{
      if(!navigator.mediaDevices?.getUserMedia)throw new Error('Камера доступна на HTTPS-сайте или localhost. Откройте сайт через хостинг.');
      const media=await navigator.mediaDevices.getUserMedia({audio:false,video:{width:{ideal:640},height:{ideal:480},...(deviceId?{deviceId:{exact:deviceId}}:{facingMode:'user'})}});
      if(run!==generation){media.getTracks().forEach(track=>track.stop());return;}
      stream=media;const video=$('face-video');video.srcObject=media;
      await video.play();
      if(!video.videoWidth)await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Камера не вернула изображение.')),10000);video.addEventListener('loadeddata',()=>{clearTimeout(timer);resolve();},{once:true});});
      $('camera-placeholder').hidden=true;
      const devices=(await navigator.mediaDevices.enumerateDevices()).filter(item=>item.kind==='videoinput');
      $('camera-select').replaceChildren(...devices.map((item,index)=>new Option(item.label||`Камера ${index+1}`,item.deviceId)));
      $('camera-select').value=media.getVideoTracks()[0].getSettings().deviceId||deviceId;
      media.getVideoTracks()[0].addEventListener('ended',()=>{stop();notice('Камера отключена. Подключите её снова.');});
    }catch(error){stop();$('camera-placeholder').hidden=false;notice(error.name==='NotAllowedError'?'Разрешите сайту доступ к камере в настройках браузера.':error.name==='NotReadableError'?'Камера занята. Закройте другие программы с камерой.':error.message);}finally{opening=false;$('camera-loading').hidden=true;$('start-camera').disabled=false;$('camera-select').disabled=!stream;update();}
  }
  $('start-camera').addEventListener('click',()=>openCamera($('camera-select').value));
  $('camera-select').addEventListener('change',()=>openCamera($('camera-select').value));
  $('capture-photo').addEventListener('click',()=>{
    const video=$('face-video');if(!stream||photos.length>=3||busy||!video.videoWidth)return;
    const canvas=document.createElement('canvas'),scale=Math.min(1,640/video.videoWidth,640/video.videoHeight);canvas.width=Math.round(video.videoWidth*scale);canvas.height=Math.round(video.videoHeight*scale);
    canvas.getContext('2d').drawImage(video,0,0,canvas.width,canvas.height);const url=canvas.toDataURL('image/jpeg',.8),encoded=url.split(',')[1];
    if(encoded.length>330000){notice('Снимок получился слишком большим. Попробуйте переснять.');return;}
    photos.push(encoded);const figure=document.createElement('figure'),image=new Image(),caption=document.createElement('figcaption');image.src=url;image.alt=`Снимок ${photos.length}`;caption.textContent=['Прямо','Немного влево','Немного вправо'][photos.length-1];figure.append(image,caption);$('photos').append(figure);notice('');update();
  });
  $('reset-photos').addEventListener('click',reset);
  $('send-photos').addEventListener('click',async()=>{
    if(!invite||photos.length!==3||busy)return;busy=true;update();$('camera-select').disabled=true;$('send-photos').textContent='Отправляем снимки…';notice('');
    try{
      const payload=JSON.stringify({samples:photos});if(new TextEncoder().encode(payload).length>750000)throw new Error('Набор снимков слишком большой. Переснимите лицо.');
      await request(`${root}/faceSubmissions/${code}?currentDocument.exists=false`,{method:'PATCH',headers:{'Content-Type':'application/json'},body:JSON.stringify({fields:{studentId:{stringValue:invite.studentId},payload:{stringValue:payload}}})});
      stop();photos=[];$('photos').replaceChildren();$('capture-section').hidden=true;$('success-section').hidden=false;$('step-camera').classList.remove('current');$('step-save').classList.add('current');
    }catch(error){notice(error.name==='AbortError'?'Ответ не получен. Снимки могли сохраниться; повторная отправка не заменит ранее сохранённые.':error.message);}finally{busy=false;$('send-photos').textContent='Отправить три снимка';$('camera-select').disabled=!stream;update();}
  });
  window.addEventListener('pagehide',()=>{stop();photos=[];});
})();
