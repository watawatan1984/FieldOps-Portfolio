# Task 4 Report: 役割別「今日やること」ホーム

## 実装内容

- `DashboardPageViewModel` と `DashboardActionCard` を追加した。
- `DashboardPageModelFactory.Create(DashboardMetrics metrics, string role, Guid? branchId)` を追加し、役割別の「今日やること」「確認が必要」を固定順で生成するようにした。
- `HomeController` は既存 `DashboardQueries.GetAsync(branchId)` の結果を表示モデルへ包むだけに変更した。`DashboardMetrics`、集計クエリ、支店スコープは変更していない。
- ホームViewを「挨拶」→「今日やること」→「確認が必要」→「詳しい集計を見る」の順へ変更した。
- 既存集計カードは `<details class="details-disclosure">` 内に残し、既存 `data-metric` / `data-value` を維持した。
- 0件カードは否定的・エラー風にせず、`該当なし。今は追加対応はいりません。` と表示するようにした。
- E2Eページオブジェクトと4役割テストに、役割別先頭カードの確認を追加した。

## 変更ファイル

- `src/FieldOps.Web/Models/DashboardPageViewModel.cs`
- `src/FieldOps.Web/Services/DashboardPageModelFactory.cs`
- `src/FieldOps.Web/Program.cs`
- `src/FieldOps.Web/Controllers/HomeController.cs`
- `src/FieldOps.Web/Views/Home/Index.cshtml`
- `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- `tests/FieldOps.E2ETests/Pages/DashboardPage.cs`
- `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs`
- `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`

## RED

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests"
```

Output:

```text
error CS0246: 型または名前空間の名前 'DashboardMetrics' が見つかりませんでした
error CS0246: 型または名前空間の名前 'DashboardPageViewModel' が見つかりませんでした
error CS0246: 型または名前空間の名前 'DashboardPageModelFactory' が見つかりませんでした
```

## GREEN / 対象確認

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests"
```

Output:

```text
成功!   -失敗:     0、合格:    14、スキップ:     0、合計:    14、期間: 16 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
dotnet build src/FieldOps.Web/FieldOps.Web.csproj -c Release
```

Output:

```text
ビルドに成功しました。
    0 個の警告
    0 エラー
```

Command:

```powershell
dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles" -- Playwright.BrowserName=chromium
```

Output:

```text
成功!   -失敗:     0、合格:     4、スキップ:     0、合計:     4、期間: 6 s - FieldOps.E2ETests.dll (net10.0)
```

## 全体確認

Command:

```powershell
dotnet test FieldOps.sln
```

Output:

```text
成功!   -失敗:     0、合格:    62、スキップ:     0、合計:    62、期間: 67 ms - FieldOps.Domain.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    18、スキップ:     0、合計:    18、期間: 13 s - FieldOps.E2ETests.dll (net10.0)
成功!   -失敗:     0、合格:   196、スキップ:     0、合計:   196、期間: 2 m 24 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
git diff --check
```

Output:

```text
exit code 0
```

Note: Windowsの改行警告は表示されたが、空白エラーはなかった。

## 自己レビュー

- 支店スコープ: `HomeController` の既存 `branchId` 決定と `DashboardQueries.GetAsync(branchId)` を維持。Factoryは受け取った `branchId` を一覧リンクへ渡すだけで、集計や認可範囲を広げていない。
- 0件状態: カード内で `Count == 0` のとき `該当なし。今は追加対応はいりません。` を表示。空の全体状態も `app-empty-state` で肯定的に表示。
- data属性維持: `Index.cshtml` の詳細集計内に `open-opportunities`、`proposals-due`、`scheduled-work`、`work-in-progress`、`overdue-work`、`completions-this-month` の `data-metric` / `data-value` を維持。
- SQL/集計: `FieldOps.Features.Dashboard.DashboardQueries` と `DashboardMetrics` は未変更。DashboardTests の SQLコマンド数回帰も対象テストで通過。
- 一時リンク裁定: 作業予定・営業案件リンクは現行一覧へ接続。Task 7で状態/期限フィルターURLへ更新予定。

## 懸念

- E2E Fixtureは `bin/Release/net10.0/FieldOps.Web.dll` を直接起動するため、役割E2E前にReleaseビルドが必要だった。今回は `dotnet build src/FieldOps.Web/FieldOps.Web.csproj -c Release` 実行後に通過確認済み。
