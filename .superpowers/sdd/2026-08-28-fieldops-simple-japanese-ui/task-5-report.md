# Task 5 report: 顧客・協力会社をカード中心の日本語導線へ変更

## 実装内容
- 顧客、協力会社、全取引先の一覧を日本語導線へ変更し、PC は 4 列以内の一覧、タブレット以下は `task-card` のカード表示にした。
- 一覧上部に説明文、具体的な検索ラベル、`この条件で探す`、空状態、`前へ` / `次へ` / `全N件` のページ送りを追加した。
- 詳細画面の主要操作を `顧客情報を変更する` にし、連絡先、現場、担当支店を日本語見出しと空状態にした。
- 登録/編集画面に必須バッジ、具体的な送信ボタン、送信しない `前の画面へ戻る` リンクを追加した。
- DataAnnotations と Party controller の既知失敗メッセージを日本語化し、ドメイン例外の生メッセージを画面に出さないようにした。
- 表示上の顧客/協力会社ラベルは `UiDisplayText.ForPartyRole` を使用した。

## 変更ファイル
- `src/FieldOps.Features/Parties/PartyDtos.cs`
- `src/FieldOps.Web/Controllers/PartiesController.cs`
- `src/FieldOps.Web/Views/Customers/Index.cshtml`
- `src/FieldOps.Web/Views/BusinessPartners/Index.cshtml`
- `src/FieldOps.Web/Views/Parties/Index.cshtml`
- `src/FieldOps.Web/Views/Parties/Details.cshtml`
- `src/FieldOps.Web/Views/Parties/Create.cshtml`
- `src/FieldOps.Web/Views/Parties/Edit.cshtml`
- `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
- `tests/FieldOps.E2ETests/Pages/PartyPage.cs`
- `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`

## RED / GREEN
- RED: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"` -> 4 failed / 11 passed. Missing Japanese customer search, empty state, validation, concurrency messages.
- GREEN: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"` -> 15 passed.

## 対象テスト
- `dotnet build src\FieldOps.Web\FieldOps.Web.csproj -c Release` -> succeeded, 0 warnings, 0 errors. Required because E2E fixture launches the Release web DLL.
- `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~BranchManagerTests|FullyQualifiedName~SalesRepresentativeTests" -- Playwright.BrowserName=chromium` -> 3 passed.

## 全体テスト
- `dotnet test` -> Domain 62 passed, E2E 18 passed, Integration 196 passed.
- `git diff --check` -> exit 0. Git reported LF-to-CRLF normalization warnings only.

## 自己レビュー
- 支店スコープ: `AuthorizeBranchAsync` / `AuthorizePartyAsync` と `BranchResourceAction.ManageParties` の呼び出しは維持。検索 DTO の `BranchId`、詳細/編集/共有の branchId flow は変更していない。
- 権限: controller の `[Authorize(Policy = Policies.ManageParties)]` と resource authorizer は変更していない。
- 内部値: `PartyRoleType.Customer` / `PartyRoleType.BusinessPartner`、post form values、demo login `data-role`、固定 ID、Party command/domain 契約は変更していない。表示ラベルだけ `UiDisplayText.ForPartyRole` に寄せた。
- UI 抽象: 新しい依存や CSS 抽象は追加せず、既存の `task-card`、`responsive-records`、`details-disclosure`、`app-empty-state` を再利用した。

## 懸念
- `PartyFeatureTests` の英語非表示確認は Layout の内部 `data-policy="ManageParties"` を除外して判定している。利用者に見える顧客画面の英語混入を確認するための調整。
- E2E fixture が Release DLL を直接起動するため、E2E 実行前に Release ビルドが必要。

## Fix round 1 evidence

### 修正前再現
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"` -> 13 passed / 3 failed。`d-none d-xl-block` 不在、協力会社作成 GET が顧客文言、RoleType 未送信が redirect になることを確認。
- `dotnet test tests\FieldOps.E2ETests --filter "FullyQualifiedName~TabletLandscapeCustomerListUsesCards" -- Playwright.BrowserName=chromium` -> 0 passed / 1 failed。1024px 幅で table が表示され、カードの `顧客の情報を見る` が見えないことを確認。

### 修正内容
- `Customers/Index.cshtml`、`BusinessPartners/Index.cshtml`、`Parties/Index.cshtml` の一覧 table を `d-none d-xl-block`、カードを `d-xl-none` に変更し、1024px をカード表示に固定。
- 顧客一覧 CTA は `role=Customer`、協力会社一覧 CTA は `role=BusinessPartner` を渡すように変更。全取引先一覧の CTA は中立文言のまま role なし。
- `PartiesController.Create` GET で role query を検証し、顧客/協力会社/中立の title/H1/submit 文言と初期選択を切り替え。
- `CreatePartyInput.RoleType` を nullable にし、空選択・不正値を `顧客または協力会社を選んでください。` で拒否。各 `StringLength` に自然な日本語 `ErrorMessage` を追加。
- `PartyCommands.CreateAsync` は controller/DTO validation 後の nullable role を明示的に確認してから既存の `party.AddRole(roleType)` へ渡すようにし、domain/command の内部 enum 契約は維持。

