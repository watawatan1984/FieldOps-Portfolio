# FieldOps GAS死活監視 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Googleスプレッドシートに紐づくGoogle Apps ScriptからFieldOps公開環境を1時間ごとに外形監視し、履歴を日本語で残し、異常と復旧の状態変化だけをメール通知できるようにする。

**Architecture:** FieldOps本体、Render設定、PostgreSQLには変更を加えず、既存の`/health/live`と`/health/ready`をGASから確認する。GASはスプレッドシートを設定・操作・履歴の画面として使い、Script Propertiesで前回状態を保持する。リポジトリではC#の構造契約テストとPowerShellの静的検査でGASソース、最小権限、毎時トリガー、安全なテスト経路を固定し、最後にGoogle実環境で正常・異常・復旧・トリガーを実測する。

**Tech Stack:** Google Apps Script V8、Google Sheets、UrlFetchApp、MailApp、ScriptApp、PropertiesService、LockService、PowerShell 7、.NET 10、xUnit、GitHub Actions

**Spec:** `docs/superpowers/specs/2026-08-29-fieldops-gas-health-monitor-design.md`

## Global Constraints

- 監視対象は`https://fieldops-portfolio.onrender.com/health/live`と`https://fieldops-portfolio.onrender.com/health/ready`だけとする。
- 監視間隔は`everyHours(1)`に固定し、`everyMinutes(...)`や15分未満のスリープ回避を追加しない。
- 各エンドポイントはHTTP 200かつ本文を`trim()`した値が完全一致で`Healthy`の場合だけ正常とする。
- 初回を含め最大5回、失敗時は15秒間隔で再試行し、最大待機は約60秒とする。
- 総合状態は両方正常なら`UP`、どちらか一方でも失敗なら`DOWN`とする。
- メールは初回異常、`UP`から`DOWN`、`DOWN`から`UP`の状態変化時だけ送る。初回正常と同一状態の継続時には送らない。
- 前回状態はScript Propertiesの`LAST_STATUS`、直近異常日時は`LAST_DOWN_AT`へ保存する。
- 履歴は`監視履歴`シートへ追記だけを行い、既存行を更新・削除しない。
- テスト異常は`https://fieldops-monitor-test.invalid`だけを使い、本番URLの設定値を変更しない。
- 削除するトリガーは現在のGASプロジェクト内でハンドラー名が`runHealthCheck`のものだけとする。
- 同時実行は`LockService.getScriptLock()`で排他し、ロックを取れない後発処理は外部アクセスせず終了する。
- レスポンス本文、Cookie、ヘッダー、Google認証情報、Renderトークン、APIキーを履歴、メール、ソース、証跡へ保存しない。
- OAuth scopeは現在のスプレッドシート、外部HTTP、メール送信、トリガー管理、初期通知先取得用の実行ユーザーメールだけに限定する。
- FieldOpsのアプリケーションコード、DBスキーマ、Render設定、新しい依存パッケージは変更しない。
- Googleスプレッドシートは非公開のまま維持する。
- 各タスクは失敗する検査を先に追加し、失敗確認、最小実装、合格確認、コミットの順で完了する。

---

### Task 1: GASファイル、毎時トリガー、最小権限の構造契約

**Files:**
- Create: `ops/google-apps-script/fieldops-health-monitor/Code.gs`
- Create: `ops/google-apps-script/fieldops-health-monitor/appsscript.json`
- Modify: `tests/FieldOps.Domain.Tests/Architecture/ProjectDependencyTests.cs`

**Interfaces:**
- Produces: `onOpen(): void`
- Produces: `setupMonitoring(): void`
- Produces: `startHourlyMonitoring(): void`
- Produces: `stopMonitoring(): void`
- Produces: `ensureSheets_(): {settingsSheet: GoogleAppsScript.Spreadsheet.Sheet, historySheet: GoogleAppsScript.Spreadsheet.Sheet}`
- Produces: `deleteHealthCheckTriggers_(): number`
- Preserves: FieldOps本体、Render設定、他ハンドラーのGASトリガー

- [ ] **Step 1: GASの構造と権限を固定する失敗テストを書く**

