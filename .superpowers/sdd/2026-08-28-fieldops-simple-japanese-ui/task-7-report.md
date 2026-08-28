# Task 7 Report: 作業予定の日本時間入力・状態フィルター・安全な状態変更

## Status

complete

## 実装内容

- Web専用フォームモデル `WorkOrderScheduleForm` と `WorkEventForm` を追加し、画面入力は `ScheduledDate` / `ScheduledTime` と `OccurredDate` / `OccurredTime` に分割した。
- フォームモデルの `ToCommand()` で既存 Feature command DTO (`WorkOrderEditInput`, `WorkEventInput`) へ変換し、保存境界は従来どおりUTCの `DateTime` に限定した。
- `WorkOrderSearchRequest` に `Status` と `Overdue` を追加し、`WorkOrderQueries` に `TimeProvider` を注入して遅延判定だけ固定UTC時刻を使えるようにした。
- WorkOrder一覧へ状態フィルターと遅延フィルターを追加し、一覧はデスクトップテーブル、タブレット以下カード表示にした。
- WorkOrder詳細、日程編集、作業記録追加を日本語UIへ更新し、内部状態・UTC日時・Versionは「詳細を見る」に収納した。
- ホームの作業カードURLを `status=Scheduled`、`status=InProgress`、`status=Completed`、`overdue=true` 付きの作業予定一覧へ接続した。
- 状態変更の全ボタンに Task 3 の共通確認モーダル用 `data-confirm-action` と対象/変更内容/影響のdata属性を付けた。ボタンはsubmitterとして保持され、既存JSの `requestSubmit(submitter)` で `NextStatus` と `Version` を維持する。

## 変更ファイル

- `src/FieldOps.Web/Models/WorkOrderScheduleForm.cs`
- `src/FieldOps.Web/Models/WorkEventForm.cs`
- `src/FieldOps.Features/Work/WorkOrderDtos.cs`
- `src/FieldOps.Features/Work/WorkOrderQueries.cs`
- `src/FieldOps.Web/Controllers/WorkOrdersController.cs`
- `src/FieldOps.Web/Services/DashboardPageModelFactory.cs`
- `src/FieldOps.Web/Views/WorkOrders/Index.cshtml`
- `src/FieldOps.Web/Views/WorkOrders/Details.cshtml`
- `src/FieldOps.Web/Views/WorkOrders/Edit.cshtml`
- `src/FieldOps.Web/Views/WorkOrders/AddEvent.cshtml`
- `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`
- `tests/FieldOps.E2ETests/Pages/WorkOrderPage.cs`

## RED / GREEN

- RED: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkOrderFeatureTests"` failed after switching tests to `ScheduledDate` / `ScheduledTime` and `OccurredDate` / `OccurredTime`; failures were the expected old UTC form names and English action labels.
- GREEN: after implementing Web form models, controller conversion, JST Razor inputs, query filters, and confirmation attributes, the same command passed: 15/15.

## 対象テスト

- `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkOrderFeatureTests"` -> passed, 15/15.
- `dotnet build src/FieldOps.Web/FieldOps.Web.csproj -c Release` -> passed, 0 warnings, 0 errors. This is required because E2E starts the Release web assembly.
- `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~FieldTechnicianTests|FullyQualifiedName~BranchManagerTests" -- Playwright.BrowserName=chromium` -> passed, 2/2.

## 全体テスト

- `dotnet test` -> passed.
  - Domain: 62/62.
  - E2E: 19/19.
  - Integration: 201/201.

## UTC / JST 往復

- Schedule form input `2026-09-20` + `10:30` JST is converted by `JapanTimeFormatter.ToUtc` and persisted as `2026-09-20T01:30:00Z`.
- Work event input `2026-08-11` + `12:15` JST is converted and persisted as `2026-08-11T03:15:00Z`.
- GET edit forms split existing UTC values back through `JapanTimeFormatter.ToJapanDate` and `ToJapanTime`.
- WorkOrder edit/add-event screens no longer expose UTC, ISO 8601, or trailing-Z text inputs.

## 遅延基準

- `WorkOrderQueries.SearchAsync` uses injected `TimeProvider.GetUtcNow().UtcDateTime` only when `Overdue` is true.
- Overdue includes only `Scheduled` or `InProgress` work with `ScheduledStartUtc < utcNow`.
- The integration test fixes `utcNow` at `2026-09-20T02:00:00Z` and verifies the scheduled `2026-09-20T01:30:00Z` work appears in `overdue=true`.

## スコープ維持

- WorkOrderCommands, domain transition rules, audit writer behavior, concurrency checks, assigned technician scope, and branch authorization were not changed.
- `GetDetailsAsync` loads events with the WorkOrder entity and still delegates allowed transition calculation to `WorkOrder.GetAllowedTransitions()`.
- Existing manager, sales, technician, tamper, stale version, correction, future-date, concurrent transition, concurrent event, and assignment-race tests remained green.

## 競合 / 入力保持

- Schedule POST now binds to `WorkOrderScheduleForm`.
- On planned-work concurrency, the controller rereads the latest version/status and updates only `Version` and `Status` on the submitted form, preserving the user's `ScheduledDate`, `ScheduledTime`, and selected technician input where it is still a valid option.
- If the latest work order is no longer Planned, the existing conflict details path is preserved.
- Work event concurrency keeps the submitted Japanese date/time/summary and refreshes only `Version`.

## 確認モーダル自己レビュー

- All rendered transition submit buttons have `data-confirm-action`.
- Target, message, and impact are set per work order and next status.
- `Version` and `NextStatus` remain inside the original form, and the existing Task 3 JS submits via `event.submitter` + one-shot confirmed flag + `requestSubmit(submitter)`.
- The shared modal is not duplicated in Details; it remains supplied by the layout.

## 懸念

- None known. E2E uses the Release web assembly, so Release build must precede E2E runs when Razor/UI changes are made.
