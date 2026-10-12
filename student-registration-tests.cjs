const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync('student-site/register.js','utf8');
function fixture(){
  const elements={},calls=[];let fail=false;
  function element(id){return elements[id]??=( {id,hidden:false,disabled:false,value:'',textContent:'',children:[],events:{},classList:{add(){},remove(){}},addEventListener(name,fn){this.events[name]=fn;},replaceChildren(...children){this.children=children;},append(...children){this.children.push(...children);}} );}
  const video=element('face-video');video.videoWidth=640;video.videoHeight=480;video.play=async()=>{};
  const track={stop(){this.stopped=true;},getSettings(){return {deviceId:'usb-second'};},addEventListener(){}};
  const stream={getTracks:()=>[track],getVideoTracks:()=>[track]};
  const context={window:{BISTUDENT_CONFIG:{projectId:'test',groupId:'test',requestTimeoutMs:100},addEventListener(){}},document:{getElementById:element,createElement(name){if(name==='canvas')return {getContext:()=>({drawImage(){}}),toDataURL:()=> 'data:image/jpeg;base64,dGVzdC1waG90bw=='};return element('generated-'+Math.random());}},navigator:{mediaDevices:{getUserMedia:async options=>{context.cameraOptions=options;return stream;},enumerateDevices:async()=>[{kind:'videoinput',deviceId:'usb-second',label:'College USB camera'}]}},Image:class{},Option:class{constructor(name,value){this.textContent=name;this.value=value;}},AbortController,TextEncoder,setTimeout,clearTimeout,fetch:async(url,options={})=>{calls.push({url,options});if(fail)throw new Error('Offline');return {ok:true,status:200,json:async()=>({fields:{active:{integerValue:'1'},expiresAt:{timestampValue:new Date(Date.now()+60000).toISOString()},studentId:{stringValue:'s1'},studentName:{stringValue:'Тестовый студент'}}})};}};
  vm.runInNewContext(source,context);return {element,calls,context,stream,setFail:value=>fail=value,fire:async(id,event='click')=>element(id).events[event]({preventDefault(){}})};
}
(async()=>{
  const test=fixture(),e=test.element;e('enrollment-code').value='bad';await test.fire('code-form','submit');assert.equal(test.calls.length,0);assert.match(e('enrollment-notice').textContent,/полный код/);
  e('enrollment-code').value='a'.repeat(40);await test.fire('code-form','submit');assert.equal(test.calls.length,1);assert.equal(e('student-name').textContent,'Тестовый студент');assert.equal(e('capture-section').hidden,false);assert.equal(e('send-photos').disabled,true);
  await test.fire('start-camera');assert.equal(e('camera-select').children[0].textContent,'College USB camera');assert.equal(e('capture-photo').disabled,false);assert.equal(test.context.cameraOptions.audio,false);
  await test.fire('capture-photo');await test.fire('capture-photo');assert.equal(e('photo-count').textContent,'2 / 3');assert.equal(test.calls.length,1);assert.equal(e('send-photos').disabled,true);
  await test.fire('reset-photos');assert.equal(e('photos').children.length,0);
  for(let i=0;i<3;i++)await test.fire('capture-photo');assert.equal(e('send-photos').disabled,false);assert.equal(e('capture-photo').disabled,true);
  test.setFail(true);await test.fire('send-photos');assert.equal(e('photos').children.length,3);assert.equal(e('send-photos').disabled,false);test.setFail(false);
  await test.fire('send-photos');assert.equal(e('success-section').hidden,false);assert.equal(e('capture-section').hidden,true);assert.equal(e('photos').children.length,0);
  const submitted=test.calls.at(-1);assert.match(submitted.url,/faceSubmissions\/a{40}\?currentDocument.exists=false/);assert.equal(submitted.options.method,'PATCH');const body=JSON.parse(submitted.options.body);assert.equal(body.fields.studentId.stringValue,'s1');assert.equal(JSON.parse(body.fields.payload.stringValue).samples.length,3);
  const count=test.calls.length;await test.fire('send-photos');assert.equal(test.calls.length,count);
  console.log('OK: bad code no request; one code GET; USB camera selection; 3 photos stay local; reset; failed upload retained; single PATCH with create precondition; repeat prevented');
})().catch(error=>{console.error(error);process.exitCode=1;});
