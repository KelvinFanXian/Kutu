const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function control(value = '') {
  return {
    value,
    hidden: false,
    disabled: false,
    textContent: '',
    events: {},
    addEventListener(name, callback) { this.events[name] = callback; }
  };
}

const today = Date.now();
const rows = [
  { dataset: { name: '示例子目录', kind: 'folder', time: String(today - 90 * 86400000), size: '0' } },
  ...Array.from({ length: 60 }, (_, index) => ({
    dataset: {
      name: `文件-${String(index + 1).padStart(2, '0')}.txt`,
      kind: index === 59 ? 'image' : 'document',
      time: String(today - (index + 1) * 86400000),
      size: String(index + 1)
    }
  }))
];
for (const row of rows) {
  row.hidden = false;
  row.time = { textContent: '' };
  row.querySelector = () => row.time;
}

const entries = {
  children: [...rows],
  querySelectorAll: () => rows,
  append(row) {
    this.children = this.children.filter(item => item !== row);
    this.children.push(row);
  },
  scrollIntoView() {}
};
const previewRow = rows[60];
const thumbnail = { ...control(), dataset: { preview: '/_kutu/preview?path=picture.png' }, closest: () => previewRow };
const previewAction = { ...control(), closest: () => previewRow };
previewRow.querySelector = selector => selector === 'time' ? previewRow.time
  : selector === '.thumbButton' ? thumbnail : { href: '/picture.png' };
const elements = {
  entries,
  search: control(),
  kind: control('all'),
  timeFilter: control('all'),
  sort: control('recent'),
  dateRange: control(),
  dateFrom: control(),
  dateTo: control(),
  dateError: control(),
  resultCount: control(),
  empty: control(),
  pageSize: control('50'),
  prevPage: control(),
  nextPage: control(),
  pageInfo: control(),
  previewDialog: { ...control(), showModal() { this.open = true; }, close() { this.open = false; } },
  previewImage: { ...control(), removeAttribute() {} },
  previewTitle: control(),
  downloadPreview: control(),
  closePreview: control(),
  reset: control(),
  pick: control(),
  drop: { ...control(), classList: { add() {}, remove() {} } },
  uploadStatus: { append() {} },
  choose: control()
};
const document = {
  querySelectorAll: () => [thumbnail, previewAction],
  getElementById: id => {
    assert.ok(elements[id], `missing element ${id}`);
    return elements[id];
  },
  createElement: () => control()
};
const source = fs.readFileSync(path.join(__dirname, '..', 'Features', 'FileShare', 'Web', 'directory.js'), 'utf8');
vm.runInNewContext(source, { document, Intl, Date, Number, Set, URLSearchParams });

assert.equal(rows.filter(row => !row.hidden).length, 50);
assert.equal(elements.pageInfo.textContent, '第 1 / 2 页');
assert.equal(elements.prevPage.disabled, true);
assert.equal(elements.nextPage.disabled, false);
assert.equal(entries.children[0].dataset.kind, 'folder');
assert.equal(entries.children[1].dataset.name, '文件-01.txt');

elements.nextPage.events.click();
assert.equal(elements.pageInfo.textContent, '第 2 / 2 页');
assert.equal(rows.filter(row => !row.hidden).length, 11);
assert.equal(rows[1].hidden, true);
assert.equal(rows[60].hidden, false);

elements.search.value = '文件-60';
elements.search.events.input();
assert.equal(elements.pageInfo.textContent, '第 1 / 1 页');
assert.equal(rows.filter(row => !row.hidden).length, 1);
assert.equal(rows[60].hidden, false);

elements.search.value = '';
elements.kind.value = 'image';
elements.kind.events.change();
assert.equal(rows.filter(row => !row.hidden).length, 1);
assert.equal(rows[60].hidden, false);

elements.kind.value = 'all';
elements.timeFilter.value = 'today';
elements.timeFilter.events.change();
assert.equal(rows.filter(row => !row.hidden).length, 1);
assert.equal(rows[0].hidden, false);

elements.timeFilter.value = 'custom';
elements.dateFrom.value = '2026-11-20';
elements.dateTo.value = '2026-11-10';
elements.dateTo.events.change();
assert.equal(elements.dateError.hidden, false);

elements.reset.events.click();
assert.equal(elements.pageInfo.textContent, '第 1 / 2 页');
assert.equal(rows.filter(row => !row.hidden).length, 50);

elements.pageSize.value = '25';
elements.pageSize.events.change();
assert.equal(elements.pageInfo.textContent, '第 1 / 3 页');
assert.equal(rows.filter(row => !row.hidden).length, 25);

previewAction.events.click();
assert.equal(elements.previewDialog.open, true);
assert.equal(elements.previewTitle.textContent, previewRow.dataset.name);
assert.equal(elements.previewImage.src, '/_kutu/preview?path=picture.png');
assert.equal(elements.downloadPreview.href, '/picture.png');
elements.closePreview.events.click();
assert.equal(elements.previewDialog.open, false);

console.log('PASS: sort, search, type/date filters, pagination and preview actions');
