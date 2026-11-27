const entries = document.getElementById('entries');
const rows = Array.from(entries.querySelectorAll('.item'));
const search = document.getElementById('search');
const kind = document.getElementById('kind');
const timeFilter = document.getElementById('timeFilter');
const sort = document.getElementById('sort');
const dateRange = document.getElementById('dateRange');
const dateFrom = document.getElementById('dateFrom');
const dateTo = document.getElementById('dateTo');
const dateError = document.getElementById('dateError');
const resultCount = document.getElementById('resultCount');
const empty = document.getElementById('empty');
const pageSize = document.getElementById('pageSize');
const prevPage = document.getElementById('prevPage');
const nextPage = document.getElementById('nextPage');
const pageInfo = document.getElementById('pageInfo');
const collator = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' });
const preferencesKey = 'kutu.file-list.preferences.v1';
let page = 1;
let filteredRows = rows;

function restorePreferences() {
  try {
    const saved = JSON.parse(localStorage.getItem(preferencesKey));
    if (!saved || typeof saved !== 'object') return;
    const allowed = [
      [kind, ['all', 'folder', 'image', 'document', 'video', 'audio', 'archive', 'other'], saved.kind],
      [timeFilter, ['all', 'today', '7d', '30d', 'custom'], saved.timeFilter],
      [sort, ['recent', 'name', 'oldest', 'largest', 'smallest'], saved.sort],
      [pageSize, ['10', '25', '50', '100'], saved.pageSize]
    ];
    for (const [control, values, value] of allowed) {
      if (values.includes(value)) control.value = value;
    }
    if (/^\d{4}-\d{2}-\d{2}$/.test(saved.dateFrom || '')) dateFrom.value = saved.dateFrom;
    if (/^\d{4}-\d{2}-\d{2}$/.test(saved.dateTo || '')) dateTo.value = saved.dateTo;
  } catch { /* Storage may be unavailable; the page still works with defaults. */ }
}

function savePreferences() {
  try {
    localStorage.setItem(preferencesKey, JSON.stringify({
      kind: kind.value, timeFilter: timeFilter.value, sort: sort.value,
      pageSize: pageSize.value, dateFrom: dateFrom.value, dateTo: dateTo.value
    }));
  } catch { /* Browsing remains usable when storage is blocked. */ }
}

restorePreferences();

for (const row of rows) {
  const time = row.querySelector('time');
  time.textContent = new Date(Number(row.dataset.time)).toLocaleString('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false
  });
}

function range() {
  const value = timeFilter.value;
  if (value === 'today') {
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    return [start.getTime(), Infinity];
  }
  if (value === '7d') return [Date.now() - 7 * 86400000, Infinity];
  if (value === '30d') return [Date.now() - 30 * 86400000, Infinity];
  if (value === 'custom') {
    const start = dateFrom.value ? new Date(dateFrom.value + 'T00:00:00').getTime() : -Infinity;
    const end = dateTo.value ? new Date(dateTo.value + 'T00:00:00').getTime() + 86400000 : Infinity;
    return [start, end];
  }
  return [-Infinity, Infinity];
}

function renderPage() {
  const size = Number(pageSize.value);
  const pageCount = Math.max(1, Math.ceil(filteredRows.length / size));
  page = Math.min(page, pageCount);
  const first = (page - 1) * size;
  const shown = new Set(filteredRows.slice(first, first + size));
  for (const row of rows) row.hidden = !shown.has(row);
  resultCount.textContent = filteredRows.length === 0
    ? `显示 0 项 · 共 ${rows.length} 项`
    : `显示 ${first + 1}–${Math.min(first + size, filteredRows.length)} 项 · 匹配 ${filteredRows.length} 项`;
  empty.hidden = filteredRows.length !== 0;
  empty.textContent = rows.length === 0 ? '这个文件夹是空的' : '没有匹配的文件。试试清除筛选。';
  pageInfo.textContent = `第 ${page} / ${pageCount} 页`;
  prevPage.disabled = page <= 1;
  nextPage.disabled = page >= pageCount;
}

