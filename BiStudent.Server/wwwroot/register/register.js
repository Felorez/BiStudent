const $ = id => document.getElementById(id);
const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const poses = ['Смотрите прямо в камеру', 'Слегка поверните голову влево', 'Слегка поверните голову вправо'];
let code = '';
let group = null;
let selectedId = null;
let captures = [null, null, null];
let stream = null;
let captureIndex = 0;
let retaking = false;
let sending = false;
let currentStep = 1;
let cameraAttempt = 0;
let registrationGeneration = 0;

function formatCode(value) {
  const clean = [...value.toUpperCase()].filter(char => alphabet.includes(char)).slice(0, 8).join('');
  return clean.length > 4 ? `${clean.slice(0, 4)}-${clean.slice(4)}` : clean;
}

function showStep(number) {
  if (sending && number !== 4) return;
  if (currentStep === 3 && number !== 3) registrationGeneration++;
  currentStep = number;
  for (let index = 1; index <= 4; index++) {
    $(`step-${index}`).hidden = index !== number;
    document.querySelector(`[data-step-label="${index}"]`).classList.toggle('active', index === number);
  }
  if (number !== 3) stopCamera();
  updateButtons();
  window.scrollTo(0, 0);
}

function stopCamera() {
  cameraAttempt++;
  if (stream) stream.getTracks().forEach(track => track.stop());
  stream = null;
  $('video').srcObject = null;
}

async function api(path, body, generation) {
  let response;
  try {
    response = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (generation !== registrationGeneration) return null;
  } catch {
    throw new Error('Нет связи с сервером. Проверьте подключение и повторите попытку.');
  }
  if (response.status === 429) throw new Error('Слишком много попыток — подождите минуту.');
  let data;
  try {
    data = await response.json();
    if (generation !== registrationGeneration) return null;
  }
  catch { throw new Error('Сервер вернул неожиданный ответ. Повторите попытку.'); }
  if (!response.ok) throw new Error(data.message || 'Не удалось выполнить запрос.');
  return data;
}

function updateButtons() {
  $('student-next').disabled = sending || selectedId === null || !$('consent').checked;
  $('back').disabled = sending;
  $('another').disabled = sending;
  $('camera-select').disabled = sending;
  $('submit').disabled = sending || currentStep !== 3 || captures.some(image => !image);
  $('capture').disabled = sending || currentStep !== 3 || !stream ||
    (captures[captureIndex] !== null && !retaking);
  for (let index = 0; index < 3; index++) $(`file-${index}`).disabled = sending;
  $('thumbnails').querySelectorAll('button').forEach(button => { button.disabled = sending; });
}

function updateNext() {
  updateButtons();
  const selected = group?.students.find(student => student.id === selectedId);
  $('replacement-warning').hidden = !selected || selected.state === 'None';
}

function renderStudents() {
  const list = $('students');
  list.replaceChildren();
  const query = $('student-search').value.trim().toLocaleLowerCase('ru');
  for (const student of group.students.filter(item => item.fullName.toLocaleLowerCase('ru').includes(query))) {
    const label = document.createElement('label');
    label.className = 'student-card';
    const radio = document.createElement('input');
    radio.type = 'radio';
    radio.name = 'student';
    radio.value = student.id;
    radio.checked = student.id === selectedId;
    radio.addEventListener('change', () => {
      if (selectedId !== student.id) registrationGeneration++;
      selectedId = student.id;
      updateNext();
    });
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = student.fullName;
    label.append(radio, name);
    if (student.state !== 'None') {
      const badge = document.createElement('span');
      badge.className = `badge${student.state === 'Pending' ? ' pending' : ''}`;
      badge.textContent = student.state === 'Pending' ? 'На проверке' : 'Лицо зарегистрировано';
      label.append(badge);
    }
    list.append(label);
  }
}

