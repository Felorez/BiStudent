const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, 'student-site');
const types = {'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8','.svg':'image/svg+xml'};
http.createServer((req,res) => {
  const url = new URL(req.url,'http://localhost');
  if (url.pathname === '/_qa/populated') {
    const html = fs.readFileSync(path.join(root,'index.html'),'utf8').replaceAll('href="styles.css"','href="/styles.css"').replaceAll('href="icon.svg"','href="/icon.svg"').replace('src="config.js"','src="/config.js"').replace('src="data.js"','src="/data.js"').replace('src="app.js"','src="/_qa/fixture.js" defer></script><script src="/app.js"');
    res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'}); return res.end(html);
  }
  if (url.pathname === '/_qa/fixture.js') {
    const sample = {schema:1,group:'Тестовая группа',teacher:'Ниязова Мадина Ержанкызы',students:[{id:'s1',name:'Тестовый студент'}],subjects:[{id:'csharp',name:'Программирование C#'}],grades:[[0,0,100,'2026-10-08'],[0,0,100,'2026-10-09'],[0,0,85,'2026-10-12']],lessons:[{id:1,subject:'Программирование C#'}],attendance:[[0,0,'2026-10-12','09:03']]};
    res.writeHead(200,{'Content-Type':'text/javascript; charset=utf-8'}); return res.end('BiStudentData.CacheClient = class { async load() { return '+JSON.stringify({data:sample,source:'test',updatedAt:'2026-10-12T10:00:00Z',storageAvailable:true})+'; } };');
  }
  const file = path.resolve(root, '.' + decodeURIComponent(new URL(req.url,'http://localhost').pathname === '/' ? '/index.html' : new URL(req.url,'http://localhost').pathname));
  if (!file.startsWith(root + path.sep)) { res.writeHead(403); return res.end(); }
  fs.readFile(file,(error,content) => { if(error) { res.writeHead(404); return res.end(); } res.writeHead(200,{'Content-Type':types[path.extname(file)]||'text/plain; charset=utf-8'}); res.end(content); });
}).listen(8765,'127.0.0.1',()=>console.log('Preview: http://127.0.0.1:8765'));