`ProjectDependencyTests.cs`へ`using System.Text.Json;`を追加し、次のテストを追加する。

```csharp
[Fact]
public void Gas_health_monitor_uses_hourly_checks_and_explicit_minimum_scopes()
{
    string repositoryRoot = FindRepositoryRoot();
    string monitorRoot = Path.Combine(
        repositoryRoot,
        "ops",
        "google-apps-script",
        "fieldops-health-monitor");
    string codePath = Path.Combine(monitorRoot, "Code.gs");
    string manifestPath = Path.Combine(monitorRoot, "appsscript.json");

    Assert.True(File.Exists(codePath), $"Missing GAS source: {codePath}");
    Assert.True(File.Exists(manifestPath), $"Missing GAS manifest: {manifestPath}");

    string code = File.ReadAllText(codePath);
    Assert.Contains("https://fieldops-portfolio.onrender.com", code, StringComparison.Ordinal);
    Assert.Contains("/health/live", code, StringComparison.Ordinal);
    Assert.Contains("/health/ready", code, StringComparison.Ordinal);
    Assert.Contains("everyHours(1)", code, StringComparison.Ordinal);
    Assert.DoesNotContain("everyMinutes(", code, StringComparison.Ordinal);
    Assert.Contains("getHandlerFunction() === CONFIG.triggerHandler", code, StringComparison.Ordinal);

    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    JsonElement root = manifest.RootElement;
    Assert.Equal("Asia/Tokyo", root.GetProperty("timeZone").GetString());
    Assert.Equal("V8", root.GetProperty("runtimeVersion").GetString());

    string[] scopes = root.GetProperty("oauthScopes")
        .EnumerateArray()
        .Select(scope => scope.GetString() ?? string.Empty)
        .OrderBy(scope => scope, StringComparer.Ordinal)
        .ToArray();
    string[] expectedScopes =
    [
        "https://www.googleapis.com/auth/script.external_request",
        "https://www.googleapis.com/auth/script.scriptapp",
        "https://www.googleapis.com/auth/script.send_mail",
        "https://www.googleapis.com/auth/spreadsheets.currentonly",
        "https://www.googleapis.com/auth/userinfo.email"
    ];

    Assert.Equal(expectedScopes.OrderBy(scope => scope, StringComparer.Ordinal), scopes);
}
```

- [ ] **Step 2: 必須ファイルがないため失敗することを確認する**

Run:

```powershell
dotnet test tests/FieldOps.Domain.Tests/FieldOps.Domain.Tests.csproj --configuration Release --filter "FullyQualifiedName~Gas_health_monitor_uses_hourly_checks_and_explicit_minimum_scopes"
```

Expected: `Missing GAS source`でFAIL。

- [ ] **Step 3: 明示的な最小権限マニフェストを作成する**

`appsscript.json`を次の内容で作成する。

```json
{
  "timeZone": "Asia/Tokyo",
  "dependencies": {},
  "exceptionLogging": "STACKDRIVER",
  "runtimeVersion": "V8",
  "oauthScopes": [
    "https://www.googleapis.com/auth/spreadsheets.currentonly",
    "https://www.googleapis.com/auth/script.external_request",
    "https://www.googleapis.com/auth/script.send_mail",
    "https://www.googleapis.com/auth/script.scriptapp",
    "https://www.googleapis.com/auth/userinfo.email"
  ]
}
```

`userinfo.email`は`setupMonitoring()`が通知先の初期値として実行ユーザーのメールアドレスを取得するためだけに使う。メール取得結果が空の場合は空欄のままにし、利用者が入力するまで監視開始を拒否する。

- [ ] **Step 4: 定数、メニュー、設定・履歴シート作成を実装する**

`Code.gs`の先頭に`@OnlyCurrentDoc`を置き、定数を1か所へ集約する。

```javascript
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
```

`onOpen()`は次の順で日本語メニューを作成する。

```javascript
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
```

