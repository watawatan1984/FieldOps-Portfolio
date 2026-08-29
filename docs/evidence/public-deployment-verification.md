# 公開デプロイ検証記録

## 検証対象

- 公開URL: <https://fieldops-portfolio.onrender.com>
- アプリ: Render Free（Frankfurt）
- データベース: Neon Free PostgreSQL 17（AWS EU Central 1）
- 日本語UI実装リビジョン: `adf9893b91cf9318d1bcaf8dbc2874ceb8a49cbe`
- 検証日: 2026-08-29 JST

## 検証結果

| 確認項目 | 結果 |
| --- | --- |
| GitHub CI | 合格 — 書式、Releaseビルド、Domain 63件、Integration 213件、Linuxコンテナ、Playwright E2E 27件。合計303件 |
| Renderデプロイ | 合格 — `dep-da93rqhf2nfc73e6t1lg` がLiveへ移行 |
| `/health/live` | HTTP 200 |
| `/health/ready` | HTTP 200、Neon接続確認済み |
| HTTPS役割選択画面 | 日本語表示を確認 |
| システム管理者 | ログイン、ホーム、支店状況を確認 |
| 支店管理者 | ログイン、担当支店、顧客一覧を確認 |
| 営業担当者 | ログイン、営業案件一覧を確認 |
| 現場担当者 | ログイン、作業予定一覧を確認 |
| 4役割の公開読み取りスモーク | 全役割合格 |
| PC表示 | 1440×900で役割選択、メニュー、主要導線を確認 |
| タブレット表示 | 768×1024で役割選択、折りたたみメニュー、作業予定への導線を確認 |
| 管理者によるデモ初期化 | ユーザー承認後に1回実行し成功。相関ID `158c3959467d428ba2b716866f7186a6` |
| 初期化後の固定データ | 利用者名、支店名、顧客、営業案件、作業予定が日本語であることを再ログイン後に確認 |
| 旧英語データ | 初期化後の営業案件・作業予定画面に `Fictional`、`Taylor Kim`、`Alex Morgan` がないことを確認 |

- CI: <https://github.com/watawatan1984/FieldOps-Portfolio/actions/runs/33227666740>
- 公開検証ワークフロー: <https://github.com/watawatan1984/FieldOps-Portfolio/actions/runs/33228071224>

## 無料枠での挙動

- Render Freeは一定時間使われないと停止するため、最初の表示には50秒以上かかる場合があります。
- Neon Freeも未使用時に停止するため、最初のデータ表示が遅くなる場合があります。
- この公開環境はポートフォリオ用デモであり、本番業務向けの稼働保証はありません。
- 負荷試験は公開環境に影響を与えないよう、分離したローカルDocker環境だけで行います。