function applyFilters() {
  dateRange.hidden = timeFilter.value !== 'custom';
  const [start, end] = range();
  dateError.hidden = start < end;
  if (!dateError.hidden) return;

  const query = search.value.trim().normalize('NFKC').toLocaleLowerCase('zh-CN');
  const selectedKind = kind.value;
  const selectedSort = sort.value;
  rows.sort((a, b) => {
    const aFolder = a.dataset.kind === 'folder';
    const bFolder = b.dataset.kind === 'folder';
    if (aFolder !== bFolder) return aFolder ? -1 : 1;
    const nameOrder = collator.compare(a.dataset.name, b.dataset.name);
    if (selectedSort === 'name') return nameOrder;
    if (selectedSort === 'oldest') return Number(a.dataset.time) - Number(b.dataset.time) || nameOrder;
    if (selectedSort === 'largest') return Number(b.dataset.size) - Number(a.dataset.size) || nameOrder;
    if (selectedSort === 'smallest') return Number(a.dataset.size) - Number(b.dataset.size) || nameOrder;
    return Number(b.dataset.time) - Number(a.dataset.time) || nameOrder;
  });

  filteredRows = rows.filter(row => {
    const isFolder = row.dataset.kind === 'folder';
    const nameMatches = row.dataset.name.normalize('NFKC').toLocaleLowerCase('zh-CN').includes(query);
    const kindMatches = selectedKind === 'all' || row.dataset.kind === selectedKind;
    const modified = Number(row.dataset.time);
    const timeMatches = isFolder || (modified >= start && modified < end);
    return nameMatches && kindMatches && timeMatches;
  });
  for (const row of rows) entries.append(row);
  page = 1;
  renderPage();
}

for (const control of [search, kind, timeFilter, sort, dateFrom, dateTo]) {
  control.addEventListener(control === search ? 'input' : 'change', () => {
    applyFilters();
    if (control !== search) savePreferences();
  });
}
document.getElementById('reset').addEventListener('click', () => {
  search.value = '';
  kind.value = 'all';
  timeFilter.value = 'all';
  sort.value = 'recent';
  dateFrom.value = '';
  dateTo.value = '';
  applyFilters();
  savePreferences();
});
pageSize.addEventListener('change', () => { page = 1; renderPage(); savePreferences(); });
prevPage.addEventListener('click', () => { if (page > 1) { page--; renderPage(); entries.scrollIntoView({ block: 'start' }); } });
nextPage.addEventListener('click', () => {
  if (page * Number(pageSize.value) < filteredRows.length) {
    page++;
    renderPage();
    entries.scrollIntoView({ block: 'start' });
  }
});
applyFilters();

const previewDialog = document.getElementById('previewDialog');
const previewImage = document.getElementById('previewImage');
const previewTitle = document.getElementById('previewTitle');
const downloadPreview = document.getElementById('downloadPreview');
for (const button of document.querySelectorAll('.thumbButton, .previewAction')) {
  button.addEventListener('click', () => {
    const row = button.closest('.item');
    previewTitle.textContent = row.dataset.name;
    previewImage.src = row.querySelector('.thumbButton').dataset.preview;
    downloadPreview.href = row.querySelector('.itemText').href;
    previewDialog.showModal();
  });
}
document.getElementById('closePreview').addEventListener('click', () => previewDialog.close());
previewDialog.addEventListener('close', () => previewImage.removeAttribute('src'));

const pick = document.getElementById('pick');
const drop = document.getElementById('drop');
const status = document.getElementById('uploadStatus');
document.getElementById('choose').addEventListener('click', () => pick.click());
pick.addEventListener('change', () => uploadFiles(pick.files));
drop.addEventListener('dragover', event => {
  event.preventDefault();
  drop.classList.add('over');
});
drop.addEventListener('dragleave', () => drop.classList.remove('over'));
drop.addEventListener('drop', event => {
  event.preventDefault();
  drop.classList.remove('over');
  uploadFiles(event.dataTransfer.files);
});

async function uploadFiles(files) {
  if (!files || !files.length) return;
  let completed = 0;
  for (const file of files) {
    const row = document.createElement('li');
    row.textContent = file.name + '：等待上传';
    status.append(row);
    if (file.size > 10 * 1024 * 1024 * 1024) {
      row.textContent = file.name + '：超过 10 GB 上限';
      continue;
    }
    const ok = await new Promise(resolve => {
      const xhr = new XMLHttpRequest();
      const query = new URLSearchParams({ path: uploadPath, name: file.name });
      xhr.open('POST', '/_kutu/upload?' + query);
      xhr.setRequestHeader('X-Kutu-Upload-Token', uploadToken);
      xhr.upload.onprogress = event => {
        if (event.lengthComputable && event.total > 0) {
          row.textContent = file.name + '：' + Math.round(event.loaded / event.total * 100) + '%';
        }
      };
      xhr.onload = () => {
        row.textContent = file.name + '：' + (xhr.status === 201 ? '上传完成' : xhr.responseText || '上传失败');
        resolve(xhr.status === 201);
      };
      xhr.onerror = () => { row.textContent = file.name + '：网络中断'; resolve(false); };
      xhr.send(file);
    });
    if (ok) completed++;
  }
  if (completed === files.length) location.reload();
  else if (completed > 0) {
    const note = document.createElement('li');
    note.textContent = '部分文件已上传。刷新页面可以看到新文件。';
    status.append(note);
  }
}
