(function (root) {
  'use strict';
  const emptyData = () => ({schema: 1, group: '23-ПО-103-4р-2', teacher: 'Ниязова Мадина Ержанкызы', students: [], subjects: [], grades: [], lessons: [], attendance: []});
  function validate(data) {
    if (!data || data.schema !== 1 || !['students','subjects','grades','lessons','attendance'].every(k => Array.isArray(data[k]))) throw new Error('Неверный формат статистики.');
    if (!data.students.every(s => typeof s.name === 'string' && typeof s.id === 'string') || !data.subjects.every(s => typeof s.name === 'string' && typeof s.id === 'string')) throw new Error('Неверный список студентов или предметов.');
    if (!data.grades.every(g => Array.isArray(g) && Number.isInteger(g[0]) && g[0] >= 0 && g[0] < data.students.length && Number.isInteger(g[1]) && g[1] >= 0 && g[1] < data.subjects.length && Number.isInteger(g[2]) && g[2] >= 0 && g[2] <= 100 && /^\d{4}-\d{2}-\d{2}$/.test(g[3]))) throw new Error('Неверные оценки.');
    if (!data.attendance.every(a => Array.isArray(a) && Number.isInteger(a[0]) && a[0] >= 0 && a[0] < data.students.length && Number.isInteger(a[1]) && a[1] >= 0 && a[1] < data.lessons.length && /^\d{4}-\d{2}-\d{2}$/.test(a[2]) && typeof a[3] === 'string')) throw new Error('Неверная посещаемость.');
    return data;
  }
  class CacheClient {
    constructor(config, dependencies = {}) {
      this.config = config; this.memory = new Map(); this.storageAvailable = true;
      try { this.storage = dependencies.storage || root.localStorage; }
      catch { this.storageAvailable = false; this.storage = {getItem() { throw new Error('Storage unavailable'); }, setItem() { throw new Error('Storage unavailable'); }}; }
      this.fetcher = dependencies.fetcher || root.fetch.bind(root); this.now = dependencies.now || Date.now;
      this.lock = dependencies.lock || (root.navigator?.locks ? callback => root.navigator.locks.request(this.key + ':lock', callback) : null);
    }
    get key() { return 'bistudent:public:v1:' + this.config.projectId + ':' + this.config.groupId; }
    read(key) { try { const value = this.storage.getItem(key); return value ? JSON.parse(value) : null; } catch { this.storageAvailable = false; return this.memory.get(key) || null; } }
    write(key, value) { this.memory.set(key, value); try { this.storage.setItem(key, JSON.stringify(value)); } catch { this.storageAvailable = false; } }
    cached() { const item = this.read(this.key); try { return item && typeof item.savedAt === 'number' && item.savedAt <= this.now() ? {...item, data: validate(item.data)} : null; } catch { return null; } }
    result(item, source, error = '') { return {data: item?.data || emptyData(), updatedAt: item?.updatedAt || '', savedAt: item?.savedAt || 0, source, error, storageAvailable: this.storageAvailable}; }
    async load() {
      const cached = this.cached();
      if (cached && this.now() - cached.savedAt < this.config.cacheTtlMs) return this.result(cached, 'cache');
      if (this.lock) return this.lock(() => this.loadOnce());
      const key = this.key + ':lease'; const owner = Math.random().toString(36).slice(2);
      for (let i = 0; i < 85; i++) {
        const fresh = this.cached(); if (fresh && this.now() - fresh.savedAt < this.config.cacheTtlMs) return this.result(fresh, 'cache');
        const lease = this.read(key);
        if (!lease || lease.until < this.now()) {
          this.write(key, {owner, until: this.now() + this.config.requestTimeoutMs + 2000});
          await new Promise(resolve => setTimeout(resolve, 25));
          if (this.read(key)?.owner === owner || !this.storageAvailable) {
            try { return await this.loadOnce(); }
            finally { if (this.read(key)?.owner === owner) this.write(key, {owner: '', until: 0}); }
          }
        }
        await new Promise(resolve => setTimeout(resolve, 200));
      }
      return this.result(this.cached(), 'stale', 'Обновление уже выполняется в другой вкладке. Попробуйте позже.');
    }
    async loadOnce() {
      const cached = this.cached();
      if (cached && this.now() - cached.savedAt < this.config.cacheTtlMs) return this.result(cached, 'cache');
      const cooldown = this.read(this.key + ':retry');
      if (cooldown && cooldown.until > this.now()) return this.result(cached, 'stale', cooldown.message);
      const controller = new AbortController(); const timeout = setTimeout(() => controller.abort(), this.config.requestTimeoutMs);
      try {
        const url = 'https://firestore.googleapis.com/v1/projects/' + encodeURIComponent(this.config.projectId) + '/databases/(default)/documents/publicStats/' + encodeURIComponent(this.config.groupId);
        const response = await this.fetcher(url, {method: 'GET', credentials: 'omit', cache: 'no-store', signal: controller.signal});
        let item;
        if (response.status === 404) item = {data: emptyData(), updatedAt: '', savedAt: this.now()};
        else {
          if (!response.ok) throw new Error(response.status === 403 ? 'Firebase пока не разрешает чтение статистики. Преподавателю нужно опубликовать правила доступа.' : 'Не удалось получить статистику из Firebase.');
          const document = await response.json();
          const data = validate(JSON.parse(document.fields?.payload?.stringValue || 'null'));
          item = {data, updatedAt: document.fields?.updatedAt?.stringValue || '', savedAt: this.now()};
        }
        this.write(this.key, item); this.write(this.key + ':retry', {until: 0}); return this.result(item, 'firebase');
      } catch (error) {
        const message = error?.name === 'AbortError' ? 'Firebase не ответил вовремя. Попробуйте обновить страницу позже.' : error?.message === 'Failed to fetch' ? 'Нет соединения. Показываем последнюю сохранённую статистику, если она есть.' : error?.message || 'Не удалось загрузить данные.';
        this.write(this.key + ':retry', {until: this.now() + this.config.failureCooldownMs, message}); return this.result(cached, 'stale', message);
      } finally { clearTimeout(timeout); }
    }
  }
  const api = {CacheClient, validate, emptyData};
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.BiStudentData = api;
})(typeof window !== 'undefined' ? window : globalThis);
