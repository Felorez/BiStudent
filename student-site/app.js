(function () {
  'use strict';
  const $ = id => document.getElementById(id);
  const format = new Intl.NumberFormat('ru-RU', {maximumFractionDigits: 1});
  const mean = scores => scores.length ? scores.reduce((sum, x) => sum + x, 0) / scores.length : null;
  const formatted = value => value == null ? '—' : format.format(value);
  const dateText = date => { const parts = date.split('-'); return parts.length === 3 ? parts[2] + '.' + parts[1] + '.' + parts[0] : date; };
  let data = BiStudentData.emptyData(); let tab = 'grades';
  function text(tag, value, className = '') { const node = document.createElement(tag); node.textContent = value; if (className) node.className = className; return node; }
  function svg(tag, attributes = {}, content = '') { const node = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value); if (content) node.textContent = content; return node; }
  function drawChart() {
    const container = $('chart'); container.replaceChildren();
    if (!data.grades.length) {
      const empty = text('div', '', 'empty-chart');
      const wave = svg('svg', {viewBox: '0 0 1200 70', preserveAspectRatio: 'none', 'aria-hidden': 'true'});
      wave.append(svg('path', {d: 'M0 35 C50 15 100 15 150 35 S250 55 300 35 S400 15 450 35 S550 55 600 35 S700 15 750 35 S850 55 900 35 S1000 15 1050 35 S1150 55 1200 35'}));
      const caption = text('div', '', 'empty-caption'); caption.append(text('strong', 'Пока данных нет'), text('span', 'График появится после первых оценок'));
      empty.append(wave, caption); container.append(empty); return;
    }
    const byDate = new Map(); for (const grade of data.grades) { if (!byDate.has(grade[3])) byDate.set(grade[3], []); byDate.get(grade[3]).push(grade[2]); }
    let sum = 0, count = 0;
    const series = [...byDate].sort(([a], [b]) => a.localeCompare(b)).map(([date, scores]) => { for (const value of scores) { sum += value; count++; } return {date, average: sum / count}; });
    const width = 1040, height = 280, left = 40, right = 17, top = 17, bottom = 33;
    const y = value => top + (100 - value) / 100 * (height - top - bottom);
    const x = index => series.length === 1 ? (left + width - right) / 2 : left + index / (series.length - 1) * (width - left - right);
    const plot = svg('svg', {viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': 'Средний балл группы по датам: ' + series.map(p => dateText(p.date) + ': ' + formatted(p.average)).join(', ')});
    const defs = svg('defs'); const gradient = svg('linearGradient', {id: 'area-fill', x1: '0', x2: '0', y1: '0', y2: '1'}); gradient.append(svg('stop', {offset: '0%', 'stop-color': '#a090ff', 'stop-opacity': '.19'}), svg('stop', {offset: '100%', 'stop-color': '#a090ff', 'stop-opacity': '0'})); defs.append(gradient); plot.append(defs);
    for (const value of [0, 25, 50, 75, 100]) { plot.append(svg('line', {x1: left, x2: width - right, y1: y(value), y2: y(value), class: 'chart-grid'}), svg('text', {x: 1, y: y(value) + 4, class: 'chart-text'}, String(value))); }
    const line = series.map((point, index) => `${index ? 'L' : 'M'}${x(index).toFixed(2)} ${y(point.average).toFixed(2)}`).join(' ');
    if (series.length > 1) { plot.append(svg('path', {d: line + ` L${x(series.length - 1)} ${y(0)} L${x(0)} ${y(0)} Z`, class: 'chart-area'}), svg('path', {d: line, class: 'chart-line', pathLength: '1'})); }
    const stride = Math.max(1, Math.ceil(series.length / 5));
    series.forEach((point, index) => {
      if (series.length < 30 || index === series.length - 1) { const dot = svg('circle', {cx: x(index), cy: y(point.average), r: 4, class: 'chart-point'}); dot.append(svg('title', {}, dateText(point.date) + ' · ' + formatted(point.average))); plot.append(dot); }
      if (index % stride === 0 || index === series.length - 1) plot.append(svg('text', {x: x(index), y: height - 5, class: 'chart-text', 'text-anchor': 'middle'}, dateText(point.date)));
    });
    if (series.length === 1) plot.append(svg('text', {x: x(0) + 12, y: y(series[0].average) - 9, class: 'chart-text'}, formatted(series[0].average)));
    container.append(plot);
  }
  function drawTable() {
    const filter = $('subject-filter').value; const search = $('search').value.trim().toLocaleLowerCase('ru');
    const head = $('table-head'); const body = $('table-body'); head.replaceChildren(); body.replaceChildren();
    const headings = tab === 'grades' ? ['Студент', 'Предмет', 'Все оценки', 'Средний балл'] : ['Студент', 'Занятие', 'Дата и время', 'Посещаемость'];
    const title = document.createElement('tr'); for (const heading of headings) { const th = text('th', heading); th.scope = 'col'; title.append(th); } head.append(title);
    let rows = 0;
    const includesStudent = index => data.students[index]?.name.toLocaleLowerCase('ru').includes(search);
    if (tab === 'grades' && data.grades.length) {
      data.students.forEach((student, studentIndex) => {
        if (!includesStudent(studentIndex)) return;
        data.subjects.forEach((subject, subjectIndex) => {
          if (filter && subject.id !== filter) return;
          const grades = data.grades.filter(g => g[0] === studentIndex && g[1] === subjectIndex).sort((a, b) => a[3].localeCompare(b[3]));
          const row = document.createElement('tr'); const who = document.createElement('td'); who.append(text('span', student.name, 'student-name'), text('span', data.group, 'student-sub'));
          const subjectCell = text('td', subject.name, 'subject-name'); const scores = document.createElement('td');
          if (grades.length) { const list = text('div', '', 'grade-list'); for (const g of grades) { const chip = text('span', String(g[2]), 'grade-chip' + (g[2] < 50 ? ' low' : g[2] < 80 ? ' mid' : '')); chip.title = dateText(g[3]); list.append(chip); } scores.append(list); } else scores.append(text('span', '—', 'muted'));
          const average = text('td', formatted(mean(grades.map(g => g[2]))), 'average-cell'); average.append(text('span', grades.length ? 'Оценок: ' + grades.length : 'Нет оценок', 'grade-total'));
          row.append(who, subjectCell, scores, average); body.append(row); rows++;
        });
      });
    } else if (tab === 'attendance') {
      [...data.attendance].sort((a, b) => b[2].localeCompare(a[2]) || a[0] - b[0]).forEach(entry => {
        const [studentIndex, lessonIndex, date, time] = entry; const lesson = data.lessons[lessonIndex];
        if (!includesStudent(studentIndex) || filter && data.subjects.find(s => s.id === filter)?.name !== lesson.subject) return;
        const row = document.createElement('tr'); const who = text('td', data.students[studentIndex].name, 'student-name');
        const status = document.createElement('td'); const badge = text('span', 'Присутствовал', 'present-badge'); const dot = text('i', '', 'dot cyan'); badge.prepend(dot); status.append(badge);
        row.append(who, text('td', lesson.subject, 'subject-name'), text('td', dateText(date) + (time ? ' · ' + time : '')), status); body.append(row); rows++;
      });
    }
    body.classList.toggle('empty-table', rows === 0);
    $('journal-table').classList.toggle('empty-state', rows === 0);
    if (!rows) { const row = document.createElement('tr'); const cell = text('td', search || filter ? 'Данных нет по выбранным фильтрам' : 'Данных нет'); cell.colSpan = 4; row.append(cell); body.append(row); }
    $('rows-count').textContent = rows ? 'Строк: ' + rows : '';
    $('table-description').textContent = tab === 'grades' ? rows ? 'Все оценки по шести предметам · дата оценки при наведении' : 'Пока данных нет' : rows ? 'Сохранённые отметки присутствия' : 'Пока отметок посещаемости нет';
  }
  function setTab(value) {
    tab = value;
    for (const [id, name] of [['grades-tab','grades'],['attendance-tab','attendance']]) { $(id).classList.toggle('active', value === name); $(id).setAttribute('aria-selected', String(value === name)); }
    drawTable();
  }
  function render(result) {
    data = result.data; $('group-pill').textContent = data.group; $('teacher-name').textContent = data.teacher;
    $('average').textContent = formatted(mean(data.grades.map(g => g[2])));
    $('students-count').textContent = data.students.length || '—'; $('grades-count').textContent = data.grades.length || '—'; $('attendance-count').textContent = data.attendance.length || '—';
    const timestamp = new Date(result.updatedAt);
    $('updated-label').textContent = result.updatedAt && Number.isFinite(timestamp.getTime()) ? 'Обновлено ' + timestamp.toLocaleString('ru-RU', {timeZone: 'Asia/Qyzylorda', day:'2-digit',month:'2-digit',hour:'2-digit',minute:'2-digit'}) : 'Ожидаем первые данные';
    $('cache-label').textContent = result.source === 'cache' ? 'Сохранённая копия · кэш 1 минута' : result.source === 'stale' ? 'Последняя доступная копия' : 'Данные из Firebase · кэш 1 минута';
    if (result.error || !result.storageAvailable) { $('notice').hidden = false; $('notice').textContent = result.error || 'Браузер запретил локальное хранение. Для кэша разрешите данные сайта.'; }
    for (const subject of data.subjects) { const option = text('option', subject.name); option.value = subject.id; $('subject-filter').append(option); }
    drawChart(); drawTable();
  }
  $('grades-tab').addEventListener('click', () => setTab('grades')); $('attendance-tab').addEventListener('click', () => setTab('attendance'));
  $('search').addEventListener('input', drawTable); $('subject-filter').addEventListener('change', drawTable);
  (async () => {
    try { const client = new BiStudentData.CacheClient(BISTUDENT_CONFIG); render(await client.load()); }
    catch { render({data: BiStudentData.emptyData(), source:'stale', updatedAt:'', storageAvailable:true, error:'Не удалось загрузить статистику. Попробуйте обновить страницу позже.'}); }
    finally { $('preloader').classList.add('done'); }
  })();
})();
