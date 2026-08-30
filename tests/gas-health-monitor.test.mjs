import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const codeUrl = new URL(
  '../ops/google-apps-script/fieldops-health-monitor/Code.gs',
  import.meta.url);

async function loadMonitor(overrides = {}) {
  const code = await readFile(codeUrl, 'utf8');
  const context = vm.createContext({
    console,
    Utilities: {
      formatDate(date, timeZone, pattern) {
        assert.equal(timeZone, 'Asia/Tokyo');
        assert.equal(pattern, 'HH:mm');
        return new Intl.DateTimeFormat('ja-JP', {
          timeZone,
          hour: '2-digit',
          minute: '2-digit',
          hour12: false
        }).format(date);
      }
    },
    ...overrides
  });

  vm.runInContext(code, context, {filename: codeUrl.pathname});
  return context;
}

test('日本時間10:00以上18:00未満だけを監視時間と判定する', async () => {
  const monitor = await loadMonitor();

  const cases = [
    ['2026-08-30T00:59:00Z', false], // 09:59 JST
    ['2026-08-30T01:00:00Z', true],  // 10:00 JST
    ['2026-08-30T08:50:00Z', true],  // 17:50 JST
    ['2026-08-30T09:00:00Z', false]  // 18:00 JST
  ];

  for (const [timestamp, expected] of cases) {
    assert.equal(monitor.isWithinMonitoringWindow_(new Date(timestamp)), expected);
  }
});

test('時間外の定期実行は外部処理を始めず終了する', async () => {
  const monitor = await loadMonitor({
    LockService: {
      getScriptLock() {
        throw new Error('時間外にロック処理を開始しました');
      }
    }
  });

  const result = monitor.runScheduledHealthCheckAt_(new Date('2026-08-30T09:00:00Z'));

  assert.equal(result, null);
});

test('時間主導トリガーのイベント値を日時として扱わない', async () => {
  class FixedDate extends Date {
    constructor(...args) {
      super(args.length > 0 ? args[0] : '2026-08-30T09:00:00Z');
    }
  }
  const monitor = await loadMonitor({
    Date: FixedDate,
    LockService: {
      getScriptLock() {
        throw new Error('18:00以降にロック処理を開始しました');
      }
    }
  });

  const result = monitor.runHealthCheck({triggerUid: 'scheduled-trigger'});

  assert.equal(result, null);
});

test('監視開始時は既存の対象トリガーを10分間隔の1件に置き換える', async () => {
  const existingTarget = {getHandlerFunction: () => 'runHealthCheck'};
  const unrelated = {getHandlerFunction: () => 'otherFunction'};
  const deleted = [];
  let createdHandler = '';
  let createdInterval = 0;
  let createdCount = 0;
  const builder = {
    timeBased() {
      return this;
    },
    everyMinutes(minutes) {
      createdInterval = minutes;
      return this;
    },
    create() {
      createdCount += 1;
    }
  };
  const monitor = await loadMonitor({
    ScriptApp: {
      getProjectTriggers: () => [existingTarget, unrelated],
      deleteTrigger: trigger => deleted.push(trigger),
      newTrigger(handler) {
        createdHandler = handler;
        return builder;
      }
    }
  });

  monitor.replaceHealthCheckTrigger_();

  assert.deepEqual(deleted, [existingTarget]);
  assert.equal(createdHandler, 'runHealthCheck');
  assert.equal(createdInterval, 10);
  assert.equal(createdCount, 1);
});

test('スプレッドシートのメニューから時間帯限定10分監視を開始できる', async () => {
  const menuItems = [];
  const menu = {
    addItem(label, handler) {
      menuItems.push([label, handler]);
      return this;
    },
    addSeparator() {
      return this;
    },
    addToUi() {
      return this;
    }
  };
  const monitor = await loadMonitor({
    SpreadsheetApp: {
      getUi() {
        return {
          createMenu() {
            return menu;
          }
        };
      }
    }
  });

  monitor.onOpen();

  assert.ok(menuItems.some(([label, handler]) =>
    label === '10:00〜18:00の10分監視を開始' && handler === 'startBusinessHoursMonitoring'));
  assert.ok(!menuItems.some(([label]) => label.includes('1時間ごと')));
});

