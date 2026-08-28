# FieldOps かんたん日本語UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** FieldOps Portalを、PCとタブレットでIT初心者・高齢者が迷わず安全に使える、役割別・今日の仕事中心の日本語UIへ変更する。

**Architecture:** 業務ロジック、権限、支店スコープ、監査、PostgreSQLスキーマは維持し、Web層に日本語表示・日本時間入力・初心者向け表示モデルの境界を追加する。Razor Viewと既存Bootstrapを段階的に更新し、画面契約は既存の統合テストとPlaywright E2Eテストで固定する。

**Tech Stack:** .NET 10、ASP.NET Core MVC、Razor、Bootstrap、PostgreSQL、xUnit、Microsoft Playwright、PowerShell、Docker、GitHub Actions、Render Free

**Spec:** `docs/superpowers/specs/2026-08-27-fieldops-simple-japanese-ui-design.md`

## Global Constraints

- 人が読む画面文言、ボタン、案内、バリデーション、エラー、状態名、デモデータは自然な日本語にする。
- 内部のクラス名、プロパティ名、Enum名、認可ロール名、監査保存値、ログは英語のまま維持する。
- PCとタブレット横向き・縦向きを主要対象とする。
- 本文は原則18px以上、補助文は16px以上、主要操作対象は原則48px以上とする。
- かんたん画面を標準にし、専門情報は「詳細を見る」に収納する。表示モード切替は追加しない。
- 通常画面でUTCやISO 8601形式を直接入力させない。表示と入力はAsia/Tokyo、保存と監査はUTCを維持する。
- 色だけで状態を表現せず、日本語ラベルと説明を併用する。
- 取消、完了、状態変更、デモ初期化には結果を説明する確認段階を置く。
- 認可ポリシー、支店スコープ、状態遷移、監査、固定ID、PostgreSQLスキーマを変更しない。
- 新しい有料サービス、UIフレームワーク、依存パッケージを追加しない。
- 各タスクはテストを先に変更して失敗を確認し、最小実装、対象テスト、コミットの順で完了する。

---

### Task 1: 日本語表示・日本時間・状態名の共通契約

**Files:**
- Create: `src/FieldOps.Web/Formatting/UiDisplayText.cs`
- Modify: `src/FieldOps.Web/Formatting/JapanTimeFormatter.cs`
- Modify: `src/FieldOps.Web/Program.cs`
- Create: `tests/FieldOps.IntegrationTests/Presentation/JapanesePresentationTests.cs`

**Interfaces:**
- Produces: `UiDisplayText.ForRole(string)`, `UiDisplayText.ForPartyRole(PartyRoleType)`, `UiDisplayText.ForSalesStatus(SalesOpportunityStatus)`, `UiDisplayText.ForWorkOrderStatus(WorkOrderStatus)`, `UiDisplayText.ForWorkEventType(WorkEventType)`, `UiDisplayText.ForAuditArea(string)`, `UiDisplayText.ForAuditAction(string)`, `UiDisplayText.ForAuditOutcome(string)`, `UiDisplayText.ForAuditFields(string)`
- Produces: `JapanTimeFormatter.FormatUtc(DateTime)`, `JapanTimeFormatter.ToJapanDate(DateTime)`, `JapanTimeFormatter.ToJapanTime(DateTime)`, `JapanTimeFormatter.ToUtc(DateOnly, TimeOnly)`
- Preserves: UTCの保存値、英語のEnum名、英語の監査保存値

- [ ] **Step 1: 日本語表示と日本時間変換の失敗テストを書く**

```csharp
using FieldOps.Domain.Enums;
using FieldOps.Web.Formatting;

namespace FieldOps.IntegrationTests.Presentation;

public sealed class JapanesePresentationTests
{
    [Fact]
    public void DisplayTextMapsInternalValuesWithoutChangingTheirStoredNames()
    {
        Assert.Equal("支店管理者", UiDisplayText.ForRole("Branch Manager"));
        Assert.Equal("提案済み", UiDisplayText.ForSalesStatus(SalesOpportunityStatus.Proposed));
        Assert.Equal("作業中", UiDisplayText.ForWorkOrderStatus(WorkOrderStatus.InProgress));
        Assert.Equal("完了記録", UiDisplayText.ForWorkEventType(WorkEventType.Completion));
        Assert.Equal("作業予定", UiDisplayText.ForAuditArea("WorkOrder"));
        Assert.Equal("日程と担当者を設定", UiDisplayText.ForAuditAction("ScheduledAndAssigned"));
        Assert.Equal("成功", UiDisplayText.ForAuditOutcome("Success"));
    }

    [Fact]
    public void JapanTimeUsesFriendlyDisplayAndRoundTripsLocalInputToUtc()
    {
        DateTime utc = new(2026, 8, 27, 5, 30, 0, DateTimeKind.Utc);

        Assert.Equal("2026年8月27日 14:30", JapanTimeFormatter.FormatUtc(utc));
        Assert.Equal(new DateOnly(2026, 8, 27), JapanTimeFormatter.ToJapanDate(utc));
        Assert.Equal(new TimeOnly(14, 30), JapanTimeFormatter.ToJapanTime(utc));
        Assert.Equal(utc, JapanTimeFormatter.ToUtc(new DateOnly(2026, 8, 27), new TimeOnly(14, 30)));
    }
}
```

- [ ] **Step 2: 対象テストが未実装で失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~JapanesePresentationTests"`

Expected: `UiDisplayText`と新しい`JapanTimeFormatter`メソッドが存在せずコンパイル失敗。

- [ ] **Step 3: 表示専用の日本語変換を実装する**

`UiDisplayText`は全既知値をswitchで明示的に変換し、未知値は技術文字列をそのまま公開せず「未定義」とする。最低限、次を実装する。

```csharp
public static string ForWorkOrderStatus(WorkOrderStatus status) => status switch
{
    WorkOrderStatus.Planned => "未設定",
    WorkOrderStatus.Scheduled => "予定あり",
    WorkOrderStatus.InProgress => "作業中",
    WorkOrderStatus.Completed => "完了",
    WorkOrderStatus.Cancelled => "取り消し",
    _ => "未定義"
};

public static string ForSalesStatus(SalesOpportunityStatus status) => status switch
{
    SalesOpportunityStatus.New => "新規",
    SalesOpportunityStatus.Contacted => "連絡済み",
    SalesOpportunityStatus.SurveyScheduled => "現地確認予定",
    SalesOpportunityStatus.Quoting => "見積作成中",
    SalesOpportunityStatus.Proposed => "提案済み",
    SalesOpportunityStatus.Won => "受注",
    SalesOpportunityStatus.Lost => "失注",
    SalesOpportunityStatus.OnHold => "保留",
    _ => "未定義"
};
```

