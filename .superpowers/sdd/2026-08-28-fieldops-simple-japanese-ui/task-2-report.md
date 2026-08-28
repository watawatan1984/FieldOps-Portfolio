# Task 2 Report: 日本語デモ利用者・ログイン・固定データ

## 実装内容

- `DemoRoleCardViewModel` に `RoleLabel` を追加し、`Role` は内部ロール名として維持した。
- `DemoLoginController` は `UiDisplayText.ForRole(role)` から `RoleLabel` を導出し、デモ利用者名と説明文を日本語化した。
- `DemoLogin/Index.cshtml` は日本語の見出し、説明、ロール見出し、ログインボタンを表示し、フォーム付近の `data-role` に内部英語ロール名を保持するようにした。
- `DemoDataManifest` と `DemoDataSeeder` の表示用固定データを日本語化した。固定 ID、件数、ロール、ユーザー名、権限関係、DatasetVersion は変更していない。
- ログイントークン抽出テストは見出しではなく `data-role` から内部ロール名を参照するよう更新した。
- seed 表示名変更に伴う統合テストと E2E の期待値を日本語へ合わせた。

## 変更ファイル

- `src/FieldOps.Web/Models/DemoRoleCardViewModel.cs`
- `src/FieldOps.Web/Controllers/DemoLoginController.cs`
- `src/FieldOps.Web/Views/DemoLogin/Index.cshtml`
- `src/FieldOps.Infrastructure/Demo/DemoDataManifest.cs`
- `src/FieldOps.Infrastructure/Demo/DemoDataSeeder.cs`
- `tests/FieldOps.IntegrationTests/Authorization/DemoLoginTests.cs`
- `tests/FieldOps.IntegrationTests/Administration/DemoResetTests.cs`
- `tests/FieldOps.IntegrationTests/Authorization/AuthorizationPolicyTests.cs`
- `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
- `tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs`
- `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`
- `tests/FieldOps.IntegrationTests/Concurrency/ConcurrentMutationTests.cs`
- `tests/FieldOps.IntegrationTests/Diagnostics/DiagnosticsTests.cs`
- `tests/FieldOps.IntegrationTests/Failures/FailurePathTests.cs`
- `tests/FieldOps.IntegrationTests/Features/WorkHistorySearchTests.cs`
- `tests/FieldOps.IntegrationTests/Security/SecurityRegressionTests.cs`
- `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs`
- `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`
- `tests/FieldOps.E2ETests/Accessibility/AccessibilitySmokeTests.cs`

## RED

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DemoResetTests"
```

Expected failure occurred:

- `DemoResetTests.ResetRestoresTheDeterministicManifestAndStableIdentifiers` failed because branch name was still `Fictional Central Service Branch` instead of `中央サービス支店`.
- `DemoLoginTests.LoginPageOffersExactlyFourPublicRolesWithoutPasswordInput` failed because `担当する仕事を選んでください` was not present.
- Login token helpers failed after being moved to `data-role` because the existing page did not emit `data-role` yet.

Output summary:

```text
失敗!   -失敗:     5、合格:    38、スキップ:     0、合計:    43、期間: 55 s - FieldOps.IntegrationTests.dll (net10.0)
```

## GREEN

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DemoResetTests"
```

Output:

```text
成功!   -失敗:     0、合格:    43、スキップ:     0、合計:    43、期間: 53 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DemoResetTests|FullyQualifiedName~AuthorizationPolicyTests"
```

Output:

```text
成功!   -失敗:     0、合格:    51、スキップ:     0、合計:    51、期間: 1 m 2 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
dotnet build src/FieldOps.Web -c Release
dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles" -- Playwright.BrowserName=chromium
```

Output:

```text
ビルドに成功しました。
    0 個の警告
    0 エラー

成功!   -失敗:     0、合格:     4、スキップ:     0、合計:     4、期間: 8 s - FieldOps.E2ETests.dll (net10.0)
```

## 全体確認

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests|FullyQualifiedName~PartyFeatureTests|FullyQualifiedName~SalesFeatureTests|FullyQualifiedName~WorkOrderFeatureTests|FullyQualifiedName~ModelMappingTests"
```

Output:

```text
成功!   -失敗:     0、合格:    66、スキップ:     0、合計:    66、期間: 1 m 6 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
dotnet test FieldOps.sln
```

Output:

```text
成功!   -失敗:     0、合格:    62、スキップ:     0、合計:    62、期間: 59 ms - FieldOps.Domain.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    16、スキップ:     0、合計:    16、期間: 27 s - FieldOps.E2ETests.dll (net10.0)
成功!   -失敗:     0、合格:   192、スキップ:     0、合計:   192、期間: 2 m 43 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
git diff --check
```

Output:

```text
exit code 0; whitespace errors none. Git reported expected LF-to-CRLF working-copy warnings.
```

## 自己レビュー

- `RoleLabel` は `UiDisplayText.ForRole(role)` から導出しており、ロール変換 switch の複製は production code に追加していない。
- フォームの `data-role` と protected role token は内部英語ロール名を保持している。
- `DemoDataManifest` / `DemoDataSeeder` は表示文字列のみ変更し、固定 ID、件数、DatasetVersion、UserName、SecurityStamp、ConcurrencyStamp、権限関係は維持した。
- ASP.NET の動的 HTML 出力では日本語がエンティティ化される箇所があるため、統合テストの画面文字列比較は必要箇所で `WebUtility.HtmlDecode` して確認した。

## 懸念

- E2E fixture は Release の Web DLL を直接起動するため、E2E 前に `dotnet build src/FieldOps.Web -c Release` が必要だった。最終確認では Release build 後に E2E を実行済み。