`ensureSheets_()`は現在のスプレッドシートだけを使う。各シートが存在しない場合だけ作成し、既存シートの見出しが契約と異なる場合は上書きせず例外にする。コードが設定値として読むセルはB2（公開URL）とB3（通知先メール）だけとし、B4:B7は固定値または状態の表示専用にする。`監視履歴`は見出しを固定し、日時列は文字列でJSTを保存する。

`setupMonitoring()`は`ensureSheets_()`を呼び、B3が空のときだけ`Session.getEffectiveUser().getEmail()`を設定する。空のままの場合は日本語で「通知先メールを入力してください」と表示し、秘密情報や権限を自動追加しない。

```javascript
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
  } else if (settingsSheet.getRange('A1').getDisplayValue() !== '項目') {
    throw new Error('監視設定シートの見出しが不正です。既存データは上書きしません。');
  }

  let historySheet = spreadsheet.getSheetByName(CONFIG.historySheetName);
  if (!historySheet) {
    historySheet = spreadsheet.insertSheet(CONFIG.historySheetName);
    historySheet.getRange(1, 1, 1, HISTORY_HEADERS.length).setValues([HISTORY_HEADERS]);
    historySheet.setFrozenRows(1);
  } else if (historySheet.getRange('A1').getDisplayValue() !== HISTORY_HEADERS[0]) {
    throw new Error('監視履歴シートの見出しが不正です。既存データは上書きしません。');
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
```

- [ ] **Step 5: 同一ハンドラーだけを整理する毎時トリガー操作を実装する**

```javascript
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
```

`readSettings_()`はB2とB3を読み、URLが`https://`で始まり、パス、クエリ、フラグメントを持たないことを検査する。通知先は前後空白を除去し、単一メールアドレスとして妥当でなければ例外にする。B2の末尾`/`は除去して返すが、シート値を勝手に上書きしない。

- [ ] **Step 6: 構造契約テストを合格させる**

Run:

```powershell
dotnet test tests/FieldOps.Domain.Tests/FieldOps.Domain.Tests.csproj --configuration Release --filter "FullyQualifiedName~Gas_health_monitor_uses_hourly_checks_and_explicit_minimum_scopes"
```

Expected: PASS。

- [ ] **Step 7: Task 1をコミットする**

```powershell
git add ops/google-apps-script/fieldops-health-monitor/Code.gs ops/google-apps-script/fieldops-health-monitor/appsscript.json tests/FieldOps.Domain.Tests/Architecture/ProjectDependencyTests.cs
git commit -m "GAS監視の構造契約と権限を追加"
```

---

### Task 2: ヘルス判定、再試行、履歴、状態変化通知

**Files:**
- Modify: `ops/google-apps-script/fieldops-health-monitor/Code.gs`
- Create: `scripts/check-gas-monitor.ps1`

**Interfaces:**
- Produces: `runHealthCheck(): void`
- Produces: `runHealthCheckNow(): void`
- Produces: `sendFailureTest(): void`
- Produces: `sendRecoveryTest(): void`
- Produces: `runMonitorCycle_(baseUrl: string, recipient: string): MonitorResult`
- Produces: `probeUntilHealthy_(baseUrl: string): MonitorResult`
- Produces: `probeEndpoint_(url: string): EndpointResult`
- Produces: `notifyStateChange_(result: MonitorResult, recipient: string): void`
- Produces: `appendHistory_(result: MonitorResult): void`
- Produces: `safeError_(error: unknown): string`
- Consumes: `readSettings_(): {baseUrl: string, recipient: string}`
- Persists: `LAST_STATUS` as `UP|DOWN` and `LAST_DOWN_AT` as a JST timestamp string

`EndpointResult`の実行時形状は`{ok: boolean, httpStatus: number|string, error: string}`、`MonitorResult`は`{checkedAtJst: string, baseUrl: string, status: 'UP'|'DOWN', live: EndpointResult, ready: EndpointResult, elapsedSeconds: number, attempts: number, errorSummary: string}`へ固定する。

- [ ] **Step 1: GAS監視の安全契約を検査するPowerShellスクリプトを書く**

`scripts/check-gas-monitor.ps1`を作成し、実装前の`Code.gs`に対して失敗する必須パターンを定義する。