監査フィールドはカンマで分割し、`Status`→`状態`、`AssignedUserId`→`担当者`、`ScheduledStartUtc`→`予定日時`、`OwnerUserId`→`営業担当者`、`NextStatus`→`変更後の状態`、`Summary`→`内容`のように変換する。

- [ ] **Step 4: 日本時間の表示・入力変換を実装する**

```csharp
public static string FormatUtc(DateTime utcValue)
{
    if (utcValue.Kind != DateTimeKind.Utc)
    {
        throw new ArgumentException("The timestamp must be UTC.", nameof(utcValue));
    }

    DateTime japanValue = TimeZoneInfo.ConvertTimeFromUtc(utcValue, JapanTimeZone);
    return japanValue.ToString("yyyy年M月d日 H:mm", CultureInfo.GetCultureInfo("ja-JP"));
}

public static DateTime ToUtc(DateOnly date, TimeOnly time)
{
    DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
    return TimeZoneInfo.ConvertTimeToUtc(local, JapanTimeZone);
}
```

非UTCの`DateTime`は従来どおり`ArgumentException`とし、曖昧な変換を許可しない。

- [ ] **Step 5: リクエストカルチャとモデル入力エラーを日本語化する**

`Program.cs`で`ja-JP`を既定カルチャに設定し、`RequestLocalizationOptions`を登録する。MVCの`ModelBindingMessageProvider`には、数値・日付・必須値の入力不備を日本語で返すアクセサーを設定する。

```csharp
CultureInfo japaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(japaneseCulture);
    options.SupportedCultures = [japaneseCulture];
    options.SupportedUICultures = [japaneseCulture];
});
```

`app.UseRequestLocalization()`は転送ヘッダー処理の後、ルーティングの前へ置く。

- [ ] **Step 6: 対象テストとフォーマット確認を実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~JapanesePresentationTests"`

Expected: PASS。

Run: `dotnet format FieldOps.sln --verify-no-changes`

Expected: 終了コード0。

- [ ] **Step 7: Task 1をコミットする**

```powershell
git add src/FieldOps.Web/Formatting src/FieldOps.Web/Program.cs tests/FieldOps.IntegrationTests/Presentation
git commit -m "日本語表示と日本時間の共通契約を追加"
```

---

### Task 2: 日本語デモ利用者・ログイン・固定データ

**Files:**
- Modify: `src/FieldOps.Web/Models/DemoRoleCardViewModel.cs`
- Modify: `src/FieldOps.Web/Controllers/DemoLoginController.cs`
- Modify: `src/FieldOps.Web/Views/DemoLogin/Index.cshtml`
- Modify: `src/FieldOps.Infrastructure/Demo/DemoDataManifest.cs`
- Modify: `src/FieldOps.Infrastructure/Demo/DemoDataSeeder.cs`
- Modify: `tests/FieldOps.IntegrationTests/Authorization/DemoLoginTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Administration/DemoResetTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Authorization/AuthorizationPolicyTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Persistence/ModelMappingTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/DemoLoginPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`

**Interfaces:**
- Changes: `DemoRoleCardViewModel`へ`RoleLabel`を追加し、`Role`は認証トークン用の内部英語値として維持する。
- Preserves: 内部ロール名、ログイン用UserName、固定ID、データ件数、DatasetIdentifier、DatasetVersion、権限関係。
- Produces: 日本語の架空氏名、支店名、顧客名、現場名、連絡先、作業履歴。

- [ ] **Step 1: 日本語ログインと固定データの失敗テストを書く**

`DemoLoginTests.LoginPageOffersExactlyFourPublicRolesWithoutPasswordInput`を次の契約へ変更する。

```csharp
Assert.Contains("担当する仕事を選んでください", html, StringComparison.Ordinal);
Assert.Contains("システム管理者", html, StringComparison.Ordinal);
Assert.Contains("支店管理者", html, StringComparison.Ordinal);
Assert.Contains("営業担当者", html, StringComparison.Ordinal);
Assert.Contains("現場担当者", html, StringComparison.Ordinal);
Assert.Contains("架空のデモデータ", html, StringComparison.Ordinal);
Assert.DoesNotContain("Continue as", html, StringComparison.Ordinal);
Assert.DoesNotContain("type=\"password\"", html, StringComparison.OrdinalIgnoreCase);
```

`DemoResetTests`には、リセット後の中央支店名が`中央サービス支店`、利用者表示名が日本語、顧客名が`架空設備サービス 01`であることをDBから確認するアサーションを追加する。

- [ ] **Step 2: 変更前の画面とシードデータで失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DemoResetTests"`

Expected: 英語ログイン文言と英語シード名のためFAIL。

- [ ] **Step 3: ログインカードを内部ロールと日本語表示に分離する**

```csharp
public sealed record DemoRoleCardViewModel(
    string Role,
    string RoleLabel,
    string DisplayName,
    string Description,
    string LoginToken);
```

利用者表示名は次に固定する。

- System Administrator: `佐藤 健一`
- Branch Manager: `鈴木 美咲`
- Sales Representative: `高橋 翔太`
- Field Technician: `田中 葵`

説明は各役割で実行できることを一文で示し、ボタンは`{RoleLabel}として始める`とする。テスト用のトークン抽出は見出しの表示文言ではなく、フォームの`data-role="内部ロール名"`を使う。

- [ ] **Step 4: 固定データの表示名だけを日本語化する**

固定ID、件数、ロール、DatasetVersionは変えず、次の文字列を変更する。

- 支店: `中央サービス支店`、`現場サービス支店`、`北部サービス支店`、`南部サービス支店`、`西部サービス支店`
- 顧客: `架空設備サービス 01`から`架空設備サービス 40`
- 現場: `架空設備 現場 01`から`架空設備 現場 40`
- 連絡先: 姓`架空`、名`担当者01`から`担当者40`
- 作業履歴: `架空の作業記録 001`から`架空の作業記録 250`

データセットの意味、固定ID、件数、状態構成が同じため、承認済みDatasetVersionは`1`のまま維持する。

- [ ] **Step 5: テスト内の人向け架空名称を日本語へ合わせる**

対象テストで画面に出る名称を、`架空果樹園設備`、`架空中央支店 未処理作業`などの日本語へ変更する。メールアドレス、内部ロール名、監査Action、SQL列名は英語のまま維持する。

- [ ] **Step 6: ログイン・リセット・4役割テストを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DemoLoginTests|FullyQualifiedName~DemoResetTests|FullyQualifiedName~AuthorizationPolicyTests"`

Expected: PASS。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles" -- Playwright.BrowserName=chromium`

Expected: 日本語のログインボタンで4役割がログインできる。

- [ ] **Step 7: Task 2をコミットする**