## Fix Round 1 Evidence

### 修正前の再現

Review HEAD: `3fe9af0`

Command:

```powershell
rg -n "RoleLabel\(|Html\.Raw|Fictional Central Nav|Fictional Field Nav|Fictional Orchard|Fictional Central Backlog|data-role" tests\FieldOps.E2ETests\Pages\DemoLoginPage.cs src\FieldOps.Web\Views\DemoLogin\Index.cshtml tests\FieldOps.IntegrationTests\Features\DashboardTests.cs tests\FieldOps.IntegrationTests\Features\WorkOrderFeatureTests.cs
```

Result:

- `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs` に英語ロールから日本語ラベルへの `RoleLabel` switch が残っていた。
- `src/FieldOps.Web/Views/DemoLogin/Index.cshtml` に `Html.Raw(card.RoleLabel)` / `Html.Raw(card.DisplayName)` / `Html.Raw(card.Description)` / `Html.Raw(card.RoleLabel)` が残っていた。
- `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs` に `Fictional Central Nav Customer` / `Fictional Field Nav Customer` / partner names が残っていた。
- `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs` に `Fictional Orchard Facilities` / `Fictional Orchard Annex` / `Fictional Central Backlog` が残っていた。

Command:

```powershell
dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles|FullyQualifiedName~AccessibilitySmokeTests" -- Playwright.BrowserName=chromium
```

Result before rebuilding Release Web after source changes:

```text
失敗!   -失敗:     6、合格:     1、スキップ:     0、合計:     7、期間: 1 m 3 s - FieldOps.E2ETests.dll (net10.0)
```

Failure showed the E2E helper was looking for `form[data-role="..."]` before the Release Web DLL had the updated form markup.

### 修正内容

- `DemoLogin/Index.cshtml` は `Html.Raw` をすべて通常 Razor 出力へ戻し、`data-role="@card.Role"` を `section` ではなく `form` に移した。
- `DemoLoginPage.LoginAsAsync` は日本語ラベル変換を削除し、`form[data-role="{role}"]` の submit button をクリックするようにした。
- `DemoLoginTests` は Razor が動的日本語を HTML encode する前提で、ロールラベル確認だけ `WebUtility.HtmlDecode(html)` を使うようにした。
- `DashboardTests` の navigation party names を `架空中央支店 顧客`、`架空現場支店 顧客`、`架空中央支店 協力会社`、`架空現場支店 協力会社` へ変更した。
- `WorkOrderFeatureTests` の表示用 party/site/backlog names を `架空果樹園設備`、`架空果樹園 別館`、`架空中央支店 未処理作業`、`架空中央支店 未処理現場` へ変更した。

### カバーしたテストファイル

- `tests/FieldOps.IntegrationTests/Authorization/DemoLoginTests.cs`
- `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`
- `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs`
- `tests/FieldOps.E2ETests/Accessibility/AccessibilitySmokeTests.cs`
- `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`

### 修正後コマンドと出力

Command:

```powershell
dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~WorkOrderFeatureTests"
```

Output:

```text
成功!   -失敗:     0、合格:    32、スキップ:     0、合計:    32、期間: 37 s - FieldOps.IntegrationTests.dll (net10.0)
```

Command:

```powershell
dotnet build src/FieldOps.Web -c Release
dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles|FullyQualifiedName~AccessibilitySmokeTests" -- Playwright.BrowserName=chromium
```

Output:

```text
ビルドに成功しました。
    0 個の警告
    0 エラー

成功!   -失敗:     0、合格:     7、スキップ:     0、合計:     7、期間: 11 s - FieldOps.E2ETests.dll (net10.0)
```

Command:

```powershell
Select-String -Path 'tests\FieldOps.E2ETests\Pages\DemoLoginPage.cs','src\FieldOps.Web\Views\DemoLogin\Index.cshtml','tests\FieldOps.IntegrationTests\Features\DashboardTests.cs','tests\FieldOps.IntegrationTests\Features\WorkOrderFeatureTests.cs' -Pattern 'RoleLabel\(','Html.Raw','Fictional Central Nav','Fictional Field Nav','Fictional Orchard','Fictional Central Backlog','section class="card h-100" data-role','form method="post" asp-controller="DemoLogin"'
git diff --check
```

Output:

```text
src\FieldOps.Web\Views\DemoLogin\Index.cshtml:18:<form method="post" asp-controller="DemoLogin" asp-action="Index" data-role="@card.Role">
git diff --check exit code 0; whitespace errors none. Git reported expected LF-to-CRLF working-copy warnings.
```

### 必要な全体確認

Command:

```powershell
dotnet test FieldOps.sln
```

Output:

```text
成功!   -失敗:     0、合格:    62、スキップ:     0、合計:    62、期間: 50 ms - FieldOps.Domain.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    16、スキップ:     0、合計:    16、期間: 25 s - FieldOps.E2ETests.dll (net10.0)
成功!   -失敗:     0、合格:   192、スキップ:     0、合計:   192、期間: 2 m 42 s - FieldOps.IntegrationTests.dll (net10.0)
```

### 自己レビュー

- E2E helper にロール表示ラベル変換は残っていない。
- `data-role` は brief の契約どおり、内部英語ロール名を `form` に保持している。
- Demo login view は通常 Razor encoding に戻っており、`Html.Raw` は残っていない。
- 指摘された navigation/work-order 用の画面表示名は自然な日本語に置換済み。
- 既存の固定 ID、内部ロール名、UserName、DatasetVersion、権限関係は変更していない。
