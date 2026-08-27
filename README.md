# FieldOps Portal

複数拠点を持つサービス会社の顧客・営業・現場作業を題材にした、C# / ASP.NET Core MVC のポートフォリオアプリケーションです。

> [!IMPORTANT]
> このリポジトリは、業務システムの設計・実装・検証方法を公開するためにゼロから作成した**架空の再構成（fictional reconstruction）**です。実在する勤務先・顧客・本番システムのソースコード、データ、URL、認証情報は含みません。

- Source: [github.com/watawatan1984/FieldOps-Portfolio](https://github.com/watawatan1984/FieldOps-Portfolio)
- Live demo: [fieldops-portfolio.onrender.com](https://fieldops-portfolio.onrender.com)
- Hosting: Render Free（Frankfurt）+ Neon Free PostgreSQL（AWS EU Central 1）
- Latest published source: [`main`](https://github.com/watawatan1984/FieldOps-Portfolio/tree/main)

## 主な機能

- 顧客・取引先・連絡先・現場の管理
- 営業案件の検索、提案、受注・失注などの状態遷移
- 受注案件からの作業指示作成、予定・担当者設定、作業イベント、完了
- 支店別ダッシュボード、全国比較、監査履歴、作業履歴検索
- PostgreSQLトランザクション、楽観的同時実行制御、追記専用履歴
- 相関ID、構造化ログ、ヘルスチェック、例外の安全な応答
- 管理者だけが実行できる、排他制御されたデモデータ初期化

## デモの4ロール

デモモードではパスワードをブラウザへ渡さず、署名されたロール選択によるワンクリックログインを使用します。この仕組みは架空データ専用で、通常の本番認証として利用する設計ではありません。

| ロール               | 主な操作範囲                           |
| -------------------- | -------------------------------------- |
| System Administrator | 全支店の参照、監査、デモ初期化         |
| Branch Manager       | 自支店の顧客・営業・作業管理           |
| Sales Representative | 自支店の営業・顧客業務                 |
| Field Technician     | 自分に割り当てられた作業と作業イベント |

サーバー側の認可は画面表示だけに依存せず、支店・所有者・担当者をデータベースから再取得して判定します。

## アーキテクチャとドメイン

依存方向を `Web -> Features -> Domain` とし、PostgreSQL・Identity・監査などの実装をInfrastructureへ分離したモジュラーモノリスです。

```text
FieldOps.Web            MVC / Razor / authentication / authorization
FieldOps.Features       queries, commands, application boundaries
FieldOps.Domain         entities, invariants, state transitions
FieldOps.Infrastructure EF Core, Npgsql, Identity, audit, demo reset
```

- [ドメイン用語集](CONTEXT.md)
- [設計仕様](docs/superpowers/specs/2026-08-11-fieldops-portfolio-design.md)
- [実装計画](docs/superpowers/plans/2026-08-11-fieldops-portfolio-implementation.md)
- [初期マイグレーションSQL](docs/evidence/initial-migration.sql)
- [作業履歴検索の実行計画](docs/evidence/work-history-explain.json)

## ローカル起動

### 必要環境

- .NET SDK `10.0.110` 以降の互換パッチ
- Docker Desktop
- PowerShell 7

### 1. PostgreSQLを起動

次の資格情報はローカル開発専用の例です。クラウドや共有環境では使用しないでください。

```powershell
docker run --name fieldops-postgres `
  -e POSTGRES_DB=fieldops `
  -e POSTGRES_USER=fieldops `
  -e POSTGRES_PASSWORD=fieldops_local_only `
  -p 5432:5432 `
  -d postgres:17-alpine
```

### 2. アプリを起動

```powershell
$env:ConnectionStrings__FieldOps = 'Host=127.0.0.1;Port=5432;Database=fieldops;Username=fieldops;Password=fieldops_local_only'
$env:DemoMode__Enabled = 'true'
$env:DemoMode__DatasetIdentifier = 'fieldops-portal-fictional-demo'
$env:DemoMode__DatasetVersion = '1'

dotnet restore FieldOps.sln
dotnet run --project src/FieldOps.Web --launch-profile http
```

起動後に `http://localhost:5062/demo-login` を開きます。終了後は `docker stop fieldops-postgres` で停止できます。

## デモ初期化の安全性

初期化は自動実行されません。System Administratorが確認画面で正確な確認語と短時間有効な署名付き意図トークンを送信した場合だけ実行されます。

- デモモード設定と承認済みデータセットマーカーを二重確認
- 通常更新は共有advisory lock、初期化は排他advisory lock
- 業務データ、監査、実行結果を単一トランザクションで確定
- 失敗時はロールバックし、秘密情報を除いた失敗証跡を別トランザクションで保存
- 同じidempotency keyの再実行は保存済み結果を返す

## テストと検証結果

2026-08-27、Windows / Linux CI / .NET 10 / PostgreSQL 17 / Chromiumで次を再確認しました。公開前の全CIゲートはソースコミット [`1c3ea75`](https://github.com/watawatan1984/FieldOps-Portfolio/commit/1c3ea75bd9a2df7000d8fa566c791a86a1779edf) で成功しています。負荷試験は隔離されたローカル環境で測定し、公開環境には実行していません。

| 種別              |    結果 | 主な範囲                                                |
| ----------------- | ------: | ------------------------------------------------------- |
| Domain tests      |   62/62 | 不変条件、状態遷移、終端規則                            |
| Integration tests | 188/188 | 実PostgreSQL、認可、同時実行、障害、安全な初期化        |
| Playwright E2E    |   16/16 | 4ロール、モバイル、CSP、アクセシビリティ、証跡基盤      |
| Full solution     | 266/266 | Release構成、失敗・スキップ0                            |
| Baseline load     |    PASS | 20 VUs / 10分、11,843 requests、p95 31.90 ms、HTTP失敗0 |
| Stress load       |    PASS | 100 VUs / 5分、29,548 requests、p95 39.63 ms、HTTP失敗0 |

- [負荷試験の検証結果](docs/evidence/load-test-results.md)
- [公開デプロイ検証結果](docs/evidence/public-deployment-verification.md)
- 負荷試験は隔離されたローカルDocker環境だけで実行しています。
- 数値はこの環境での再現可能な測定結果であり、無料の公開インスタンスが100同時ユーザーを処理できるという主張ではありません。

```powershell
dotnet build FieldOps.sln --configuration Release --no-restore -warnaserror
dotnet test FieldOps.sln --configuration Release --no-build
./scripts/check-readme.ps1
```

## Screenshots

スクリーンショットは現在未掲載です。画面と権限制御は上記Live demoで直接確認できます。

## 公開環境の制約

- Render Freeは15分間アクセスがないと停止し、次回アクセス時にコールドスタートが発生します。起動まで50秒以上かかる場合があります。
- Neon Freeも未使用時にscale-to-zeroするため、最初のDB接続が遅くなる場合があります。
- 公開デモに対してbaseline/stress負荷試験は実行しません。
- デモログインとデモ初期化は、承認済みの架空データセットでのみ有効にします。

## English summary

FieldOps Portal is a fictional, production-shaped ASP.NET Core MVC portfolio application for multi-branch customer, sales, and field-work operations. It demonstrates PostgreSQL persistence, resource-based authorization, optimistic concurrency, append-only history, structured diagnostics, a guarded demo reset, four role-specific browser journeys, and reproducible local load-test evidence. It contains no employer source code or real customer data. The public live demo runs on Render Free with Neon Free PostgreSQL.

## License

ライセンスはまだ付与していません。MITなどのライセンスは、公開条件を別途確認してから追加します。