async function checkCode() {
  const generation = ++registrationGeneration;
  $('code-error').textContent = '';
  code = formatCode($('code').value);
  $('code').value = code;
  if (code.replace('-', '').length !== 8) {
    $('code-error').textContent = 'Введите восьмизначный код группы.';
    return;
  }
  $('code-button').disabled = true;
  try {
    const checkedGroup = await api('/api/public/enrollment/check', { code }, generation);
    if (generation !== registrationGeneration) return;
    group = checkedGroup;
    $('group-name').textContent = group.groupName;
    const expires = new Date(group.expiresAt);
    $('expires').textContent = `Код действует до ${expires.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}`;
    selectedId = null;
    $('consent').checked = false;
    $('student-search').value = '';
    renderStudents();
    updateNext();
    showStep(2);
  } catch (error) {
    if (generation === registrationGeneration) $('code-error').textContent = error.message;
  } finally {
    if (generation === registrationGeneration) $('code-button').disabled = false;
  }
}

function updateCaptures() {
  const list = $('thumbnails');
  list.replaceChildren();
  for (let index = 0; index < 3; index++) {
    const item = document.createElement('div');
    item.className = 'thumb';
    if (captures[index]) {
      const image = document.createElement('img');
      image.src = `data:image/jpeg;base64,${captures[index]}`;
      image.alt = `Снимок ${index + 1}`;
      item.append(image);
      const retake = document.createElement('button');
      retake.type = 'button';
      retake.className = 'secondary';
      retake.textContent = 'Переснять';
      retake.addEventListener('click', () => {
        if (sending) return;
        if (stream) {
          captureIndex = index;
          retaking = true;
          updatePose();
          updateButtons();
          $('capture').focus();
        } else {
          const input = $(`file-${index}`);
          input.value = '';
          input.click();
        }
      });
      item.append(retake);
    } else {
      const placeholder = document.createElement('div');
      placeholder.className = 'thumb-placeholder';
      placeholder.textContent = `Снимок ${index + 1}`;
      item.append(placeholder);
    }
    list.append(item);
  }
  updateButtons();
  updatePose();
}

function updatePose() {
  $('pose').textContent = `${captureIndex + 1}. ${poses[captureIndex]}`;
}

function scaledJpeg(source, width, height) {
  const ratio = Math.min(1, 1280 / Math.max(width, height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  canvas.getContext('2d').drawImage(source, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL('image/jpeg', 0.9).split(',')[1];
}

async function startCamera(deviceId) {
  stopCamera();
  const generation = registrationGeneration;
  const attempt = cameraAttempt;
  if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
    $('camera-mode').hidden = true;
    $('file-mode').hidden = false;
    updateCaptures();
    return;
  }
  try {
    const nextStream = await navigator.mediaDevices.getUserMedia({
      video: deviceId ? { deviceId: { exact: deviceId }, width: { ideal: 1280 }, height: { ideal: 720 } }
        : { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } },
      audio: false
    });
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) {
      nextStream.getTracks().forEach(track => track.stop());
      return;
    }
    stream = nextStream;
    $('video').srcObject = stream;
    $('camera-mode').hidden = false;
    $('file-mode').hidden = true;
    const devices = (await navigator.mediaDevices.enumerateDevices()).filter(item => item.kind === 'videoinput');
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) return;
    $('camera-select').replaceChildren();
    devices.forEach((device, index) => {
      const option = document.createElement('option');
      option.value = device.deviceId;
      option.textContent = device.label || `Камера ${index + 1}`;
      $('camera-select').append(option);
    });
    $('camera-select').value = stream.getVideoTracks()[0].getSettings().deviceId || '';
    $('camera-select').hidden = $('camera-label').hidden = devices.length <= 1;
  } catch {
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) return;
    stopCamera();
    $('camera-mode').hidden = true;
    $('file-mode').hidden = false;
  }
  updateCaptures();
}