```powershell
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$monitorRoot = Join-Path $repositoryRoot 'ops/google-apps-script/fieldops-health-monitor'
$codePath = Join-Path $monitorRoot 'Code.gs'
$manifestPath = Join-Path $monitorRoot 'appsscript.json'

foreach ($path in @($codePath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required GAS monitor file was not found: $path"
    }
}

$code = Get-Content -LiteralPath $codePath -Raw
$requiredPatterns = [ordered]@{
    'production base URL' = [regex]::Escape('https://fieldops-portfolio.onrender.com')
    'safe failure-test URL' = [regex]::Escape('https://fieldops-monitor-test.invalid')
    'live health path' = [regex]::Escape('/health/live')
    'ready health path' = [regex]::Escape('/health/ready')
    'five attempts' = 'retryAttempts:\s*5'
    'fifteen-second delay' = 'retryDelayMs:\s*15000'
    'exact healthy body' = "body\s*===\s*'Healthy'"
    'muted HTTP exceptions' = 'muteHttpExceptions:\s*true'
    'script lock' = 'LockService\.getScriptLock\(\)'
    'one-hour trigger' = 'everyHours\(1\)'
    'state property' = [regex]::Escape("lastStatusProperty: 'LAST_STATUS'")
    'down timestamp property' = [regex]::Escape("lastDownAtProperty: 'LAST_DOWN_AT'")
    'mail sender' = 'MailApp\.sendEmail\('
    'append-only history' = 'appendRow\('
}

$missing = @()
foreach ($item in $requiredPatterns.GetEnumerator()) {
    if ($code -notmatch $item.Value) {
        $missing += $item.Key
    }
}

if ($missing.Count -gt 0) {
    throw "GAS monitor checks failed: $($missing -join ', ')"
}

$prohibitedPatterns = [ordered]@{
    'minute trigger' = 'everyMinutes\('
    'demo login ping' = '/demo-login'
    'cookie handling' = '(?i)cookie'
    'authorization header' = '(?i)authorization'
}

foreach ($item in $prohibitedPatterns.GetEnumerator()) {
    if ($code -match $item.Value) {
        throw "GAS monitor contains prohibited behavior: $($item.Key)"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedScopes = @(
    'https://www.googleapis.com/auth/script.external_request'
    'https://www.googleapis.com/auth/script.scriptapp'
    'https://www.googleapis.com/auth/script.send_mail'
    'https://www.googleapis.com/auth/spreadsheets.currentonly'
    'https://www.googleapis.com/auth/userinfo.email'
) | Sort-Object
$actualScopes = @($manifest.oauthScopes) | Sort-Object
if (($actualScopes -join "`n") -ne ($expectedScopes -join "`n")) {
    throw 'GAS manifest OAuth scopes do not match the approved minimum set.'
}

Write-Output "GAS monitor checks passed ($($requiredPatterns.Count) requirements)."
```

- [ ] **Step 2: 監視本体が未実装のため静的検査が失敗することを確認する**

Run:

```powershell
pwsh -NoProfile -File ./scripts/check-gas-monitor.ps1
```

Expected: `exact healthy body`、`muted HTTP exceptions`、`mail sender`、`append-only history`などが不足してFAIL。

- [ ] **Step 3: 排他付きの通常実行と手動実行を実装する**

```javascript
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
```

トリガー実行の`runHealthCheck()`ではUI APIを呼ばない。手動実行の`runHealthCheckNow()`だけがトーストを出す。

- [ ] **Step 4: HTTP 200と本文完全一致による再試行判定を実装する**

```javascript
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
```

`safeError_()`は`String(error && error.message ? error.message : error)`を改行なしの200文字以内へ切り詰める。レスポンス本文、ヘッダー、Cookieは渡さない。

- [ ] **Step 5: 状態変化メールと異常日時の保存を実装する**

```javascript
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
    properties.setProperties({
      LAST_STATUS: 'DOWN',
      LAST_DOWN_AT: result.checkedAtJst
    });
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
```

