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
  if (baseUrl !== CONFIG.defaultBaseUrl) {
    throw new Error('公開URLはFieldOpsの公開URLから変更できません。');
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

function runHealthCheck() {
  runWithLock_(function () {
    const settings = readSettings_();
    return runMonitorCycle_(settings.baseUrl, settings.recipient);
  });
}

function runHealthCheckNow() {
  const result = runWithLock_(function () {
    const settings = readSettings_();
    return runMonitorCycle_(settings.baseUrl, settings.recipient);
  });
  if (result) {
    SpreadsheetApp.getActiveSpreadsheet().toast(
      result.status === 'UP' ? '公開環境は正常です。' : '公開環境の異常を検知しました。',
      'FieldOps監視',
      8);
  }
}

function sendFailureTest() {
  const result = runWithLock_(function () {
    const settings = readSettings_();
    const properties = PropertiesService.getScriptProperties();
    properties.setProperty(CONFIG.lastStatusProperty, 'UP');
    properties.deleteProperty(CONFIG.lastDownAtProperty);
    return runMonitorCycle_(CONFIG.failureTestBaseUrl, settings.recipient);
  });
  if (!result || result.status !== 'DOWN') {
    throw new Error('テスト用の異常状態を確認できませんでした。');
  }
  SpreadsheetApp.getActiveSpreadsheet().toast(
    '異常通知テストを実行しました。メールと監視履歴を確認してください。',
    'FieldOps監視',
    8);
}

function sendRecoveryTest() {
  const result = runWithLock_(function () {
    const properties = PropertiesService.getScriptProperties();
    if (properties.getProperty(CONFIG.lastStatusProperty) !== 'DOWN') {
      throw new Error('先に「テスト用の異常通知」を実行してください。');
    }
    const settings = readSettings_();
    return runMonitorCycle_(settings.baseUrl, settings.recipient);
  });
  if (!result || result.status !== 'UP') {
    throw new Error('公開環境が正常ではないため、復旧通知テストは完了していません。');
  }
  SpreadsheetApp.getActiveSpreadsheet().toast(
    '復旧通知テストを実行しました。メールと監視履歴を確認してください。',
    'FieldOps監視',
    8);
}

function runWithLock_(action) {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(CONFIG.lockWaitMs)) {
    console.warn('先行する監視が実行中のため、今回の処理を中止しました。');
    return null;
  }
  try {
    return action();
  } finally {
    lock.releaseLock();
  }
}

function runMonitorCycle_(baseUrl, recipient) {
  const result = probeUntilHealthy_(baseUrl);
  let notificationError = '';
  let historyError = '';

  try {
    notifyStateChange_(result, recipient);
  } catch (error) {
    notificationError = '通知失敗: ' + safeError_(error);
  }

  if (notificationError) {
    result.errorSummary = [result.errorSummary, notificationError].filter(String).join(' / ');
  }

  try {
    appendHistory_(result);
  } catch (error) {
    historyError = '履歴記録失敗: ' + safeError_(error);
  }

  if (notificationError || historyError) {
    throw new Error([notificationError, historyError].filter(String).join(' / '));
  }
  return result;
}

function probeUntilHealthy_(baseUrl) {
  const startedAt = Date.now();
  let live = null;
  let ready = null;
  let attempts = 0;

  for (let attempt = 1; attempt <= CONFIG.retryAttempts; attempt += 1) {
    attempts = attempt;
    live = probeEndpoint_(baseUrl + CONFIG.livePath);
    ready = probeEndpoint_(baseUrl + CONFIG.readyPath);
    if (live.ok && ready.ok) {
      break;
    }
    if (attempt < CONFIG.retryAttempts) {
      Utilities.sleep(CONFIG.retryDelayMs);
    }
  }

  const status = live.ok && ready.ok ? 'UP' : 'DOWN';
  return {
    checkedAtJst: Utilities.formatDate(new Date(), CONFIG.timeZone, 'yyyy/MM/dd HH:mm:ss'),
    baseUrl: baseUrl,
    status: status,
    live: live,
    ready: ready,
    elapsedSeconds: Math.round((Date.now() - startedAt) / 100) / 10,
    attempts: attempts,
    errorSummary: [
      live.ok ? '' : 'live: ' + live.error,
      ready.ok ? '' : 'ready: ' + ready.error
    ].filter(String).join(' / ')
  };
}