```powershell
git add src/FieldOps.Web/Models/DemoRoleCardViewModel.cs src/FieldOps.Web/Controllers/DemoLoginController.cs src/FieldOps.Web/Views/DemoLogin/Index.cshtml src/FieldOps.Infrastructure/Demo tests/FieldOps.IntegrationTests tests/FieldOps.E2ETests
git commit -m "デモ利用者と固定データを日本語化"
```

---

### Task 3: 大きく見やすい共通レイアウトと安全な確認操作

**Files:**
- Create: `src/FieldOps.Web/Views/Shared/_ConfirmActionModal.cshtml`
- Modify: `src/FieldOps.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/FieldOps.Web/Views/Shared/_Layout.cshtml.css`
- Modify: `src/FieldOps.Web/wwwroot/css/site.css`
- Modify: `src/FieldOps.Web/wwwroot/js/site.js`
- Modify: `tests/FieldOps.E2ETests/Views/SharedLayoutTests.cs`
- Modify: `tests/FieldOps.E2ETests/Accessibility/AccessibilitySmokeTests.cs`

**Interfaces:**
- Produces: `[data-confirm-action]`、`data-confirm-title`、`data-confirm-message`を持つ送信ボタンに共通確認モーダルを適用する。
- Produces: `.page-intro`、`.task-card`、`.responsive-records`、`.details-disclosure`、`.app-empty-state`の共通スタイル。
- Preserves: 既存`data-nav`、`data-user-*`、`data-policy`属性。

- [ ] **Step 1: 日本語ランドマークと操作サイズの失敗テストを書く**

`SharedLayoutTests`へ次を追加する。

```csharp
await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "ja");
await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "主なメニュー" })).ToBeVisibleAsync();
await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く" })).ToBeVisibleAsync();
await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "終了する" })).ToBeVisibleAsync();
```

CSSの計算値はPlaywrightで主要ボタンの高さが48px以上、本文フォントが18px以上であることを確認する。

```csharp
float buttonHeight = await page.Locator(".btn-primary").First.EvaluateAsync<float>("el => el.getBoundingClientRect().height");
float bodyFont = await page.Locator("body").EvaluateAsync<float>("el => parseFloat(getComputedStyle(el).fontSize)");
Assert.True(buttonHeight >= 48);
Assert.True(bodyFont >= 18);
```

- [ ] **Step 2: 変更前のレイアウトで失敗することを確認する**

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SharedLayoutTests" -- Playwright.BrowserName=chromium`

Expected: `lang="en"`、英語ラベル、14〜16pxの文字、48px未満のボタンによりFAIL。

- [ ] **Step 3: 共通レイアウトを日本語化する**

- `<html lang="ja">`
- ブランド表示を`FieldOps 業務ポータル`
- `Navigation`を`メニュー`
- `Dashboard`を`ホーム`
- `Customers`を`顧客`
- `Business partners`を`協力会社`
- `Sales`を`営業案件`
- `Work orders`を`作業予定`
- `Work history`を`作業履歴`
- `Branch progress`を`支店状況`
- `Audit`を`変更履歴`
- `Logout`を`終了する`

ヘッダーのロールと支店は`UiDisplayText.ForRole`で表示し、内部値は`data-user-role`へ残す。

- [ ] **Step 4: 読みやすい共通スタイルを実装する**

```css
html { font-size: 18px; }
body { line-height: 1.65; color: #1f2937; background: #f5f7fa; }
.btn, .form-control, .form-select { min-height: 3rem; }
.nav-link { min-height: 3rem; display: flex; align-items: center; }
.page-intro { max-width: 46rem; color: #4b5563; }
.task-card { border: 2px solid transparent; border-radius: .75rem; }
.task-card:focus-within { border-color: #0b5ed7; }
```

Bootstrapの既存色を基準にし、状態は色と日本語ラベルの両方で示す。タブレット縦向きではサイドバーをオフキャンバスにし、本文幅を画面いっぱい使う。

- [ ] **Step 5: 共通確認モーダルを実装する**

`site.js`は対象フォームの送信を一度止め、モーダルへ対象名・変更内容・影響を表示する。最初の`submit`イベントで`event.submitter`を保存し、利用者が`実行する`を押した場合だけフォームへ一回限りの確認済みフラグを付けて`requestSubmit(savedSubmitter)`を呼ぶ。次の`submit`イベントはフラグを消して通すことで、ブラウザー標準検証と送信ボタンのname/valueを維持しながら無限ループを避ける。`やめる`でモーダルを閉じ、元のボタンへフォーカスを戻す。JavaScriptが無効な場合は通常のフォーム送信を妨げない。

- [ ] **Step 6: 共通レイアウトとアクセシビリティテストを実行する**

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SharedLayoutTests|FullyQualifiedName~AccessibilitySmokeTests" -- Playwright.BrowserName=chromium`

Expected: PASS、コンソールエラー0、フォーカス表示あり。

- [ ] **Step 7: Task 3をコミットする**

```powershell
git add src/FieldOps.Web/Views/Shared src/FieldOps.Web/wwwroot/css/site.css src/FieldOps.Web/wwwroot/js/site.js tests/FieldOps.E2ETests/Views tests/FieldOps.E2ETests/Accessibility
git commit -m "初心者向け共通レイアウトと確認操作を追加"
```

---

### Task 4: 役割別「今日やること」ホーム

**Files:**
- Create: `src/FieldOps.Web/Models/DashboardPageViewModel.cs`
- Create: `src/FieldOps.Web/Services/DashboardPageModelFactory.cs`
- Modify: `src/FieldOps.Web/Program.cs`
- Modify: `src/FieldOps.Web/Controllers/HomeController.cs`
- Modify: `src/FieldOps.Web/Views/Home/Index.cshtml`
- Modify: `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/DashboardPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`

**Interfaces:**
- Produces: `DashboardPageModelFactory.Create(DashboardMetrics metrics, string role, Guid? branchId)`
- Produces: `DashboardPageViewModel`、`DashboardActionCard`
- Consumes: Task 1の`UiDisplayText`と`JapanTimeFormatter`
- Preserves: `DashboardMetrics`の定義、集計クエリ、支店スコープ、既存`data-metric`属性。

- [ ] **Step 1: 役割別カード順序の失敗テストを書く**

```csharp
[Theory]
[InlineData("Branch Manager", "期限を過ぎた作業")]
[InlineData("Sales Representative", "期限が近い提案")]
[InlineData("Field Technician", "今日の作業")]
public void FactoryPutsTheRoleSpecificActionFirst(string role, string expectedTitle)
{
    DashboardMetrics metrics = new(5, 2, 3, 1, 4, 6, new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc));
    DashboardPageViewModel model = new DashboardPageModelFactory().Create(metrics, role, KnownBranchId);

    Assert.Equal(expectedTitle, model.Today.First().Title);
}
```