`buildDownMailBody_()`には検知日時、公開URL、live/readyの結果とHTTP状態、試行回数、スプレッドシートURLだけを含める。`buildRecoveryMailBody_()`には復旧日時、公開URL、`LAST_DOWN_AT`、スプレッドシートURLだけを含める。レスポンス本文は含めない。

メール送信が失敗した場合は`LAST_STATUS`を更新しない。次回も状態変化として再送を試せるようにする。

- [ ] **Step 6: 1実行1行の追記履歴と失敗分離を実装する**

```javascript
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
```

この順番により、履歴記録が失敗しても状態変化メールを先に試せる。通知失敗は可能なら同じ履歴行のエラー概要に残し、その後に例外を再送出してApps Script実行履歴にも残す。

- [ ] **Step 7: 本番URLを書き換えない異常・復旧テスト操作を実装する**

```javascript
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
```

異常テストは意図的に前回状態を`UP`へ揃えて1通の異常メールを発生させる。復旧テストは実際の本番ヘルスチェックが`UP`になった場合だけ復旧メールを送る。

- [ ] **Step 8: 静的検査とDomainテストを合格させる**

Run:

```powershell
pwsh -NoProfile -File ./scripts/check-gas-monitor.ps1
dotnet test tests/FieldOps.Domain.Tests/FieldOps.Domain.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProjectDependencyTests"
```

Expected: どちらもPASS。

- [ ] **Step 9: Task 2をコミットする**

```powershell
git add ops/google-apps-script/fieldops-health-monitor/Code.gs scripts/check-gas-monitor.ps1
git commit -m "GAS監視の判定と状態変化通知を実装"
```

---

### Task 3: 非エンジニア向け導入・停止・復旧手順

**Files:**
- Create: `ops/google-apps-script/fieldops-health-monitor/README.md`
- Modify: `README.md`
- Modify: `scripts/check-readme.ps1`

**Interfaces:**
- Produces: Googleスプレッドシートを使う利用者向けの作成、権限承認、開始、停止、異常テスト、復旧テスト手順
- Produces: リポジトリREADMEから運用手順への導線
- Preserves: 既存の日本語README契約、公開デモ説明、テスト実績

- [ ] **Step 1: README契約へGAS監視の必須説明を追加する**

`scripts/check-readme.ps1`の`$requiredPatterns`へ次を追加する。

```powershell
"GAS public monitor" = "## GASによる公開監視"
"hourly monitoring purpose" = "1時間ごとに.*health/live.*health/ready|health/live.*health/ready.*1時間ごと"
"monitor is not keepalive" = "スリープ回避を目的としません"
"monitor operations guide" = "ops/google-apps-script/fieldops-health-monitor/README\.md"
```

さらに運用READMEの存在と、開始・停止・権限・異常・復旧の5見出しを検査する。

```powershell
$gasMonitorReadmePath = Join-Path $repositoryRoot 'ops/google-apps-script/fieldops-health-monitor/README.md'
if (-not (Test-Path -LiteralPath $gasMonitorReadmePath -PathType Leaf)) {
    throw 'GAS monitor operations README was not found.'
}
$gasMonitorReadme = Get-Content -LiteralPath $gasMonitorReadmePath -Raw
foreach ($requiredHeading in @('## 初期設定', '## 監視を開始', '## 監視を停止', '## 異常通知テスト', '## 復旧通知テスト', '## 必要な権限')) {
    if ($gasMonitorReadme -notmatch [regex]::Escape($requiredHeading)) {
        throw "GAS monitor operations README is missing: $requiredHeading"
    }
}
```

- [ ] **Step 2: 運用READMEがないため検査が失敗することを確認する**

Run:

```powershell
pwsh -NoProfile -File ./scripts/check-readme.ps1
```

Expected: `GAS monitor operations README was not found`でFAIL。

- [ ] **Step 3: GAS運用READMEを日本語で作成する**

次の内容を順番どおりに記載する。