$('code').addEventListener('input', event => { event.target.value = formatCode(event.target.value); });
$('code-form').addEventListener('submit', event => { event.preventDefault(); checkCode(); });
$('student-search').addEventListener('input', renderStudents);
$('consent').addEventListener('change', updateNext);
$('student-next').addEventListener('click', () => {
  if (sending) return;
  captures = [null, null, null];
  for (let index = 0; index < 3; index++) $(`file-${index}`).value = '';
  captureIndex = 0;
  retaking = false;
  $('submit-error').textContent = '';
  $('problems').replaceChildren();
  updateCaptures();
  showStep(3);
  startCamera();
});
$('camera-select').addEventListener('change', event => { if (!sending) startCamera(event.target.value); });
$('capture').addEventListener('click', () => {
  if (sending || currentStep !== 3 || !stream) return;
  const video = $('video');
  if (!video.videoWidth || !video.videoHeight) return;
  captures[captureIndex] = scaledJpeg(video, video.videoWidth, video.videoHeight);
  retaking = false;
  captureIndex = captures.findIndex(image => !image);
  if (captureIndex < 0) captureIndex = 2;
  updateCaptures();
});
for (let index = 0; index < 3; index++) {
  $(`file-${index}`).addEventListener('change', async event => {
    if (sending || currentStep !== 3) return;
    const file = event.target.files?.[0];
    if (!file) return;
    const generation = registrationGeneration;
    try {
      const bitmap = await createImageBitmap(file);
      if (generation !== registrationGeneration || sending || currentStep !== 3) { bitmap.close(); return; }
      captures[index] = scaledJpeg(bitmap, bitmap.width, bitmap.height);
      bitmap.close();
      updateCaptures();
      $('submit-error').textContent = '';
    } catch {
      if (generation === registrationGeneration)
        $('submit-error').textContent = 'Не удалось прочитать снимок. Выберите другой файл.';
    }
  });
}
$('back').addEventListener('click', () => showStep(2));
$('submit').addEventListener('click', async () => {
  if (sending || captures.some(image => !image)) return;
  const studentId = selectedId;
  const studentName = group.students.find(student => student.id === studentId).fullName;
  const images = [...captures];
  const generation = ++registrationGeneration;
  sending = true;
  updateButtons();
  $('submit').textContent = 'Отправляем…';
  $('submit-error').textContent = '';
  $('problems').replaceChildren();
  try {
    const result = await api('/api/public/enrollment/submit', { code, studentId, images, consent: true }, generation);
    if (generation !== registrationGeneration) return;
    if (!result.ok) {
      $('submit-error').textContent = result.message;
      for (const problem of result.problems || []) {
        const item = document.createElement('li');
        item.textContent = problem;
        $('problems').append(item);
      }
      return;
    }
    $('completed-student').textContent = studentName;
    $('completed-message').textContent = result.message;
    sending = false;
    $('submit').textContent = 'Отправить';
    showStep(4);
  } catch (error) {
    if (generation === registrationGeneration) $('submit-error').textContent = error.message;
  } finally {
    if (generation === registrationGeneration) {
      sending = false;
      $('submit').textContent = 'Отправить';
      updateCaptures();
    }
  }
});
$('another').addEventListener('click', async () => {
  if (sending) return;
  const generation = ++registrationGeneration;
  selectedId = null;
  captures = [null, null, null];
  for (let index = 0; index < 3; index++) $(`file-${index}`).value = '';
  $('consent').checked = false;
  $('student-search').value = '';
  try {
    const checkedGroup = await api('/api/public/enrollment/check', { code }, generation);
    if (generation !== registrationGeneration) return;
    group = checkedGroup;
  }
  catch (error) {
    if (generation === registrationGeneration) $('student-error').textContent = error.message;
  }
  if (generation !== registrationGeneration) return;
  renderStudents();
  updateNext();
  showStep(2);
});
showStep(1);
const queryCode = new URLSearchParams(location.search).get('code');
if (queryCode) { $('code').value = formatCode(queryCode); checkCode(); }