HTTP統合テストでは、各役割のホームHTMLに`今日やること`と日本語の推奨操作があり、他支店の名称が漏れないことを確認する。

- [ ] **Step 2: 表示モデル未実装で失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests"`

Expected: 新しい表示モデルと日本語見出しが存在せずFAIL。

- [ ] **Step 3: 役割別表示モデルを実装する**

```csharp
public sealed record DashboardActionCard(
    string Key,
    string Title,
    string Description,
    int Count,
    string TargetPath,
    bool RequiresAttention);

public sealed record DashboardPageViewModel(
    DashboardMetrics Metrics,
    string RoleLabel,
    IReadOnlyList<DashboardActionCard> Today,
    IReadOnlyList<DashboardActionCard> Review);
```

優先順位は仕様書の役割順に固定する。件数0のカードは`該当なし`と表示し、肯定的な空状態にする。リンクは既存一覧へ向け、Task 7で作業状態フィルターを追加した後に対象絞り込みへ接続する。

- [ ] **Step 4: ホームViewを行動中心に変更する**

画面構成を`挨拶`→`今日やること`→`確認が必要`→`詳しい集計を見る`の順にする。既存集計カードは`<details>`内に残し、`data-metric`と`data-value`は回帰テストのため維持する。

- [ ] **Step 5: 統合テストと4役割E2Eを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~DashboardTests"`

Expected: PASS。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~Roles" -- Playwright.BrowserName=chromium`

Expected: 4役割が役割別の先頭カードを確認でき、権限外情報は表示されない。

- [ ] **Step 6: Task 4をコミットする**

```powershell
git add src/FieldOps.Web/Models/DashboardPageViewModel.cs src/FieldOps.Web/Services/DashboardPageModelFactory.cs src/FieldOps.Web/Program.cs src/FieldOps.Web/Controllers/HomeController.cs src/FieldOps.Web/Views/Home/Index.cshtml tests/FieldOps.IntegrationTests/Features/DashboardTests.cs tests/FieldOps.E2ETests
git commit -m "役割別の今日やることホームを追加"
```

---

### Task 5: 顧客・協力会社をカード中心の日本語導線へ変更

**Files:**
- Modify: `src/FieldOps.Features/Parties/PartyDtos.cs`
- Modify: `src/FieldOps.Web/Controllers/PartiesController.cs`
- Modify: `src/FieldOps.Web/Views/Customers/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/BusinessPartners/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Parties/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Parties/Details.cshtml`
- Modify: `src/FieldOps.Web/Views/Parties/Create.cshtml`
- Modify: `src/FieldOps.Web/Views/Parties/Edit.cshtml`
- Modify: `tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/PartyPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`

**Interfaces:**
- Consumes: Task 1の`UiDisplayText.ForPartyRole`。
- Preserves: `Party`、`Customer`、`Business Partner`のドメイン上の意味、支店割当、共有処理、監査。
- Produces: PCでは簡潔な一覧、タブレットではカード表示、具体的な検索・登録・編集導線。

- [ ] **Step 1: 日本語の顧客導線を固定する失敗テストを書く**

`PartyFeatureTests`のHTMLアサーションを次へ変更する。

```csharp
Assert.Contains("顧客を探す", html);
Assert.Contains("顧客名・担当者名・現場名で検索", html);
Assert.Contains("顧客の情報を見る", html);
Assert.DoesNotContain("Parties", html);
Assert.DoesNotContain("Business partner", html);
```

作成エラーでは`組織名を入力してください`、役割未選択では`顧客または協力会社を選んでください`、同時更新では`ほかの利用者が先に更新しました`を確認する。

- [ ] **Step 2: 英語画面で失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"`

Expected: 日本語文言がなくFAIL。

- [ ] **Step 3: DataAnnotationsとControllerエラーを日本語化する**

`Organization name`→`組織名`、`Initial role`→`登録区分`、`Contact first name`→`担当者の名`、`Contact last name`→`担当者の姓`、`Site name`→`現場名`とする。例外の生メッセージをそのまま表示せず、既知の失敗を日本語へ変換してModelStateへ追加する。

- [ ] **Step 4: 一覧・詳細を初心者向けに再構成する**

- 一覧上部へ`この画面では顧客を探し、詳しい情報を確認できます。`を表示する。
- 検索ボタンを`この条件で探す`とする。
- PC表は主要4列以内、タブレットはカード表示にする。
- 詳細の主要ボタンを`顧客情報を変更する`とする。
- 連絡先、現場、担当支店は見出しと空状態を日本語化する。
- ページ送りに`前へ`、`次へ`、`全N件`を追加する。

- [ ] **Step 5: 入力画面へ説明・必須・戻る導線を追加する**

必須項目に`必須`バッジを付け、送信ボタンを`この内容で顧客を登録する`または`変更内容を保存する`とする。`キャンセル`は`前の画面へ戻る`とし、送信を起こさない通常リンクにする。

- [ ] **Step 6: 対象統合テストと役割E2Eを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~PartyFeatureTests"`

Expected: PASS。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~BranchManagerTests|FullyQualifiedName~SalesRepresentativeTests" -- Playwright.BrowserName=chromium`

Expected: 役割ごとの顧客操作と支店分離がPASS。

- [ ] **Step 7: Task 5をコミットする**

```powershell
git add src/FieldOps.Features/Parties/PartyDtos.cs src/FieldOps.Web/Controllers/PartiesController.cs src/FieldOps.Web/Views/Customers src/FieldOps.Web/Views/BusinessPartners src/FieldOps.Web/Views/Parties tests/FieldOps.IntegrationTests/Features/PartyFeatureTests.cs tests/FieldOps.E2ETests
git commit -m "顧客と協力会社のかんたん日本語導線を追加"
```

---

### Task 6: 営業案件を期限と次の行動中心へ変更

**Files:**
- Modify: `src/FieldOps.Features/Sales/SalesDtos.cs`
- Modify: `src/FieldOps.Web/Controllers/SalesController.cs`
- Modify: `src/FieldOps.Web/Views/Sales/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Sales/Details.cshtml`
- Modify: `src/FieldOps.Web/Views/Sales/Edit.cshtml`
- Modify: `tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/SalesPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SalesRepresentativeTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`

**Interfaces:**
- Consumes: Task 1の営業状態・監査表示変換と日本語日付表示。
- Preserves: 営業案件状態遷移、金額、期限、担当者、受注から作業予定作成への権限。
- Produces: 状態名と状態変更ボタンの日本語、期限優先カード、簡易絞り込み。

- [ ] **Step 1: 営業案件の日本語状態・金額・期限テストを書く**