1. これは1時間ごとの外形監視で、Render Freeのスリープ回避機能ではない。
2. Googleスプレッドシート`FieldOps 公開監視`を新規作成し、共有設定を非公開のままにする。
3. `拡張機能`→`Apps Script`を開き、`Code.gs`と`appsscript.json`をリポジトリの同名ファイルで置き換える。
4. `初期設定を作成`を実行し、権限を承認する。
5. `監視設定`の公開URLが`https://fieldops-portfolio.onrender.com`、通知先メールが本人のアドレスであることを確認する。
6. `今すぐ確認`で正常履歴を確認する。
7. `テスト用の異常通知`で異常履歴と異常メールを確認する。
8. `テスト状態を戻す`で正常履歴と復旧メールを確認する。
9. `1時間ごとの監視を開始`を実行し、Apps Scriptのトリガー画面で`runHealthCheck`が1件だけであることを確認する。
10. 停止は`監視を停止`を使い、コード、スプレッドシート、FieldOps本体を削除しない。

見出しは最低でも`初期設定`、`監視を開始`、`監視を停止`、`異常通知テスト`、`復旧通知テスト`、`必要な権限`、`困ったとき`を含める。各操作はメニュー名を太字で示し、IT初心者がGoogleの画面で迷わない一操作一文の手順にする。

必要な権限には、現在のスプレッドシート、外部URLアクセス、メール送信、トリガー管理、実行ユーザーメール取得だけを列挙する。Googleパスワード、Renderトークン、Cookie、APIキーは不要と明記する。

- [ ] **Step 4: ルートREADMEへ目的、制約、運用導線を追加する**

`## 公開環境の制約`の後に`## GASによる公開監視`を追加し、次の4点を明記する。

- 1時間ごとに`/health/live`と`/health/ready`を確認する。
- 異常と復旧のときだけメールを送る。
- 監視結果はGoogleスプレッドシートへ記録する。
- 1時間間隔なのでRender Freeのスリープ回避を目的としない。

運用手順は相対リンク`ops/google-apps-script/fieldops-health-monitor/README.md`で案内する。

- [ ] **Step 5: README、GAS、差分検査を合格させる**

Run:

```powershell
pwsh -NoProfile -File ./scripts/check-readme.ps1
pwsh -NoProfile -File ./scripts/check-gas-monitor.ps1
git diff --check
```

Expected: 3コマンドすべて終了コード0。

- [ ] **Step 6: Task 3をコミットする**

```powershell
git add README.md ops/google-apps-script/fieldops-health-monitor/README.md scripts/check-readme.ps1
git commit -m "GAS監視の日本語運用手順を追加"
```

---

### Task 4: Google実環境の正常・異常・復旧検証とGitHub反映

**Files:**
- Create: `docs/evidence/gas-health-monitor-verification.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: 検証済みの`Code.gs`と`appsscript.json`
- Creates externally: 非公開Googleスプレッドシート`FieldOps 公開監視`と、それに紐づくApps Scriptプロジェクト
- Produces externally: `runHealthCheck`の1時間トリガー1件、正常・異常・復旧の監視履歴、承認済み通知先への異常・復旧テストメール各1通
- Produces: 秘密情報を含まない検証証跡
- Preserves: FieldOps本体、Render設定、DB、他GASプロジェクト、他トリガー

- [ ] **Step 1: ローカルの最終ゲートを実行する**

Run:

```powershell
dotnet restore FieldOps.sln --locked-mode
dotnet format FieldOps.sln --verify-no-changes --no-restore
dotnet build FieldOps.sln --configuration Release --no-restore -warnaserror
dotnet test FieldOps.sln --configuration Release --no-build
pwsh -NoProfile -File ./scripts/check-readme.ps1
pwsh -NoProfile -File ./scripts/check-gas-monitor.ps1
git diff --check
```

Expected: すべて終了コード0。既存のテスト件数が変わる場合は、追加したDomain構造契約テスト1件だけが増えていることを確認する。

- [ ] **Step 2: Googleログインと公開環境の事前確認を行う**

実装時は`browser:control-in-app-browser`スキルを読み、ユーザーが選択したブラウザ操作面を使う。`https://sheets.google.com`を開き、Googleアカウントへログイン済みか確認する。未ログインまたはOAuth権限画面で本人操作が必要な場合だけ停止し、パスワードや確認コードを受け取らず、ユーザー本人の操作完了を待つ。