test('時間帯限定監視の開始は設定表示を更新して10分トリガーを作成する', async () => {
  const values = new Map([
    ['B2', 'https://fieldops-portfolio.onrender.com'],
    ['B3', 'owner@example.com']
  ]);
  const settingsHeaders = ['項目', '設定値', '説明'];
  const historyHeaders = [
    '実行日時（JST）',
    '総合結果（正常／異常）',
    'live結果',
    'live HTTP状態',
    'ready結果',
    'ready HTTP状態',
    '所要時間（秒）',
    '試行回数',
    'エラー概要'
  ];
  const makeSheet = headers => ({
    getRange(...args) {
      if (typeof args[0] === 'string') {
        const address = args[0];
        return {
          getDisplayValue: () => values.get(address) ?? '',
          setValue(value) {
            values.set(address, value);
            return this;
          }
        };
      }
      return {getDisplayValues: () => [headers]};
    }
  });
  const settingsSheet = makeSheet(settingsHeaders);
  const historySheet = makeSheet(historyHeaders);
  const toasts = [];
  let createdInterval = 0;
  const spreadsheet = {
    getSheetByName(name) {
      return name === '監視設定' ? settingsSheet : historySheet;
    },
    toast(message) {
      toasts.push(message);
    }
  };
  const triggerBuilder = {
    timeBased() {
      return this;
    },
    everyMinutes(minutes) {
      createdInterval = minutes;
      return this;
    },
    create() {}
  };
  const monitor = await loadMonitor({
    SpreadsheetApp: {getActiveSpreadsheet: () => spreadsheet},
    ScriptApp: {
      getProjectTriggers: () => [],
      deleteTrigger() {},
      newTrigger: () => triggerBuilder
    }
  });

  monitor.startBusinessHoursMonitoring();

  assert.equal(values.get('B4'), '10分');
  assert.equal(values.get('C4'), '毎日10:00以上18:00未満に監視します');
  assert.equal(values.get('B7'), '監視中（10:00〜18:00／10分おき）');
  assert.equal(createdInterval, 10);
  assert.ok(toasts.some(message => message.includes('18:00以降はアクセスしません')));
});

test('新規作成する監視設定には10分間隔と対象時間帯を表示する', async () => {
  let settingsRows = [];
  const insertedSheets = new Map();
  const makeInsertedSheet = name => ({
    getRange() {
      return {
        setValues(rows) {
          if (name === '監視設定') {
            settingsRows = rows;
          }
          return this;
        }
      };
    },
    setFrozenRows() {}
  });
  const spreadsheet = {
    getSheetByName: name => insertedSheets.get(name) ?? null,
    insertSheet(name) {
      const sheet = makeInsertedSheet(name);
      insertedSheets.set(name, sheet);
      return sheet;
    }
  };
  const monitor = await loadMonitor({
    SpreadsheetApp: {getActiveSpreadsheet: () => spreadsheet}
  });

  monitor.ensureSheets_();

  assert.deepEqual(
    Array.from(settingsRows[3]),
    ['実行間隔', '10分', '毎日10:00以上18:00未満に監視します']);
});

test('初期設定を再実行しても監視中の時間帯表示を保つ', async () => {
  const values = new Map([['B3', 'owner@example.com']]);
  const settingsHeaders = ['項目', '設定値', '説明'];
  const historyHeaders = [
    '実行日時（JST）', '総合結果（正常／異常）', 'live結果', 'live HTTP状態',
    'ready結果', 'ready HTTP状態', '所要時間（秒）', '試行回数', 'エラー概要'
  ];
  const makeSheet = headers => ({
    getRange(...args) {
      if (typeof args[0] === 'string') {
        const address = args[0];
        return {
          getDisplayValue: () => values.get(address) ?? '',
          setValue(value) {
            values.set(address, value);
            return this;
          }
        };
      }
      return {getDisplayValues: () => [headers]};
    }
  });
  const settingsSheet = makeSheet(settingsHeaders);
  const historySheet = makeSheet(historyHeaders);
  const spreadsheet = {
    getSheetByName: name => name === '監視設定' ? settingsSheet : historySheet,
    toast() {}
  };
  const monitor = await loadMonitor({
    SpreadsheetApp: {getActiveSpreadsheet: () => spreadsheet},
    ScriptApp: {
      getProjectTriggers: () => [{getHandlerFunction: () => 'runHealthCheck'}]
    }
  });

  monitor.setupMonitoring();

  assert.equal(values.get('B7'), '監視中（10:00〜18:00／10分おき）');
});