```csharp
Assert.Contains("営業案件", html);
Assert.Contains("提案済み", html);
Assert.Contains("2026年9月1日", html);
Assert.Contains("￥125,000", html);
Assert.Contains("この案件を受注にする", detailsHtml);
Assert.DoesNotContain("Move to Won", detailsHtml);
```

競合時は`ほかの利用者が先に更新しました。最新の内容を確認してください。`を確認する。

- [ ] **Step 2: 現行英語表示で失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~SalesFeatureTests"`

Expected: 英語状態と英語ボタンのためFAIL。

- [ ] **Step 3: DataAnnotations・Controllerメッセージを日本語化する**

`Party`→`顧客`、`Site`→`現場`、`Sales owner`→`営業担当者`、`Proposed amount`→`提案金額`、`Expected close date`→`予定日`とする。金額と予定日は両方入力する契約を`提案金額と予定日は両方入力してください。`と表示する。

- [ ] **Step 4: 一覧・詳細・入力を行動中心に変更する**

- 一覧の初期表示は状態、期限、顧客、担当者、次の操作を優先する。
- 詳細の状態変更は`この案件を{日本語状態}にする`とする。
- 受注済み案件の作業予定作成は`この受注から作業予定を作る`とする。
- 監査は`詳しい変更履歴を見る`内へ収納する。
- 取消に相当する失注・保留は共通確認モーダルを通す。

- [ ] **Step 5: 対象テストを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~SalesFeatureTests"`

Expected: PASS。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SalesRepresentativeTests|FullyQualifiedName~BranchManagerTests" -- Playwright.BrowserName=chromium`

Expected: 営業担当者と支店管理者の代表操作がPASS。

- [ ] **Step 6: Task 6をコミットする**

```powershell
git add src/FieldOps.Features/Sales/SalesDtos.cs src/FieldOps.Web/Controllers/SalesController.cs src/FieldOps.Web/Views/Sales tests/FieldOps.IntegrationTests/Features/SalesFeatureTests.cs tests/FieldOps.E2ETests
git commit -m "営業案件を期限と次の行動中心に改善"
```

---

### Task 7: 作業予定の日本時間入力・状態フィルター・安全な状態変更

**Files:**
- Create: `src/FieldOps.Web/Models/WorkOrderScheduleForm.cs`
- Create: `src/FieldOps.Web/Models/WorkEventForm.cs`
- Modify: `src/FieldOps.Features/Work/WorkOrderDtos.cs`
- Modify: `src/FieldOps.Features/Work/WorkOrderQueries.cs`
- Modify: `src/FieldOps.Web/Controllers/WorkOrdersController.cs`
- Modify: `src/FieldOps.Web/Views/WorkOrders/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/WorkOrders/Details.cshtml`
- Modify: `src/FieldOps.Web/Views/WorkOrders/Edit.cshtml`
- Modify: `src/FieldOps.Web/Views/WorkOrders/AddEvent.cshtml`
- Modify: `tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/WorkOrderPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/FieldTechnicianTests.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/BranchManagerTests.cs`

**Interfaces:**
- Produces: `WorkOrderScheduleForm.ToCommand()`が既存`WorkOrderEditInput`を返す。
- Produces: `WorkEventForm.ToCommand()`が既存`WorkEventInput`を返す。
- Changes: `WorkOrderSearchRequest`へ`WorkOrderStatus? Status`と`bool Overdue`を追加する。
- Consumes: Task 1の日本時間変換、状態表示、Task 3の確認モーダル。
- Preserves: WorkOrderCommands、ドメイン状態遷移、監査、同時更新、担当者スコープ。

- [ ] **Step 1: 日本時間フォームとUTC保存の失敗テストを書く**

HTTPフォームを次の入力名へ変更する。

```csharp
new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["Id"] = workOrderId.ToString(),
    ["Version"] = version,
    ["AssignedUserId"] = seed.CentralTechnicianUserId,
    ["ScheduledDate"] = "2026-09-20",
    ["ScheduledTime"] = "10:30",
    ["__RequestVerificationToken"] = token
});
```

DBアサーションは従来どおり`2026-09-20 01:30:00Z`を期待し、画面HTMLに`UTC`、`ISO 8601`、末尾`Z`入力がないことを確認する。

作業記録は`OccurredDate=2026-08-11`、`OccurredTime=12:15`を送信し、`2026-08-11 03:15:00Z`が保存されることを確認する。

- [ ] **Step 2: 現行UTC入力フォームで失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkOrderFeatureTests"`

Expected: 新しい日付・時刻フィールドがなくFAIL。

- [ ] **Step 3: Web専用フォームモデルを実装する**

```csharp
public sealed class WorkOrderScheduleForm
{
    public Guid Id { get; set; }
    public uint Version { get; set; }
    public WorkOrderStatus Status { get; set; }

    [Required(ErrorMessage = "担当者を選んでください。")]
    public string AssignedUserId { get; set; } = string.Empty;

    [Required(ErrorMessage = "作業日を選んでください。")]
    public DateOnly? ScheduledDate { get; set; }

    [Required(ErrorMessage = "開始時刻を選んでください。")]
    public TimeOnly? ScheduledTime { get; set; }

    public WorkOrderEditInput ToCommand() => new()
    {
        Id = Id,
        Version = Version,
        Status = Status,
        AssignedUserId = AssignedUserId,
        ScheduledStartUtc = ScheduledDate.HasValue && ScheduledTime.HasValue
            ? JapanTimeFormatter.ToUtc(ScheduledDate.Value, ScheduledTime.Value)
            : null
    };
}
```

`WorkEventForm`も同じ境界で`OccurredDate`と`OccurredTime`をUTCへ変換する。

```csharp
public sealed class WorkEventForm
{
    public uint Version { get; set; }
    public WorkEventType EventType { get; set; }

    [Required(ErrorMessage = "作業内容を入力してください。")]
    [StringLength(2000, ErrorMessage = "作業内容は2000文字以内で入力してください。")]
    public string Summary { get; set; } = string.Empty;

    [Required(ErrorMessage = "記録日を選んでください。")]
    public DateOnly? OccurredDate { get; set; }

    [Required(ErrorMessage = "記録時刻を選んでください。")]
    public TimeOnly? OccurredTime { get; set; }

    public WorkEventInput ToCommand() => new()
    {
        Version = Version,
        EventType = EventType,
        Summary = Summary,
        OccurredAtUtc = OccurredDate.HasValue && OccurredTime.HasValue
            ? JapanTimeFormatter.ToUtc(OccurredDate.Value, OccurredTime.Value)
            : null
    };
}
```

GET時は既存UTC値を日本時間へ分割して表示する。