別タブで次を確認する。

```text
https://fieldops-portfolio.onrender.com/health/live  -> HTTP 200 / Healthy
https://fieldops-portfolio.onrender.com/health/ready -> HTTP 200 / Healthy
```

どちらかが正常でなければGAS設定へ進まず、公開環境の障害として記録する。

- [ ] **Step 3: 非公開スプレッドシートと紐づくGASを作成する**

Googleスプレッドシートを1件新規作成し、名前を`FieldOps 公開監視`に変更する。共有設定が`制限付き`であることを確認する。

`拡張機能`→`Apps Script`を開き、既定の`Code.gs`をリポジトリの`Code.gs`全体で置き換える。プロジェクト設定でマニフェスト表示を有効にし、`appsscript.json`をリポジトリの内容と一致させる。プロジェクト名も`FieldOps 公開監視`にする。

コードとマニフェストの保存後、`setupMonitoring`を手動実行する。GoogleのOAuth画面では表示された権限が次の5種類に対応することだけを確認し、それ以外の権限が出た場合は承認せず停止する。

- 現在のスプレッドシート
- 外部URLへの接続
- メール送信
- Apps Scriptトリガー管理
- 実行ユーザーのメールアドレス取得

- [ ] **Step 4: 初期設定と初回正常を実測する**

スプレッドシートへ戻り、`監視設定`と`監視履歴`が作成されていることを確認する。次を目視で照合する。

```text
公開URL: https://fieldops-portfolio.onrender.com
通知先メール: 現在ログインしているユーザー本人のメール
実行間隔: 1時間
再試行回数: 5回
再試行間隔: 15秒
監視状態: 停止中
```

通知先が本人のメールと一致しない場合は、メール送信前に停止してユーザーへ確認する。一致する場合は、承認済みの実環境テストとして`FieldOps監視`→`今すぐ確認`を実行する。

確認項目:

- `監視履歴`へ1行だけ追加される。
- 総合結果、live結果、ready結果がすべて`正常`になる。
- live/ready HTTP状態が200になる。
- 試行回数が1以上5以下になる。
- 初回正常メールは届かない。

- [ ] **Step 5: 異常メールと異常履歴を実測する**

送信先がStep 4で確認した本人メールであることを再確認し、`FieldOps監視`→`テスト用の異常通知`を1回だけ実行する。この操作は`https://fieldops-monitor-test.invalid`を使い、最大約60秒待つ。

確認項目:

- 履歴へ`異常`が1行追加される。
- 試行回数が5になる。
- 件名`[FieldOps監視] 公開環境の異常を検知しました`のメールが1通届く。
- メールにCookie、ヘッダー、レスポンス本文、認証情報が含まれない。
- `監視設定`の公開URLは本番URLのまま変わらない。

メールが届かない場合はApps Scriptの実行履歴を確認し、権限、MailApp上限、通知先の順で原因を切り分ける。同じ操作を無条件に連打しない。

- [ ] **Step 6: 本番ヘルスによる復旧メールを実測する**

`FieldOps監視`→`テスト状態を戻す`を1回だけ実行する。

確認項目:

- 本番のlive/readyを確認した正常行が1行追加される。
- 件名`[FieldOps監視] 公開環境が復旧しました`のメールが1通届く。
- 復旧メールに直前の異常検知日時が含まれる。
- Script Propertiesの`LAST_STATUS`が`UP`になり、`LAST_DOWN_AT`が削除される。

本番ヘルスが`DOWN`の場合は復旧扱いにせず、公開環境の障害として停止する。

- [ ] **Step 7: 毎時トリガーを1件だけ登録する**

`FieldOps監視`→`1時間ごとの監視を開始`を実行する。Apps Script左メニューの`トリガー`で次を確認する。

```text
実行する関数: runHealthCheck
イベントのソース: 時間主導型
時間ベースのトリガーのタイプ: 時間ベースのタイマー
間隔: 1時間ごと
件数: 1件
```