function probeEndpoint_(url) {
  try {
    const response = UrlFetchApp.fetch(url, {
      method: 'get',
      followRedirects: true,
      muteHttpExceptions: true,
      validateHttpsCertificates: true
    });
    const httpStatus = response.getResponseCode();
    const body = response.getContentText().trim();
    const ok = httpStatus === 200 && body === 'Healthy';
    return {
      ok: ok,
      httpStatus: httpStatus,
      error: ok ? '' : (httpStatus === 200 ? '応答本文がHealthyではありません' : 'HTTP ' + httpStatus)
    };
  } catch (error) {
    return {ok: false, httpStatus: '取得失敗', error: safeError_(error)};
  }
}

function notifyStateChange_(result, recipient) {
  const properties = PropertiesService.getScriptProperties();
  const previousStatus = properties.getProperty(CONFIG.lastStatusProperty);

  if (previousStatus === result.status) {
    return;
  }
  if (previousStatus === null && result.status === 'UP') {
    properties.setProperty(CONFIG.lastStatusProperty, 'UP');
    return;
  }

  const spreadsheetUrl = SpreadsheetApp.getActiveSpreadsheet().getUrl();
  if (result.status === 'DOWN') {
    MailApp.sendEmail(
      recipient,
      '[FieldOps監視] 公開環境の異常を検知しました',
      buildDownMailBody_(result, spreadsheetUrl));
    const downState = {};
    downState[CONFIG.lastStatusProperty] = 'DOWN';
    downState[CONFIG.lastDownAtProperty] = result.checkedAtJst;
    properties.setProperties(downState);
    return;
  }

  const lastDownAt = properties.getProperty(CONFIG.lastDownAtProperty) || '記録なし';
  MailApp.sendEmail(
    recipient,
    '[FieldOps監視] 公開環境が復旧しました',
    buildRecoveryMailBody_(result, lastDownAt, spreadsheetUrl));
  properties.setProperty(CONFIG.lastStatusProperty, 'UP');
  properties.deleteProperty(CONFIG.lastDownAtProperty);
}

function appendHistory_(result) {
  const sheets = ensureSheets_();
  sheets.historySheet.appendRow([
    result.checkedAtJst,
    result.status === 'UP' ? '正常' : '異常',
    result.live.ok ? '正常' : '異常',
    result.live.httpStatus,
    result.ready.ok ? '正常' : '異常',
    result.ready.httpStatus,
    result.elapsedSeconds,
    result.attempts,
    result.errorSummary
  ]);
}

function buildDownMailBody_(result, spreadsheetUrl) {
  return [
    'FieldOps公開環境の異常を検知しました。',
    '',
    '検知日時: ' + result.checkedAtJst,
    '公開URL: ' + result.baseUrl,
    'live結果: ' + (result.live.ok ? '正常' : '異常'),
    'live HTTP状態: ' + result.live.httpStatus,
    'ready結果: ' + (result.ready.ok ? '正常' : '異常'),
    'ready HTTP状態: ' + result.ready.httpStatus,
    '試行回数: ' + result.attempts,
    '監視履歴: ' + spreadsheetUrl
  ].join('\n');
}

function buildRecoveryMailBody_(result, lastDownAt, spreadsheetUrl) {
  return [
    'FieldOps公開環境が復旧しました。',
    '',
    '復旧日時: ' + result.checkedAtJst,
    '公開URL: ' + result.baseUrl,
    '異常検知日時: ' + lastDownAt,
    '監視履歴: ' + spreadsheetUrl
  ].join('\n');
}

function safeError_(error) {
  const message = String(error && error.message ? error.message : error).replace(/[\r\n]+/g, ' ');
  return message.length > 200 ? message.slice(0, 200) : message;
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
