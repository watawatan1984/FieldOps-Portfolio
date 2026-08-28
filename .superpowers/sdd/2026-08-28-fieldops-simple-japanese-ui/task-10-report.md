# Task 10A ローカル監査・README・全検証レポート

## Status

complete

ローカル監査、README更新、public smoke scripts更新、deferred minor全件のtriage/解消、Release build後の順次E2E、Docker compose上の4役割local smoke、ローカルコミット準備まで完了。GitHub push、CI、Render deploy、公開デモ初期化、公開環境操作は実行していない。

## 変更

- README冒頭を非エンジニア向け日本語に更新し、公開URL、できること、4役割、架空データ、PC/タブレット対象、開発者向け目次を追加した。
- `scripts/check-readme.ps1` を日本語README契約へ更新し、古い `## English summary` 必須条件を `## 日本語概要` と冒頭説明の確認へ置き換えた。
- `scripts/wait-for-ready.ps1` と `scripts/test-public-smoke.ps1` を現在の日本語UI契約へ更新した。レスポンス本文はUTF-8で読み、HTML decode後に日本語タイトル・見出しを検証する。
- public smokeは4役割すべてで読み取りのみを維持した。ログインPOST後は `/` と `/work-orders` のGET確認だけを行う。
- Demo Login画面タイトルを `担当する仕事を選んでください` に更新した。
- Parties全件画面の顧客専用文言を、顧客/協力会社に中立な表現へ変更した。
- Sales入力DTOのRequired/Range DataAnnotationsへ日本語メッセージを設定した。
- WorkOrder詳細履歴を新しい順に変更した。
- Task8/9由来のformat gateを `dotnet format` で解消した。

## 英語監査分類

- 人向けUI/README/スモーク契約: 日本語化対象として修正した。
- 内部enum/role値/API/route/class/env var/data属性: 既存契約維持のため英語を残した。例: `System Administrator`, `Branch Manager`, `Sales Representative`, `Field Technician`, `SalesOpportunity`, `CustomerId`, `data-role`。
- 監査・DB・テストfixture・サードパーティライブラリ内文字列: 内部または検証用のため対象外。jQuery validation配布ファイルの英語は外部ライブラリとして維持した。
- 既存証跡ドキュメント `docs/evidence/public-deployment-verification.md`: 過去の公開検証証跡として保持し、今回のローカル専任タスクでは公開環境操作を伴う更新は行っていない。

## deferred minor処理結果

1. 確認モーダル前にnative required validationが効くことの直接E2E: 解消。`SharedLayoutTests` でrequired入力が空のとき確認モーダルが表示されず送信もされないことを確認し、入力後のみ確認モーダルが開く回帰を追加した。
2. Parties全件画面の顧客専用文言を顧客/協力会社に中立化: 解消。画面文言とIntegration assertionを更新した。
3. Sales DataAnnotations required/range日本語メッセージの直接Integration coverage: 解消。`SalesEditInputUsesJapaneseDataAnnotationMessages` を追加し、Required/Rangeの日本語メッセージを直接検証した。
4. WorkOrder詳細履歴を新しい順へ変更し回帰テスト: 解消。Queryを降順化し、詳細画面で新しい作業記録が古い作業記録より先に表示されるIntegration testを追加した。既存の追記専用履歴テストも新しい表示順へ合わせた。
5. Task8/9を含むformat gateを解消: 解消。`dotnet format FieldOps.sln --verify-no-changes --no-restore` がPASSした。

## 検証

### README

- `.\scripts\check-readme.ps1` → PASS。`README content checks passed (20 requirements).`

### Format / Build

- `dotnet restore FieldOps.sln` → PASS。すべて最新。
- `dotnet format FieldOps.sln --verify-no-changes --no-restore` → PASS。
- `dotnet build FieldOps.sln --configuration Release --no-restore` → PASS。0 warnings, 0 errors。

### Domain / Integration / E2E

- `dotnet test tests\FieldOps.Domain.Tests --configuration Release --no-build` → PASS。62 passed, 0 failed, 0 skipped。
- `dotnet test tests\FieldOps.IntegrationTests --configuration Release --no-build` → PASS。205 passed, 0 failed, 0 skipped。
- `dotnet test tests\FieldOps.E2ETests --configuration Release --no-build -- Playwright.BrowserName=chromium` → PASS。27 passed, 0 failed, 0 skipped。

E2EはRelease build後に単独コマンドで順番に実行した。失敗・再試行出力はなくretry 0。`FieldOpsWebFixture` の `BrowserErrorCollector` がconsole error/page errorを収集し、各E2E後に `AssertEmpty()` するため、27件PASSによりconsole error 0。

### Container smoke

Docker compose起動後、次の順番で4役割のlocal smokeを実行し、すべてPASSした。

- `.\scripts\wait-for-ready.ps1` → `FieldOps is ready at http://127.0.0.1:8080`
- `.\scripts\test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'System Administrator'` → PASS
- `.\scripts\test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Branch Manager'` → PASS
- `.\scripts\test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Sales Representative'` → PASS
- `.\scripts\test-public-smoke.ps1 -BaseUrl http://127.0.0.1:8080 -AllowLocalHttp -Role 'Field Technician'` → PASS

成功後、`docker compose down --volumes --remove-orphans` を実行し、web/dbコンテナ、ネットワーク、volumeが削除された。

## 懸念

- `.superpowers/` は `.gitignore` 対象のため、このレポートは作業ツリー上の指定パスへ保存したが、通常のgit addではコミット対象にならない。
- Docker smokeの初回は既存volume由来のDB重複制約で失敗し、以降はcontainer downでvolumeを削除して再実行した。最終runはclean volumeで4役割すべてPASSした。
- `docs/evidence/public-deployment-verification.md` は公開環境の過去証跡であり、今回のローカル専任範囲では更新していない。
