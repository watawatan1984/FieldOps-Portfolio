/**
 * @OnlyCurrentDoc
 */
const CONFIG = Object.freeze({
  defaultBaseUrl: 'https://fieldops-portfolio.onrender.com',
  failureTestBaseUrl: 'https://fieldops-monitor-test.invalid',
  livePath: '/health/live',
  readyPath: '/health/ready',
  settingsSheetName: '監視設定',
  historySheetName: '監視履歴',
  triggerHandler: 'runHealthCheck',
  retryAttempts: 5,
  retryDelayMs: 15000,
  lockWaitMs: 1000,
  timeZone: 'Asia/Tokyo',
  lastStatusProperty: 'LAST_STATUS',
  lastDownAtProperty: 'LAST_DOWN_AT'
});

const SETTINGS_ROWS = Object.freeze([
  ['項目', '設定値', '説明'],
  ['公開URL', CONFIG.defaultBaseUrl, '末尾の / は自動で除去します'],
  ['通知先メール', '', '異常と復旧の通知先です'],
  ['実行間隔', '1時間', '固定です'],
  ['再試行回数', '5回', '初回を含みます'],
  ['再試行間隔', '15秒', 'コールドスタート待機用です'],
  ['監視状態', '停止中', '開始または停止メニューで更新します']
]);

const HISTORY_HEADERS = Object.freeze([
  '実行日時（JST）',
  '総合結果（正常／異常）',
  'live結果',
  'live HTTP状態',
  'ready結果',
  'ready HTTP状態',
  '所要時間（秒）',
  '試行回数',
  'エラー概要'
]);

function onOpen() {
  SpreadsheetApp.getUi()
    .createMenu('FieldOps監視')
    .addItem('初期設定を作成', 'setupMonitoring')
    .addItem('今すぐ確認', 'runHealthCheckNow')
    .addSeparator()
    .addItem('1時間ごとの監視を開始', 'startHourlyMonitoring')
    .addItem('監視を停止', 'stopMonitoring')
    .addSeparator()
    .addItem('テスト用の異常通知', 'sendFailureTest')
    .addItem('テスト状態を戻す', 'sendRecoveryTest')
    .addToUi();
}

function getBoundSpreadsheet_() {
  const spreadsheet = SpreadsheetApp.getActiveSpreadsheet();
  if (!spreadsheet) {
    throw new Error('このスクリプトはGoogleスプレッドシートに紐づけて使用してください。');
  }
  return spreadsheet;
}

function ensureSheets_() {
  const spreadsheet = getBoundSpreadsheet_();
  let settingsSheet = spreadsheet.getSheetByName(CONFIG.settingsSheetName);
  if (!settingsSheet) {
    settingsSheet = spreadsheet.insertSheet(CONFIG.settingsSheetName);
    settingsSheet.getRange(1, 1, SETTINGS_ROWS.length, SETTINGS_ROWS[0].length)
      .setValues(SETTINGS_ROWS);
    settingsSheet.setFrozenRows(1);
  } else {
    assertHeader_(settingsSheet, SETTINGS_ROWS[0], '監視設定シートの見出しが不正です。既存データは上書きしません。');
  }

  let historySheet = spreadsheet.getSheetByName(CONFIG.historySheetName);
  if (!historySheet) {
    historySheet = spreadsheet.insertSheet(CONFIG.historySheetName);
    historySheet.getRange(1, 1, 1, HISTORY_HEADERS.length).setValues([HISTORY_HEADERS]);
    historySheet.setFrozenRows(1);
  } else {
    assertHeader_(historySheet, HISTORY_HEADERS, '監視履歴シートの見出しが不正です。既存データは上書きしません。');
  }

  return {settingsSheet: settingsSheet, historySheet: historySheet};
}

function setupMonitoring() {
  const sheets = ensureSheets_();
  const recipientCell = sheets.settingsSheet.getRange('B3');
  if (!recipientCell.getDisplayValue().trim()) {
    recipientCell.setValue(Session.getEffectiveUser().getEmail());
  }
  updateMonitoringState_(countHealthCheckTriggers_() === 1 ? '監視中' : '停止中');
  getBoundSpreadsheet_().toast(
    recipientCell.getDisplayValue().trim()
      ? '初期設定を作成しました。公開URLと通知先メールを確認してください。'
      : '初期設定を作成しました。通知先メールを入力してください。',
    'FieldOps監視',
    8);
}

function readSettings_() {
  const settingsSheet = ensureSheets_().settingsSheet;
  const baseUrl = settingsSheet.getRange('B2').getDisplayValue().trim().replace(/\/+$/, '');
  const recipient = settingsSheet.getRange('B3').getDisplayValue().trim();
  if (!/^https:\/\/[^/?#]+$/.test(baseUrl)) {
    throw new Error('公開URLはパスを含まないHTTPS URLで入力してください。');
  }
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(recipient)) {
    throw new Error('通知先メールを1件入力してください。');
  }
  return {baseUrl: baseUrl, recipient: recipient};
}

function updateMonitoringState_(state) {
  ensureSheets_().settingsSheet.getRange('B7').setValue(state);
}

function countHealthCheckTriggers_() {
  return ScriptApp.getProjectTriggers().filter(function (trigger) {
    return trigger.getHandlerFunction() === CONFIG.triggerHandler;
  }).length;
}

function deleteHealthCheckTriggers_() {
  let deletedCount = 0;
  ScriptApp.getProjectTriggers().forEach(function (trigger) {
    if (trigger.getHandlerFunction() === CONFIG.triggerHandler) {
      ScriptApp.deleteTrigger(trigger);
      deletedCount += 1;
    }
  });
  return deletedCount;
}

function startHourlyMonitoring() {
  ensureSheets_();
  readSettings_();
  deleteHealthCheckTriggers_();
  ScriptApp.newTrigger(CONFIG.triggerHandler)
    .timeBased()
    .everyHours(1)
    .create();
  updateMonitoringState_('監視中');
  SpreadsheetApp.getActiveSpreadsheet().toast(
    '1時間ごとの監視を開始しました。',
    'FieldOps監視',
    5);
}

function stopMonitoring() {
  const deletedCount = deleteHealthCheckTriggers_();
  ensureSheets_();
  updateMonitoringState_('停止中');
  SpreadsheetApp.getActiveSpreadsheet().toast(
    '監視を停止しました。削除した対象トリガー: ' + deletedCount + '件',
    'FieldOps監視',
    5);
}

function assertHeader_(sheet, expectedHeaders, message) {
  const values = sheet.getRange(1, 1, 1, expectedHeaders.length).getDisplayValues()[0];
  const matches = expectedHeaders.every(function (expectedHeader, index) {
    return values[index] === expectedHeader;
  });
  if (!matches) {
    throw new Error(message);
  }
}
