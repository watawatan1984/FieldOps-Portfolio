# Task 8 Report: 作業履歴・支店状況・変更履歴・エラー画面の日本語化

## Status

Complete

## 変更

- 作業履歴の初期条件を予定日、完了日、状態、キーワードに絞り、支店、顧客、協力会社、現場、記録種別、担当者は「条件を追加する」に収納した。
- 作業履歴、支店状況、支店詳細、変更履歴、デモ初期化、共通エラー画面の利用者向け表示を日本語化した。
- 作業履歴と支店詳細の日時は `JapanTimeFormatter` でJST表示にし、UTC/ISO値を通常表示から外した。
- 支店状況は遅延件数を先頭に表示し、カード中心のレスポンシブ表示を追加した。
- 変更履歴は `UiDisplayText` で表示だけ日本語化し、`data-audit-action` と監査保存値は英語のまま維持した。
- `/status/{code:int}` に403/404専用の安全な日本語回復画面を追加し、403/404以外は成功表示しない。
- HTMLエラー応答用 `SafeHtmlErrorResponse` を追加し、500系HTMLでは例外、stack、secretを表示せず相関IDだけを表示するようにした。JSON応答は既存 `{ correlationId }` 契約を維持した。
- Reset画面の `RESET` 入力、二重実行防止、処理中表示、相関ID表示を維持しつつ、共通確認モーダルを通すようにした。

## RED / GREEN

- RED: `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~FailurePathTests"` が作業履歴、支店状況、監査、403/404、HTML 500の期待で失敗することを確認した。
- GREEN: 同じ対象Integrationが 43 passed / 0 failed になった。
- GREEN: 指定E2Eが 1 passed / 0 failed になった。

## 対象テスト

- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~FailurePathTests"` -> 43 passed, 0 failed
- `dotnet build src\FieldOps.Web\FieldOps.Web.csproj -c Release` -> succeeded, 0 warnings, 0 errors
- `dotnet test tests\FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~ResetPage" -- Playwright.BrowserName=chromium` -> 1 passed, 0 failed

## 全体テスト

- `dotnet test FieldOps.sln --configuration Release --no-restore` -> Domain 62 passed, E2E 19 passed, Integration 203 passed, 0 failed
- `git diff --check` -> exit code 0

## 機密漏えい自己レビュー

- HTML 500応答は `SafeHtmlErrorResponse` で固定文言、ホームリンク、相関IDのみを返す。
- `HtmlFailuresReturnJapaneseRecoveryPageWithoutLeakingExceptionDetails` でsecret、例外型、stack風文字列が本文に出ないことを確認した。
- 既存JSONエラー応答は `{ correlationId }` のみで、secret非表示の既存テストを維持した。
- 監査画面は詳細値、利用者ID、secret風フィールドを表示しない既存契約を維持した。

## 既知懸念

- `git diff --check` 実行時にWindowsのCRLF変換警告は出たが、空白エラーはない。
- 公開環境へのdeployや公開デモ初期化はTask 8の範囲外で未実行。

## Fix round 1

### Status

Complete

### 変更

- `StatusController.Index` は403/404以外の `/status/{code:int}` を `BadRequest` で返すようにし、`/status/200` や `/status/302` が成功・リダイレクト扱いにならないようにした。
- `StatusController.Index` の対応メソッドをGET/POST/HEADに限定し、既存のunsupported method 405契約を維持した。
- `Views/Status/Index.cshtml` の `href="javascript:history.back()"` を廃止し、`href="/" data-history-back` に変更した。
- `site.js` に `data-history-back` の外部スクリプト処理を追加し、履歴がある場合だけ `history.back()`、ない場合は `/` フォールバックへ進むようにした。

### RED / GREEN

- RED: `StatusCodePagesRenderSafeJapaneseRecoveryOnlyForForbiddenAndNotFound` に `/status/200`、`/status/302`、`href="javascript:"` 禁止の期待を追加し、既存実装で失敗することを確認した。
- GREEN: 同テストが 1 passed / 0 failed になった。
- GREEN: 405回帰を `ReturnUrlCannotOpenRedirectLoginOrLogoutAndUnsupportedMethodsReturn405` と合わせて確認し、2 passed / 0 failed になった。

### 対象テスト

- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~StatusCodePagesRenderSafeJapaneseRecoveryOnlyForForbiddenAndNotFound"` -> 1 passed, 0 failed
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~StatusCodePagesRenderSafeJapaneseRecoveryOnlyForForbiddenAndNotFound|FullyQualifiedName~ReturnUrlCannotOpenRedirectLoginOrLogoutAndUnsupportedMethodsReturn405"` -> 2 passed, 0 failed
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~FailurePathTests"` -> 43 passed, 0 failed
- `dotnet build src\FieldOps.Web\FieldOps.Web.csproj -c Release` -> succeeded, 0 warnings, 0 errors
- `dotnet test tests\FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~ResetPage" -- Playwright.BrowserName=chromium` -> 1 passed, 0 failed

### 全体テスト

- `dotnet test FieldOps.sln --configuration Release --no-restore` -> Domain 62 passed, E2E 19 passed, Integration 203 passed, 0 failed
- `git diff --check` -> exit code 0

### 機密漏えい自己レビュー

- 403/404以外の `/status/{code:int}` は本文付き成功画面を返さず、意図しない200/302を公開しない。
- 戻り導線は `href="/"` をフォールバックにし、インラインJavaScript URLを使わないためCSP `script-src 'self'` と整合する。

### 既知懸念

- `git diff --check` 実行時にWindowsのCRLF変換警告は出たが、空白エラーはない。
- Minorのformat全面調整はTask 10へdeferred。