- [ ] **Step 4: Controllerをフォームモデルと既存Commandの変換境界にする**

GETはFeature DTOからWebフォームへ変換し、POSTはWebフォームを検証後に`ToCommand()`で既存Commandへ渡す。同時更新時は最新のVersionと保存値を再読込し、利用者が入力した日本語フォーム値を保持する。

- [ ] **Step 5: 作業一覧へ状態・遅延フィルターを追加する**

`WorkOrderQueries.SearchAsync`へ次を追加する。

```csharp
if (request.Status.HasValue)
{
    query = query.Where(workOrder => workOrder.Status == request.Status.Value);
}

if (request.Overdue)
{
    DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
    query = query.Where(workOrder =>
        (workOrder.Status == WorkOrderStatus.Scheduled || workOrder.Status == WorkOrderStatus.InProgress) &&
        workOrder.ScheduledStartUtc < utcNow);
}
```

`WorkOrderQueries`へ`TimeProvider`を注入し、Task 4のホームカードを`status`または`overdue=true`付きURLへ接続する。

- [ ] **Step 6: 一覧・詳細・状態変更を日本語化する**

- 一覧はタブレットでカード表示する。
- 詳細の主要操作を`日程と担当者を決める`、`作業記録を追加する`とする。
- 状態変更を`作業を開始する`、`作業を完了する`、`作業予定を取り消す`とする。
- 取消と完了は対象・変更結果を示す共通確認モーダルを通す。
- 詳しい内部状態、UTC日時、履歴の技術値は`詳細を見る`へ収納する。

- [ ] **Step 7: 対象統合テストと役割E2Eを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkOrderFeatureTests"`

Expected: UTC保存、状態フィルター、競合、権限、監査がすべてPASS。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~FieldTechnicianTests|FullyQualifiedName~BranchManagerTests" -- Playwright.BrowserName=chromium`

Expected: 日本時間入力で日程設定と作業完了がPASS。

- [ ] **Step 8: Task 7をコミットする**

```powershell
git add src/FieldOps.Web/Models/WorkOrderScheduleForm.cs src/FieldOps.Web/Models/WorkEventForm.cs src/FieldOps.Features/Work/WorkOrderDtos.cs src/FieldOps.Features/Work/WorkOrderQueries.cs src/FieldOps.Web/Controllers/WorkOrdersController.cs src/FieldOps.Web/Views/WorkOrders tests/FieldOps.IntegrationTests/Features/WorkOrderFeatureTests.cs tests/FieldOps.E2ETests
git commit -m "作業予定を日本時間と安全な操作へ改善"
```

---

### Task 8: 作業履歴・支店状況・変更履歴・エラー画面の日本語化

**Files:**
- Create: `src/FieldOps.Web/Controllers/StatusController.cs`
- Create: `src/FieldOps.Web/Services/SafeHtmlErrorResponse.cs`
- Create: `src/FieldOps.Web/Views/Status/Index.cshtml`
- Modify: `src/FieldOps.Web/Models/WorkHistorySearchViewModel.cs`
- Modify: `src/FieldOps.Web/Controllers/HomeController.cs`
- Modify: `src/FieldOps.Web/Program.cs`
- Modify: `src/FieldOps.Web/Views/WorkHistory/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Branches/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Branches/Details.cshtml`
- Modify: `src/FieldOps.Web/Views/Audit/Index.cshtml`
- Modify: `src/FieldOps.Web/Views/Administration/Reset.cshtml`
- Modify: `src/FieldOps.Web/Views/Shared/Error.cshtml`
- Modify: `tests/FieldOps.IntegrationTests/Features/WorkHistorySearchTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Features/DashboardTests.cs`
- Modify: `tests/FieldOps.IntegrationTests/Failures/FailurePathTests.cs`
- Modify: `tests/FieldOps.E2ETests/Pages/ResetPage.cs`
- Modify: `tests/FieldOps.E2ETests/Roles/SystemAdministratorTests.cs`

**Interfaces:**
- Consumes: Task 1の表示変換、Task 3のレイアウト、Task 7の日本時間表示。
- Produces: `/status/{code:int}`の日本語403・404表示。
- Produces: `SafeHtmlErrorResponse.WriteAsync(HttpContext, int, string)`による機密情報を含まない日本語500 HTML。
- Preserves: 監査保存値、変更フィールド許可リスト、例外分類、相関ID、デモ初期化の二重実行防止。

- [ ] **Step 1: 二次画面とエラー回復の失敗テストを書く**

```csharp
Assert.Contains("作業履歴", historyHtml);
Assert.Contains("予定日", historyHtml);
Assert.Contains("完了日", historyHtml);
Assert.DoesNotContain("UTC", historyHtml);

Assert.Contains("支店状況", branchHtml);
Assert.Contains("変更履歴", auditHtml);
Assert.Contains("変更した項目", auditHtml);

Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
Assert.Contains("この操作を行う権限がありません", await forbidden.Content.ReadAsStringAsync());
Assert.Contains("前の画面へ戻る", await forbidden.Content.ReadAsStringAsync());
```

- [ ] **Step 2: 現行英語表示または空レスポンスで失敗することを確認する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~FailurePathTests"`

Expected: 英語表示と空の403本文によりFAIL。

- [ ] **Step 3: 作業履歴と支店状況をカード中心に変更する**

作業履歴の詳細条件は`条件を追加する`内へ収納し、初期表示は期間・状態・キーワードだけにする。支店比較はタブレットでカード表示し、遅延件数を先に示す。日時はすべて`JapanTimeFormatter`で表示する。

- [ ] **Step 4: 変更履歴の保存値を表示時だけ日本語化する**

AggregateType、Action、Outcome、ChangedFieldsは`UiDisplayText`で変換する。`data-audit-action`には既存の英語Actionを残し、監査クエリとテスト契約を維持する。相関ID、ActorDisplayName、支店スコープは変更しない。

- [ ] **Step 5: 403・404・500の日本語回復画面を実装する**

`StatusController.Index(int code)`は403と404だけを受け付け、対応する日本語見出し、説明、戻り先をViewへ渡す。`Program.cs`に`UseStatusCodePagesWithReExecute("/status/{0}")`を追加する。

例外処理は既存`SafeExceptionClassifier`と相関IDを維持する。`Accept`に`text/html`を含む要求では`SafeHtmlErrorResponse.WriteAsync`を呼び、文書言語`ja`、見出し`処理を完了できませんでした`、説明、ホームへのリンク、HTMLエンコード済み相関IDだけを返す。JSON要求では従来どおり`{ correlationId }`を返す。