### 追加・更新テスト
- `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
  - 顧客/協力会社/全取引先一覧が `xl` breakpoint で table/card を切り替えることをビュー検査で固定。
  - 協力会社登録導線が `role=BusinessPartner` を渡し、Create GET の初期選択・H1・submit 文言が協力会社向けになることを固定。
  - 協力会社登録 POST が `BusinessPartner` role として保存され、誤って `Customer` role を付けないことを確認。
  - RoleType 不正・未送信・空文字と長すぎる文字列が BadRequest になり、日本語 validation 文言を返し、ASP.NET 既定英語文言を画面に出さないことを確認。
- `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
  - 1024x768 viewport で顧客一覧がカード表示になり、table が hidden になることを固定。

### GREEN / 影響テスト / 全体確認
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"` -> 16 passed / 0 failed。
- `dotnet build src\FieldOps.Web\FieldOps.Web.csproj -c Release` -> succeeded, 0 warnings, 0 errors。
- `dotnet test tests\FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~BranchManagerTests|FullyQualifiedName~SalesRepresentativeTests" -- Playwright.BrowserName=chromium` -> 4 passed / 0 failed。
- `dotnet test` -> Domain 62 passed, E2E 19 passed, Integration 197 passed。
- `git diff --check` -> exit 0。LF-to-CRLF normalization warnings only。

### Fix round 1 自己レビュー
- 支店スコープ: `AuthorizeBranchAsync`、`AuthorizePartyAsync`、`BranchResourceAction.ManageParties` は変更なし。role query は表示初期値だけに使い、branchId scope を広げていない。
- 権限: controller policy と resource authorization は変更なし。
- 内部値: `PartyRoleType.Customer` / `PartyRoleType.BusinessPartner`、保存 role、demo `data-role`、固定 ID は変更なし。表示は既存 `UiDisplayText.ForPartyRole` を使用。
- UI 抽象: 新しい CSS/JS/依存は追加せず、Task 3 の `task-card` / `responsive-records` の既存パターンを維持。
- 懸念: E2E fixture は Release DLL 起動のため、E2E 前の Release build が引き続き必要。Minor 指摘の全取引先ページ語調は親台帳どおり対象外。

## Fix round 2 evidence

### 修正前確認
- Review対象 `80bcf3688f4eaba0f39481214c86d535a9af6fe2` の `PartiesController.Create` GET は `[FromQuery] PartyRoleType? role` へ直接 bind しており、`role=NotARole` が ASP.NET enum binder を通る状態だった。
- 回帰テスト `InvalidCreateRoleQueryFallsBackToNeutralCreatePageWithoutBinderError` を先に追加し、現行 view では 1 passed / 0 failed で画面露出までは再現しなかったが、指摘された direct enum bind 経路が残っていることをコード差分で確認した。

### 修正内容
- `PartiesController.Create` GET の query parameter を `string? role` に変更し、`Enum.TryParse` + `Enum.IsDefined` で既知の `Customer` / `BusinessPartner` だけを初期選択へ変換。
- 不正な role query は `null` 扱いにして、中立 H1 `顧客・協力会社を登録する` と submit `この内容で登録する` にフォールバック。
- POST 側の nullable `RoleType` validation、日本語 validation 文言、顧客/協力会社 role query 契約は変更なし。

### 追加テスト
- `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
  - `/parties/create?branchId=...&role=NotARole` が 200 を返すこと。
  - 中立 H1 / 中立 submit 文言になること。
  - `Customer` / `BusinessPartner` が selected にならないこと。
  - `The value 'NotARole' is not valid`、`is not valid for`、`The field` が画面に出ないこと。

### GREEN / 影響確認
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~BusinessPartnerCreateRouteDefaultsAndPersistsBusinessPartnerRole|FullyQualifiedName~InvalidCreateRoleQueryFallsBackToNeutralCreatePageWithoutBinderError|FullyQualifiedName~DuplicateAndInvalidCreateReturnDeterministicUiWithoutAudit"` -> 3 passed / 0 failed。
- `dotnet test tests\FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"` -> 17 passed / 0 failed。
- `dotnet build src\FieldOps.Web\FieldOps.Web.csproj -c Release` -> succeeded, 0 warnings, 0 errors。
- `dotnet test tests\FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~BranchManagerTests|FullyQualifiedName~SalesRepresentativeTests" -- Playwright.BrowserName=chromium` -> 4 passed / 0 failed。
- `dotnet test` -> Domain 62 passed, E2E 19 passed, Integration 198 passed。
- `git diff --check` -> exit 0。LF-to-CRLF normalization warnings only。

### Fix round 2 自己レビュー
- 支店スコープ: `branchId` authorization flow は変更なし。不正 role query は支店 scope や検索条件へ影響しない。
- 権限: policy / resource authorization は変更なし。
- 内部値: `PartyRoleType.Customer` / `PartyRoleType.BusinessPartner` の enum 値、POST form values、保存 role は変更なし。GET query の解釈だけを安全化。
- 懸念: 修正前の追加テストは現行 view で画面露出まではREDにならなかったため、RED evidence は direct enum bind 経路のコード確認として記録。実装後は binder を通さない構造と統合テストで固定済み。