スプレッドシートの`監視状態`が`監視中`であることも確認する。重複があれば開始メニューをもう一度実行し、対象ハンドラーが1件へ整理されることを確認する。他ハンドラーは削除しない。

- [ ] **Step 8: 秘密情報を除いた検証証跡を作成する**

`docs/evidence/gas-health-monitor-verification.md`へ次を実測値で記録する。

```markdown
# GAS死活監視 検証結果

- 検証日: 2026-08-29
- 対象: https://fieldops-portfolio.onrender.com
- スプレッドシート: FieldOps 公開監視（非公開）
- 初回正常: PASS
- 異常履歴: PASS
- 異常メール: PASS
- 復旧履歴: PASS
- 復旧メール: PASS
- runHealthCheck毎時トリガー: 1件
- 監視状態: 監視中
- FieldOps本体変更: なし
- Render設定変更: なし
```

個人メールアドレス、スプレッドシートURL/ID、Apps ScriptプロジェクトID、メール本文、実行ログ全文は記録しない。

`README.md`のGAS監視節からこの証跡へリンクする。

- [ ] **Step 9: 証跡をコミットし、ブランチ全体を再検証する**

```powershell
git add README.md docs/evidence/gas-health-monitor-verification.md
git commit -m "GAS監視の実環境検証結果を記録"
pwsh -NoProfile -File ./scripts/check-readme.ps1
pwsh -NoProfile -File ./scripts/check-gas-monitor.ps1
dotnet test tests/FieldOps.Domain.Tests/FieldOps.Domain.Tests.csproj --configuration Release
git diff --check
git status --short
```

Expected: 全検査PASS、`git status --short`の出力なし。

- [ ] **Step 10: GitHub mainへ安全に反映しCI成功を確認する**

```powershell
git fetch origin
git merge-base --is-ancestor origin/main HEAD
git push origin HEAD:main
gh run list --branch main --limit 5
```

`git merge-base --is-ancestor`が終了コード1の場合はmainが先行しているため、強制プッシュせず、origin/mainを取り込んで競合解消後にTask 4のローカルゲートを再実行する。

対象コミットのCIを次で監視する。

```powershell
$headSha = git rev-parse HEAD
$runId = gh run list --branch main --commit $headSha --json databaseId --jq '.[0].databaseId'
gh run watch $runId --exit-status
```

Expected: CI成功。FieldOps本体とRender設定は変更していないため、公開アプリの機能変更は発生しない。Renderがmain更新で再デプロイする場合は完了を待ち、最後に`/health/live`と`/health/ready`がHTTP 200かつ`Healthy`であることを再確認する。

---

## 完了条件

- リポジトリのGASソース、マニフェスト、検証スクリプト、運用README、実測証跡がmainへ反映されている。
- Domain構造契約、GAS静的検査、README検査、build、全テスト、`git diff --check`が成功している。
- 非公開スプレッドシートに正常、異常、復旧の3履歴がある。
- 異常メールと復旧メールが本人の通知先へ各1通届いている。
- `runHealthCheck`の1時間トリガーが1件だけ存在する。
- 監視状態が`監視中`で、FieldOps本体、DB、Render設定、他GASトリガーに意図しない変更がない。
- GitHub mainの対象CIが成功し、公開ヘルスが再確認できている。

## 参照

- [FieldOps GAS死活監視 設計仕様](../specs/2026-08-29-fieldops-gas-health-monitor-design.md)
- [Apps Script Session](https://developers.google.com/apps-script/reference/base/session)
- [Apps Script authorization](https://developers.google.com/apps-script/guides/services/authorization)
- [Apps Script UrlFetchApp](https://developers.google.com/apps-script/reference/url-fetch/url-fetch-app)
- [Apps Script MailApp](https://developers.google.com/apps-script/reference/mail/mail-app)
- [Apps Script ScriptApp](https://developers.google.com/apps-script/reference/script/script-app)
- [Apps Script SpreadsheetApp](https://developers.google.com/apps-script/reference/spreadsheet/spreadsheet-app)
- [Apps Script ClockTriggerBuilder](https://developers.google.com/apps-script/reference/script/clock-trigger-builder)