```csharp
public static async Task WriteAsync(HttpContext context, int statusCode, string correlationId)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "text/html; charset=utf-8";
    string safeId = HtmlEncoder.Default.Encode(correlationId);
    await context.Response.WriteAsync($"""
        <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>エラー - FieldOps 業務ポータル</title><link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"><link rel="stylesheet" href="/css/site.css"></head><body><main class="container py-5"><h1>処理を完了できませんでした</h1><p>時間をおいて、もう一度お試しください。</p><p>お問い合わせ番号: <code>{safeId}</code></p><a class="btn btn-primary" href="/">ホームへ戻る</a></main></body></html>
        """);
}
```

FailurePathTestsで元のステータスコード、相関IDの存在、例外メッセージ・スタックトレース・機密値の非表示を固定する。

- [ ] **Step 6: デモ初期化画面の技術入力を補助説明付きで維持する**

安全契約上の`RESET`入力は変更しない。見出しを`デモデータを初期状態に戻す`とし、何が消えて何が復元されるかを日本語で説明する。実行ボタンは共通確認モーダルを通し、処理中・成功・失敗・相関ID表示を維持する。

- [ ] **Step 7: 対象テストを実行する**

Run: `dotnet test tests/FieldOps.IntegrationTests --filter "FullyQualifiedName~WorkHistorySearchTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~FailurePathTests"`

Expected: PASS、機密値の漏えいなし。

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~SystemAdministratorTests|FullyQualifiedName~ResetPage" -- Playwright.BrowserName=chromium`

Expected: 変更履歴と初期化の代表操作がPASS。

- [ ] **Step 8: Task 8をコミットする**

```powershell
git add src/FieldOps.Web/Controllers src/FieldOps.Web/Models/WorkHistorySearchViewModel.cs src/FieldOps.Web/Program.cs src/FieldOps.Web/Views/WorkHistory src/FieldOps.Web/Views/Branches src/FieldOps.Web/Views/Audit src/FieldOps.Web/Views/Administration src/FieldOps.Web/Views/Shared/Error.cshtml src/FieldOps.Web/Views/Status tests/FieldOps.IntegrationTests tests/FieldOps.E2ETests
git commit -m "履歴とエラー回復画面を日本語化"
```

---

### Task 9: PC・タブレット・200%拡大・キーボードの画面契約

**Files:**
- Create: `tests/FieldOps.E2ETests/Accessibility/ResponsiveUsabilityTests.cs`
- Modify: `tests/FieldOps.E2ETests/Accessibility/AccessibilitySmokeTests.cs`
- Modify: `tests/FieldOps.E2ETests/Infrastructure/FieldOpsWebFixture.cs`
- Modify: `src/FieldOps.Web/wwwroot/css/site.css`
- Modify: `src/FieldOps.Web/wwwroot/js/site.js`

**Interfaces:**
- Produces: PC `1440x900`、タブレット横 `1024x768`、タブレット縦 `768x1024`の検証ヘルパー。
- Produces: 200%拡大相当で横方向の主要コンテンツ欠落がない検証。
- Preserves: 既存の4役割E2Eとアクセシビリティスモーク。

- [ ] **Step 1: レスポンシブ・拡大・キーボードの失敗テストを書く**

```csharp
[Theory]
[InlineData(1440, 900)]
[InlineData(1024, 768)]
[InlineData(768, 1024)]
public async Task PrimaryJourneysRemainUsableAtSupportedViewports(int width, int height)
{
    await page.SetViewportSizeAsync(width, height);
    await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.FieldTechnician);
    await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "今日やること" })).ToBeVisibleAsync();
    Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"));
}
```

キーボードテストでは`Tab`でメニュー、主要カード、戻る、送信へ順に移動し、フォーカスリングの`outlineStyle`が`none`ではないことを確認する。200%拡大はCDPのズームに依存せず、CSSピクセル幅を半分にした等価リフローで確認する。

- [ ] **Step 2: 変更前または未調整箇所で失敗することを確認する**

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~ResponsiveUsabilityTests|FullyQualifiedName~AccessibilitySmokeTests" -- Playwright.BrowserName=chromium`

Expected: 横長表、操作サイズ、フォーカス順序のいずれかでFAIL。

- [ ] **Step 3: 画面ごとのオーバーフローとフォーカスを修正する**

- 768px以下では一覧表をカードへ切り替える。
- 長い日本語名に`overflow-wrap:anywhere`を適用する。
- 固定幅、`white-space:nowrap`、画面外ボタンを除去する。
- オフキャンバスメニューを閉じた後はメニューボタンへフォーカスを戻す。
- モーダルを閉じた後は元の操作ボタンへフォーカスを戻す。
- `prefers-reduced-motion`で不要なトランジションを止める。

- [ ] **Step 4: レスポンシブとアクセシビリティテストを再実行する**

Run: `dotnet test tests/FieldOps.E2ETests --filter "FullyQualifiedName~ResponsiveUsabilityTests|FullyQualifiedName~AccessibilitySmokeTests" -- Playwright.BrowserName=chromium`

Expected: 3画面幅、等価200%リフロー、キーボード操作がPASS。

- [ ] **Step 5: Task 9をコミットする**

```powershell
git add tests/FieldOps.E2ETests/Accessibility tests/FieldOps.E2ETests/Infrastructure/FieldOpsWebFixture.cs src/FieldOps.Web/wwwroot/css/site.css src/FieldOps.Web/wwwroot/js/site.js
git commit -m "PCとタブレットの操作性を検証"
```

---

### Task 10: 全文言監査・全テスト・README・公開検証

**Files:**
- Modify: `scripts/wait-for-ready.ps1`
- Modify: `scripts/test-public-smoke.ps1`
- Modify: `README.md`
- Modify: `docs/evidence/public-deployment-verification.md`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Shared/_Layout.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Shared/Error.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/DemoLogin/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Home/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Home/Privacy.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Customers/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/BusinessPartners/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Parties/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Parties/Details.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Parties/Create.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Parties/Edit.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Sales/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Sales/Details.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Sales/Edit.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/WorkOrders/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/WorkOrders/Details.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/WorkOrders/Edit.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/WorkOrders/AddEvent.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/WorkHistory/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Branches/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Branches/Details.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Audit/Index.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Administration/Reset.cshtml`
- Audit and modify when a human-facing English string remains: `src/FieldOps.Web/Views/Status/Index.cshtml`
- Audit and modify when a human-facing English validation message remains: `src/FieldOps.Web/Controllers/AdministrationController.cs`
- Audit and modify when a human-facing English validation message remains: `src/FieldOps.Web/Controllers/PartiesController.cs`
- Audit and modify when a human-facing English validation message remains: `src/FieldOps.Web/Controllers/SalesController.cs`
- Audit and modify when a human-facing English validation message remains: `src/FieldOps.Web/Controllers/WorkOrdersController.cs`

**Interfaces:**
- Consumes: Tasks 1〜9の日本語画面契約。
- Produces: 4役割の公開読み取りスモーク、公開画面の日本語確認、最新デプロイ証跡。
- Preserves: `/health/live`、`/health/ready`、HTTPS要件、認証Cookie安全属性、Render Free構成。

- [ ] **Step 1: 人向け英語残存を検出する監査を実行する**

Run:

```powershell
rg -n --glob '*.cshtml' --glob '*.cs' '(Dashboard|Customers|Business partners|Sales|Work orders|Work history|Branch progress|Audit|Logout|Continue as|Scheduled start \(UTC\)|ISO 8601|Available actions|No .* available|This .* changed)' src/FieldOps.Web src/FieldOps.Features
```

Expected: ログ、内部定数、認可ロール、監査保存値を除き、画面に到達する英語文言が0件。検出された人向け文言は対象ViewまたはControllerで日本語へ変更し、対応するテストアサーションを追加する。

- [ ] **Step 2: 公開スモークを日本語契約へ更新する**

`wait-for-ready.ps1`は`<title>担当する仕事を選んでください - FieldOps 業務ポータル</title>`を確認する。`test-public-smoke.ps1`は`form[data-role="内部ロール値"]`でフォームを選び、日本語のホーム見出し`今日やること`と`作業予定`を確認する。

- [ ] **Step 3: READMEを非エンジニアにも読める日本語へ更新する**

冒頭に次を追加する。

- 公開デモURL
- このアプリでできること
- 4役割の違い
- 架空データであること
- PC・タブレット対応
- 開発者向けの起動・テスト・デプロイ情報への目次

コマンド、環境変数、クラス名、APIパスは英語のまま維持する。

- [ ] **Step 4: フォーマット・ビルド・全テストを実行する**

Run: `dotnet restore FieldOps.sln`

Run: `dotnet format FieldOps.sln --verify-no-changes --no-restore`

Run: `dotnet build FieldOps.sln --configuration Release --no-restore`

Run: `dotnet test tests/FieldOps.Domain.Tests --configuration Release --no-build`

Run: `dotnet test tests/FieldOps.IntegrationTests --configuration Release --no-build`

Run: `dotnet test tests/FieldOps.E2ETests --configuration Release --no-build -- Playwright.BrowserName=chromium`

Expected: 全コマンド終了コード0、E2E再試行0、ブラウザコンソールエラー0。

- [ ] **Step 5: コンテナで日本語スモークを実行する**

Run:

```powershell
docker compose up --build --wait
./scripts/wait-for-ready.ps1
./scripts/test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'System Administrator'
./scripts/test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Branch Manager'
./scripts/test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Sales Representative'
./scripts/test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Field Technician'
docker compose down --volumes --remove-orphans
```

Expected: 4役割すべてPASS。失敗時は`docker compose logs --no-color`を保存して修正後に再実行する。

- [ ] **Step 6: 最終ローカル変更をコミットする**

```powershell
git add scripts README.md docs/evidence/public-deployment-verification.md src/FieldOps.Web tests
git commit -m "日本語かんたんUIの公開検証を追加"
```

- [ ] **Step 7: GitHubへプッシュしCIを確認する**

Run: `git push origin HEAD:main`

Run: `gh run list --workflow CI --limit 1`

Expected: 最新mainコミットのCIが`completed success`。

- [ ] **Step 8: Render Freeへ最新mainを反映する**

Renderの自動デプロイが最新mainを取得したことを確認する。自動デプロイが開始しない場合は既存サービス`fieldops-portfolio`で最新コミットの手動デプロイを実行する。料金プラン、課金情報、サービス名、公開URLは変更しない。

- [ ] **Step 9: 公開デモデータ初期化の直前確認を取る**

対象を`https://fieldops-portfolio.onrender.com`の架空デモデータに限定し、既存の可変デモデータを日本語固定データへ置き換えること、復旧方法が同じデモ初期化の再実行であることをユーザーへ提示する。明示確認を受けるまで初期化POSTは実行しない。

- [ ] **Step 10: 公開デモを日本語固定データへ初期化する**

承認後、システム管理者で既存のデモ初期化画面を開き、確認文字列`RESET`と既存の一回限りトークンを使って1回だけ実行する。成功表示と相関IDを確認し、二重送信しない。

- [ ] **Step 11: 公開4役割スモークと実ブラウザ検証を実行する**

Run:

```powershell
./scripts/wait-for-ready.ps1 -BaseUrl https://fieldops-portfolio.onrender.com -TimeoutSeconds 600
./scripts/test-public-smoke.ps1 -BaseUrl https://fieldops-portfolio.onrender.com -Role 'System Administrator'
./scripts/test-public-smoke.ps1 -BaseUrl https://fieldops-portfolio.onrender.com -Role 'Branch Manager'
./scripts/test-public-smoke.ps1 -BaseUrl https://fieldops-portfolio.onrender.com -Role 'Sales Representative'
./scripts/test-public-smoke.ps1 -BaseUrl https://fieldops-portfolio.onrender.com -Role 'Field Technician'
```

実ブラウザでは、ログイン、役割別ホーム、顧客一覧、営業案件、作業予定、作業詳細、日本時間入力、変更履歴をPC幅とタブレット幅で確認し、各画面のスクリーンショットを保存する。

- [ ] **Step 12: 公開証跡を追記して最終コミット・プッシュする**

`docs/evidence/public-deployment-verification.md`へ、公開URL、デプロイコミット、CI Run、Release verification Run、4役割スモーク、PC・タブレット確認、日本語固定データ確認、実施日を追記する。

```powershell
git add docs/evidence/public-deployment-verification.md
git commit -m "日本語UIの公開検証結果を記録"
git push origin HEAD:main
```

Expected: 作業ツリーがクリーンで、GitHub mainとRenderのデプロイコミットが一致する。

---

## Final Acceptance Checklist

- [ ] 4役割のログインから代表業務まで日本語で完了できる。
- [ ] 通常画面にUTC・ISO 8601の直接入力がない。
- [ ] PC・タブレット横・タブレット縦・等価200%拡大で主要操作が見える。
- [ ] キーボードのみで主要導線を操作できる。
- [ ] 主要ボタン48px以上、本文18px以上、補助文16px以上。
- [ ] 状態を色だけで表していない。
- [ ] 取消・完了・初期化に確認段階がある。
- [ ] 認可、支店分離、状態遷移、監査、固定ID、DBスキーマが維持されている。
- [ ] Domain、Integration、E2E、container smoke、public smokeがすべて成功する。
- [ ] GitHub main、CI成功コミット、Render公開コミットが一致する。
- [ ] 公開URLで日本語固定データと日本語UIを実ブラウザ確認済み。
