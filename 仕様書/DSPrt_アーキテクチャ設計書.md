# DSPrt アーキテクチャ設計書

> **帳票印刷プログラム | DScore プロジェクト | Windows 11 WPF アプリケーション**
> 最終更新: 2026-08-02（「規程」→「規定」表記統一・帳票実装完了）
> ステータス: **Phase 1〜8 実装完了（スケーティング帳票完成）**

---

## 目次

1. [システム概要](#1-システム概要)
2. [システム全体アーキテクチャ](#2-システム全体アーキテクチャ)
3. [内部レイヤー構成](#3-内部レイヤー構成)
4. [電文プロトコル設計](#4-電文プロトコル設計)
   - 4-1. 電文フォーマット
   - 4-2. コマンド一覧
   - 4-3. PR_PRINT 電文詳細
   - 4-4. data フィールドの種別と構造
   - 4-5. マスターデータのキャッシュ方針
5. [主要コンポーネント設計](#5-主要コンポーネント設計)
6. [印刷処理フロー](#6-印刷処理フロー)
7. [帳票エンジン選定](#7-帳票エンジン選定)
8. [帳票デザインのワークフロー](#8-帳票デザインのワークフロー)
9. [表彰状印刷（デザイナー内蔵）](#9-表彰状印刷デザイナー内蔵)
10. [接続管理・再接続・起動オプション](#10-接続管理再接続起動オプション)
11. [DSServer_main 側追加実装](#11-dsserver_main-側追加実装)
12. [プロジェクト構成](#12-プロジェクト構成)
13. [設定ファイル構造](#13-設定ファイル構造)
14. [MainWindow UI 構成](#14-mainwindow-ui-構成)
15. [帳票一覧](#15-帳票一覧)
16. [主要 NuGet パッケージ](#16-主要-nuget-パッケージ)
17. [実装フェーズ計画](#17-実装フェーズ計画)

---

## 1. システム概要

DSPrt は DSServer_main から WebSocket 電文を受信し、指定された帳票レイアウトに JSON データを流し込んで自動印刷を行う Windows 11 デスクトップアプリケーション。

**基本方針:**
- 既存クライアント（DSDsp, DC/GM 系）と同一の電文フォーマット・接続方式を採用
- 新規プロトコルプレフィックス `PR_` を追加することで既存システムへの影響を最小化
- 帳票エンジンは **FastReport Open Source 2025.1.0（MIT ライセンス）** を採用
- FastReport.OpenSource（OS 版）には `Report.Print()` / `DesignerControl` が非搭載のため、以下の方式を採用:
  - **印刷**: `PreparedPages.GetPage(i).Draw(FRPaintEventArgs)` で PrintDocument.PrintPage の Graphics に **直接描画**（PNG 変換なし・高品質）
  - **プレビュー**: `HTMLExport` でシングルページ HTML に出力 → WPF `WebBrowser` で表示
  - **デザイナー**: `.frx` ファイルをデフォルト関連アプリ（FastReport Designer 等）で外部起動

**確定済み要件（2025-07）:**
- 印刷トリガーは **DSServer_main のオペレーター画面ボタン** から発生（自動発火なし）
- 表彰状は **1 組 1 ジョブ**（決勝 6 組なら 6 ジョブ送信）。個人戦は 1 人 1 ジョブ
- **再印刷**：DSPrt のジョブ一覧から特定ジョブを選択して単独再印刷可能
- DSPrt は **複数インスタンス起動可能**（パターン B）。例：DSPrt_Award（表彰状専用）+ DSPrt_General（通常帳票）
- サーバーから印刷依頼時、接続中の DSPrt が **複数あれば宛先を選択**、1 台なら自動送信
- 同一 `instanceId` での二重起動を **Mutex で禁止**（異なる instanceId なら複数起動 OK）
- WebSocket 切断時は **DSPrt が 10 秒間隔で自動リトライ**。サーバーは未接続時に `PR_PRINT` が来た場合はエラーを返す
- DSServer_main 起動時に DSPrt を **自動起動するオプション**あり（設定ファイルで ON/OFF）
- 1 競技会のみ対応。同一 OrgCd に複数競技会がある場合は **選択ダイアログ**を表示
- DS_Status は印刷指示の直前に **常にサーバーから最新版を送信**（キャッシュは保険として保持）

---

## 2. システム全体アーキテクチャ

```
┌─────────────────────────────────────┐
│         DSServer_main（既存）         │
│   C# .NET 8 / Windows Forms         │
└──────────────┬──────────────────────┘
               │ WebSocket  ws://IP:7269
               │ 電文: OrgCd,CmpNo,From,PR_xxx,{JSON}
               │
┌──────────────▼──────────────────────┐
│            DSPrt（新規）              │
│   C# .NET 8 / WPF / Windows 11      │
│                                      │
│  ┌──────────────┐  ┌──────────────┐ │
│  │  帳票エンジン  │  │  印刷制御    │ │
│  │ FastReport OS │  │System.Printing│ │
│  │   .frx 描画   │  │Print Spooler │ │
│  └──────────────┘  └──────────────┘ │
└─────────────────────────────────────┘
```

---

## 3. 内部レイヤー構成

| レイヤー | クラス | 役割 |
|---------|--------|------|
| **通信層** | `WebSocketClient`（DSDsp から流用） | WebSocket 接続・送受信・再接続 |
| **通信層** | `PR_MessageHandler`（新規） | `PR_` プレフィックス電文のルーティング |
| **アプリ層** | `DSPrtClient`（Facade） | 接続・初期化・送受信のオーケストレーション |
| **アプリ層** | `PrintJobQueue` | 優先度付きジョブキュー・重複排除 |
| **帳票層** | `ReportRenderer` | FastReport `.frx` → XPS/印刷出力 |
| **帳票層** | `ReportLayoutRegistry` | layoutId → `.frx` パス・印刷設定マッピング |
| **印刷層** | `PrinterController` | `System.Printing` ラッパー・部数/両面/用紙制御 |
| **UI 層** | `MainWindow`（WPF） | 接続状態・ジョブログ・設定・デザイナー起動 |

---

## 4. 電文プロトコル設計

### 4-1. 電文フォーマット（既存踏襲）

```
OrgCd,CmpNo,From,Command,{JSON Body}

例（サーバー→DSPrt）:
JS,20001,SVR,PR_PRINT,{"jobId":"20001_RSLT_001","layoutId":"RESULT_A4","copies":2,"data":{...}}

例（DSPrt→サーバー）:
JS,20001,DSPrt_Award,PR_ACK,{"jobId":"20001_RSLT_001","status":"accepted"}
```

> `From` フィールドに **instanceId**（例: `DSPrt_Award`）を使うことで、
> サーバーがどの DSPrt から応答が来たか識別できる。

### 4-2. 新規コマンド一覧

コマンドは役割によって **3 グループ** に分類する。

#### グループ A: セッション管理

| 方向 | コマンド | タイミング | 説明 |
|------|---------|-----------|------|
| PRT → SVR | `PR_LOGIN` | 起動時 | DSPrt ログイン要求 / `instanceId, displayName, version` |
| SVR → PRT | `PR_LOGIN_OK` | ログイン応答 | `authId, serverVersion` |
| SVR → GM  | `PR_NOTIFY_PRT_LIST` | DSPrt 接続・切断時 | 接続中 DSPrt 一覧を GM クライアントへ Push |

#### グループ B: マスターデータ配信（DA_Master / DS_Status）

> DSPrt はこれらのデータをメモリにキャッシュし、印刷時に参照する。
> サーバーはログイン後に自動配信し、変更があるたびに差分を Push する。

| 方向 | コマンド | タイミング | 説明 |
|------|---------|-----------|------|
| SVR → PRT | `PR_ANS_DA` | ログイン直後 | DA_Master 全体を初期配信 |
| SVR → PRT | `PR_UPD_DA` | DA_Master 更新時 | DA_Master 全体を再送（選手・審判員追加等） |
| SVR → PRT | `PR_ANS_DS` | ログイン直後 | DS_Status 全体を初期配信 |
| SVR → PRT | `PR_UPD_DS` | 進行状態変更時 | DS_Status **差分**を Push（後述） |

#### グループ C: 印刷ジョブ制御

> `PR_PRINT` は特定の DSPrt インスタンスのセッション ID へ直接送信する。
> GM クライアントは印刷ボタン押下時に宛先 DSPrt を選択する（複数接続時のみ）。

| 方向 | コマンド | タイミング | 説明 |
|------|---------|-----------|------|
| GM → SVR | `GM_PRT_PRINT` | 印刷ボタン押下 | GM からの印刷指示 / `targetInstanceId, jobId, layoutId, copies, priority, data{}` |
| SVR → PRT | `PR_PRINT` | GM_PRT_PRINT 受信後 | 指定 DSPrt へ転送 / `jobId, layoutId, copies, priority, data{}` |
| SVR → PRT | `PR_CANCEL` | キャンセル時 | 印刷ジョブキャンセル / `jobId` |
| PRT → SVR | `PR_ACK` | PR_PRINT 受信直後 | 印刷受付確認 / `jobId, status="accepted"` |
| PRT → SVR | `PR_DONE` | 印刷完了時 | 印刷完了通知 / `jobId, status="done"\|"error", message` |

### 4-3. PR_PRINT 電文 JSON 詳細

`PR_PRINT` の `data` は **DV_Result** のみ。DA_Master・DS_Status はキャッシュを参照するため送付不要。

```json
{
  "jobId":    "20001_AWARD_20250720183045_001",
  "layoutId": "AWARD_CERTIFICATE",
  "copies":   1,
  "priority": 1,
  "data":     { /* DV_Result 構造 */ }
}
```

#### jobId 採番ルール

```
{CmpNo}_{layoutId略称}_{yyyyMMddHHmmss}_{連番3桁(000〜)}

例:
  20001_AWARD_20250720183045_000   ← 表彰状1枚目
  20001_AWARD_20250720183045_001   ← 表彰状2枚目
  20001_RESULT_20250720172230_000  ← 結果帳票
  20001_HEAT_20250720154512_000    ← ヒート表
```

| 要素 | 内容 | 例 |
|------|------|---|
| `CmpNo` | 競技会番号 | `20001` |
| `layoutId 略称` | layoutId の先頭単語（大文字） | `AWARD` / `RESULT` / `HEAT` |
| `yyyyMMddHHmmss` | サーバー側での生成日時 | `20250720183045` |
| `連番` | 同一バッチ内の通し番号（000 始まり） | `000`, `001`, ... `005` |

- 採番は **サーバー側**（`PR_MessageHandler`）が行う
- 再印刷ジョブは元の `jobId` に `_R` サフィックスを付ける（例: `20001_AWARD_20250720183045_001_R`）
- `jobId` はサーバー・DSPrt 双方でログのキーとして使用する

| フィールド | 型 | 説明 |
|-----------|---|------|
| `jobId` | string | ユニークジョブ ID（重複受信対策。省略不可） |
| `layoutId` | string | DSPrt 側の `DSPrt.json` で管理する帳票 ID |
| `copies` | int | 印刷部数（省略時は `DSPrt.json` の設定値を使用） |
| `priority` | int | 優先度: `1`=高 / `2`=通常 / `3`=低 |
| `data` | object | DV_Result 構造の JSON（後述 4-4-③） |

> **DA_Master / DS_Status を使う帳票**（エントリーリスト・ヒート表等）を印刷する場合は、
> `data` フィールドは **空オブジェクト `{}`** を送り、DSPrt がキャッシュから取得する。
> ただし DA_Master 帳票の印刷指示では `layoutId` だけで DSPrt は何を使うか判断できる。

### 4-4. data フィールドの種別と構造

各データ種別の役割と送信タイミングを整理する。

| データ種別 | 送信タイミング | 電文 | 帳票での使われ方 |
|-----------|--------------|------|----------------|
| **DA_Master** | ログイン直後 / 更新時 | `PR_ANS_DA` / `PR_UPD_DA` | エントリーリスト・審判員リスト・選手名解決 |
| **DS_Status** | ログイン直後 / 変更時差分 | `PR_ANS_DS` / `PR_UPD_DS` | ヒート組み合わせ表・進行スケジュール |
| **DV_Result** | `PR_PRINT` 電文の `data` に直接含める | `PR_PRINT` | 結果帳票・表彰状 |

---

#### ① DA_Master（競技会マスター）

競技会の基本情報・参加選手・審判員を含む。**エントリーリスト・審判員リスト** 等の印刷に使用する。

```json
{
  "DA_OrgCD":             "JS",
  "DA_CompNo":            "20001",
  "DA_CompName":          "第〇〇回 全日本選手権大会",
  "DA_CompDate":          "2025-07-20",
  "DA_CompPromoterOrgName": "公益社団法人 日本ダンス評議会",
  "DA_CompPlace":         "〇〇体育館",
  "DA_ChairPersonID":     "JDC001",
  "DA_ChiefJudgeID":      "JDC002",
  "DA_ScurtineerID":      "JDC003",
  "DB_KUBUNs": [
    {
      "DB_KbnNo": "1",
      "DB_KbnName": "スタンダード",
      "DB_KbnDispName": "スタンダード",
      "DC_ROUNDs": [
        {
          "DC_RndNo": "1",
          "DC_RndName_J": "第1ラウンド",
          "DC_RndHeatCnt": 3,
          "DD_DGRPs": [
            {
              "DD_DGrpNo": "1",
              "DD_DGrpName": "スタンダード5種目",
              "DE_DANCEs": [
                { "DE_DncNo": 1, "DE_DncCd": "W",  "DE_DncNm_J": "ワルツ" },
                { "DE_DncNo": 2, "DE_DncCd": "T",  "DE_DncNm_J": "タンゴ" },
                { "DE_DncNo": 3, "DE_DncCd": "VW", "DE_DncNm_J": "ヴィエニーズワルツ" },
                { "DE_DncNo": 4, "DE_DncCd": "SF", "DE_DncNm_J": "スローフォックストロット" },
                { "DE_DncNo": 5, "DE_DncCd": "Q",  "DE_DncNm_J": "クイックステップ" }
              ]
            }
          ]
        }
      ]
    }
  ],
  "DJ_JUDGEs": [
    {
      "DJ_JdgCd": "A",
      "DJ_JdgName": "審判員 太郎",
      "DJ_JdgDispName": "審判員 太郎",
      "DJ_JdgCtry": "日本ダンス評議会"
    }
  ],
  "DM_MEMBERs": [
    {
      "DM_UkeNo": 1,
      "DM_MASTERs": [
        {
          "DM_No": "101",
          "DM_LName": "田中 太郎",
          "DM_LDispName": "田中 太郎",
          "DM_PName": "山田 花子",
          "DM_PDispName": "山田 花子",
          "DM_Ctry": "東京支部",
          "DM_ENTRYs": [
            { "DM_KbnNo": "1", "DM_Ent": "1", "DM_Class": "A" }
          ]
        }
      ]
    }
  ]
}
```

**主な利用帳票:**

| layoutId | 帳票名 | 使用する主なフィールド |
|----------|--------|----------------------|
| `ENTRY_LIST_A4` | エントリーリスト | `DA_CompName`, `DB_KUBUNs`, `DM_MEMBERs` |
| `JUDGE_LIST_A4` | 審判員リスト | `DA_CompName`, `DA_CompDate`, `DJ_JUDGEs` |
| `PROGRAM_A4` | プログラム | `DA_CompName`, `DB_KUBUNs`（種目・ラウンド構成） |

---

#### ② DS_Status（進行状況）

フロア・ヒートの進行状態を含む。**進行表・ヒート組み合わせ表** 等の印刷に使用する。

```json
{
  "DS_OrgCD":   "JS",
  "DS_CompNo":  "20001",
  "DS_Version": 42,
  "DS_FLOORs": [
    {
      "DS_FlrCd":       "A",
      "DS_CurPrgNo":    "3",
      "DS_CurPrgSubNo": "1",
      "DS_PRGRSs": [
        {
          "DS_PrgNo":     "3",
          "DS_PrgSubNo":  "1",
          "DS_KbnNo":     "1",
          "DS_RndNo":     "1",
          "DS_DGrpNo":    "1",
          "DS_PrgSts":    "2",
          "DS_PrgPStaTM": "2025-07-20T10:00:00",
          "DS_PrgPEndTM": "2025-07-20T10:30:00",
          "PlayerAssignments": [
            { "PlayerNo": "101", "AssignedHeatIds": ["heat-uuid-001"] },
            { "PlayerNo": "102", "AssignedHeatIds": ["heat-uuid-001"] }
          ],
          "JudgeAssignments": [
            {
              "JudgeCode": "A",
              "AssignedDances": [
                { "DanceNo": 1, "AssignedHeatIds": ["heat-uuid-001"] }
              ]
            }
          ],
          "DS_PRGDANCEs": [
            {
              "DS_DncNo": 1,
              "DS_DncSts": "2",
              "DS_PRGHEATs": [
                {
                  "DS_HeatId":  "heat-uuid-001",
                  "DS_HeatNo":  1,
                  "DS_HeatSts": "3"
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

**主な利用帳票:**

| layoutId | 帳票名 | 使用する主なフィールド |
|----------|--------|----------------------|
| `HEAT_LIST_A4` | ヒート組み合わせ表 | `DS_FLOORs`, `DS_PRGRSs`, `PlayerAssignments` |
| `SCHEDULE_A4` | 進行スケジュール表 | `DS_PRGRSs`, `DS_PrgPStaTM`, `DS_PrgPEndTM` |

> **注意:** `DS_Status` 単体では選手名・区分名が含まれない。帳票で氏名等を表示するには
> `DA_Master` と合わせて送るか、DSPrt 側でキャッシュした `DA_Master` と結合する。
> → `dataType: "DS_Status"` の場合、DSPrt は起動時に受信・保持している `DA_Master` を内部で参照する。

---

#### ③ DV_Result（採点結果）

種目別・選手別のスコアと順位を含む。**結果帳票・表彰状** 等の印刷に使用する。

```json
{
  "団体CD":   "JS",
  "競技会NO": "20001",
  "区分番号": "1",
  "区分名":   "スタンダード",
  "ラウンド番号": "3",
  "ラウンド名":   "ファイナル",
  "採点方式名":   "PCS採点",
  "採点方式ID":   "AJS31",
  "総合結果": [
    {
      "総合順位番号": 1,
      "背番号":     "101",
      "総合得点":    95.6,
      "総合順位表記": "1"
    },
    {
      "総合順位番号": 2,
      "背番号":     "102",
      "総合得点":    93.2,
      "総合順位表記": "2"
    }
  ],
  "種目結果": [
    {
      "種目順":  1,
      "種目記号": "W",
      "選手結果": [
        {
          "背番号":     "101",
          "種目得点":    19.2,
          "種目順位番号": 1,
          "種目順位表記": "1",
          "TES": { "TES得点": 9.6 },
          "PCS": [
            { "PCS番号": 1, "PCS得点": 8.5 },
            { "PCS番号": 2, "PCS得点": 8.3 }
          ],
          "ジャッジ詳細結果": [
            {
              "ジャッジ記号": "A",
              "素点": 19.2,
              "順位点": 1.0,
              "PCS素点": [
                { "PCS番号": 1, "PCS素点": 8.5 }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

**主な利用帳票:**

| layoutId | 帳票名 | 使用する主なフィールド |
|----------|--------|----------------------|
| `RESULT_A4` | 結果帳票（総合） | `区分名`, `ラウンド名`, `総合結果` |
| `RESULT_DETAIL_A4` | 結果帳票（種目別詳細） | `種目結果`, `選手結果`, `ジャッジ詳細結果` |
| `AWARD_CERTIFICATE` | 表彰状 | `区分名`, `ラウンド名`, `総合結果[0]`（1位のみ） |

> **表彰状の場合:** 1 名（1 組）ごとに 1 ジョブとして送るか、`総合結果` 配列の全要素を
> 1 ジョブで送って帳票側でページを繰り返すかは運用設計で決める。

---

### 4-5. マスターデータのキャッシュ方針と差分更新

#### DA_Master のキャッシュ

DA_Master は競技会単位で変更頻度が低い（選手・審判員の追加・修正時のみ変化）。
変更量が限定的なため、**変更時は全体を再送**する方針とする。

```
DSPrt 起動
  → PR_LOGIN 送信
  → SVR: PR_ANS_DA（DA_Master 全体）を自動 Push
  → DataManager.DA_Master にキャッシュ

DA_Master 変更時（選手追加・審判員変更等）
  → SVR: PR_UPD_DA（DA_Master 全体）を再送
  → DataManager.DA_Master を上書き更新

帳票レンダリング時（DS_Status 系 / DV_Result 系）
  → 背番号 → DM_No で選手名を解決
  → 審判員コード → DJ_JdgCd で審判員名を解決
  → 区分番号 → DB_KbnNo で区分名を解決
```

#### DS_Status の扱い

DS_Status は印刷指示の **直前に常にサーバーから最新版を送信**する。
DSPrt 側はキャッシュとして保持するが、印刷に使うのはサーバーから受信した最新版を優先する。

```
DSPrt 起動
  → PR_LOGIN 送信
  → SVR: PR_ANS_DS（DS_Status 全体）を Push → DataManager にキャッシュ

DS_Status 系帳票の印刷指示直前（サーバー側オペレーター画面でボタン押下）
  → SVR: PR_ANS_DS（最新の DS_Status 全体）を再送
  → DataManager を更新
  → SVR: PR_PRINT（layoutId = HEAT_LIST_A4 等、data = {}）を送信
  → DSPrt が DataManager の DS_Status を使ってレンダリング・印刷
```

> キャッシュは通信中断時のフォールバックとして保持する。
> `PR_UPD_DS`（差分 Push）は実装しない（DSPrt は差分を受け取っても適用可能だが、運用上の複雑さを排除するため）。
> DS_Status のデータ量が将来的に問題になった場合は差分方式への移行を検討する。

---

## 5. 主要コンポーネント設計

### PR_MessageHandler（通信層）

- コマンドプレフィックス `PR_` を専任処理
- `PR_PRINT` → `PrintJobQueue.Enqueue()` へ転送後、即座に `PR_ACK` 返信
- `PR_CANCEL` → キュー内または処理中のジョブを中断
- 応答電文（`PR_ACK` / `PR_DONE`）の組み立てと送信

### PrintJobQueue（アプリ層）

- `PriorityQueue<PrintJob, int>` でジョブ優先管理
- `HashSet<string>` による jobId 重複排除
- 非同期逐次処理（同時 1 ジョブ）
- ジョブ状態遷移: `Queued` → `Processing` → `Done` / `Error` / `Cancelled`

### ReportRenderer（帳票層）

- FastReport `Report` クラスで `.frx` ファイルを読み込み
- **DV_Result**（`PR_PRINT.data`）を `DataSet` / `DataTable` に変換してバインド
- **DA_Master / DS_Status** は `DataManager` のキャッシュを参照してバインド（電文から取得しない）
- **印刷方式（重要・Phase 7 で確定）**: `PreparedPages.GetPage(i).Draw(FRPaintEventArgs)` で `PrintDocument.PrintPage` の `Graphics` に **直接描画**。
  PNG 変換を経由しないため高品質・高速。FastReport 内部単位（96dpi px）に対して `scaleX = logicalDpi(100) / 96f ≈ 1.042` を使用。
- **フォントサイズ補正（重要）**: `PrintDocument.PrintPage` の `Graphics` は `PageUnit=Display`（100dpi 論理ピクセル）のため、
  FastReport の `IsPrinting=true` 時にフォントサイズが scaleX でスケールされず相対的に小さく見える。
  `AdjustFontSizesForPrinting(page, 1.2f)` を `Draw()` 前に実行して `PreparedPages.GetPage()` 取得後の TextObject フォントサイズを **1.2 倍** に補正する（調整済みの適正値）。
- **プレビュー**: `HTMLExport`（`EmbedPictures=true`, `SinglePage=true`）で HTML ファイルに出力。WPF `WebBrowser` または外部ブラウザで表示
- **PDF 出力**: FastReport.OpenSource に PDF エクスポートは含まれないため非対応（HTML で代替）。印刷時に「Microsoft Print to PDF」プリンターを選択すれば PDF 出力可能。

#### FastReport DataTable バインディングの実装詳細（実装で確認済み）

FastReport.OpenSource 2025.1.0 の `TableDataSource` は内部で `InitSchema()` を呼び出し、`Connection == null` の場合に `table = Reference as DataTable` で再取得する仕様。このため以下の3段階対策を実装：

```csharp
// BindTableToReport() の実装
var tds = report.Dictionary.DataSources.FindByName(table.TableName) as TableDataSource;
if (tds != null)
{
    // 1. Table に直接セット
    tds.Table = table;
    
    // 2. Reference にも同じオブジェクトをセット
    //    （InitSchema() が Connection==null 時に "table = Reference as DataTable" するため）
    tds.Reference = table;
    
    // 3. IgnoreConnection=true（internal プロパティ、リフレクション経由）
    //    Connection プロパティが常に null を返すようになり DB 再取得を完全防止
    var prop = typeof(TableDataSource).GetProperty("IgnoreConnection",
        BindingFlags.Instance | BindingFlags.NonPublic);
    prop?.SetValue(tds, true);
}
```

#### WebMode 切り替え（スクリプトコンパイル有効化）

FastReport の `WebMode=true` は `[DataSource.Field]` 式のコンパイルを無効化するため、`Prepare()` 前後で切り替え：

```csharp
bool prevWebMode = FastReport.Utils.Config.WebMode;
FastReport.Utils.Config.WebMode = false;  // Prepare() 時だけコンパイル有効化
try { report.Prepare(); }
finally { FastReport.Utils.Config.WebMode = prevWebMode; }
```

### PrinterController（印刷層）

- `System.Printing.LocalPrintServer` / `PrintQueue` でプリンター存在確認・一覧取得
- 帳票別プリンター名解決（設定値 → デフォルトへフォールバック）
- 実際の印刷送信は `ReportRenderer` 内の `PrintDocument` が担当

### ReportLayoutRegistry（帳票層）

`DSPrt.json` の `Layouts` 配列を読み込み、`layoutId` をキーとして帳票設定を管理する。

```json
{
  "layoutId":    "RESULT_A4",
  "frxPath":     "./Reports/Result_A4.frx",
  "printerName": "Canon TR8600",
  "copies":       2,
  "duplex":       "OneSided",
  "paperSize":    "A4"
}
```

---

## 6. 印刷処理フロー

```
① WebSocket 受信
   WebSocketClient.ReceiveLoop() が電文受信
   → PR_MessageHandler.HandleAsync() へ

② 電文解析
   ParsedMessage.Parse() → command = PR_PRINT
   JSON Body をデシリアライズ → PrintJobRequest オブジェクト生成

③ ACK 返信（即座に）
   PR_ACK を DSServer_main へ送信（受信確認）

④ ジョブキュー投入
   PrintJobQueue.Enqueue(job)
   → jobId 重複チェック → PriorityQueue へ追加

⑤ レイアウト解決
   ReportLayoutRegistry.Get(layoutId)
   → .frx ファイルパス・プリンター設定取得

⑥ 帳票レンダリング
   ReportRenderer.RenderAsync(frxPath, data)
   → FastReport が .frx にデータバインド → 印刷可能状態に

⑦ 印刷送信
   PrinterController.PrintAsync(report, settings)
   → Windows Print Spooler へ送出

⑧ 完了通知
   PR_DONE 電文を DSServer_main へ送信
   { jobId, status: "done" | "error", message }
```

---

## 7. 帳票エンジン選定

### 採用決定: **FastReport Open Source 2025.1.0（MIT ライセンス）**

| 比較観点 | RDLC | **FastReport OS** |
|---------|------|-------------------|
| デザイナー起動 | Visual Studio 2022 必須 | **スタンドアロン exe で起動可能** |
| DSPrt へのデザイナー内蔵 | 不可 | **不可（OS 版。有料版のみ対応）** ※後述 |
| JSON バインド | DataSet 変換必要 | DataSet 変換必要（直接バインドは有料版のみ） |
| 直接印刷（Print()） | `LocalReport.Render()` → PrintDocument | **不可（OS 版）**。`ImageExport` → `PrintDocument` で代替 |
| PDF エクスポート | ◎ | **不可（OS 版）**。HTMLExport で代替 |
| 現場での帳票修正 | VS が必要 | **スタンドアロン Designer で修正可能** |
| ライセンス | 無料 | **無料（MIT）** |
| .NET 8 対応 | ◎ | **◎** |
| 出力形式（OS 版） | PDF / Excel / Word / Image | **HTML / Image のみ** |

### ⚠️ FastReport Open Source（OS 版）の制限（実装で確認済み）

FastReport.OpenSource 2025.1.0 を実際に使用して判明した制限事項：

| 機能 | OS 版の状況 | DSPrt での対処 |
|------|-----------|---------------|
| `Report.Print()` | **存在しない** | `PreparedPages.GetPage(i).Draw(FRPaintEventArgs)` → `PrintDocument.PrintPage` の `Graphics` に直接描画（Phase 7 で確定） |
| `Report.PrintSettings` | **存在しない** | `PrintDocument.PrinterSettings` で設定 |
| `DesignerControl` クラス | **存在しない** | `.frx` をデフォルトアプリで外部起動 |
| PDF エクスポート | **存在しない** | `HTMLExport` でプレビュー HTML を生成 |
| `FastReport.OpenSource.Export.Pdf` NuGet | **存在しない** | — |
| Excel / Word エクスポート | 未確認 | 現状は HTML / 直接描画のみ使用 |
| データバインド（RegisterData） | `RegisterData(DataTable, name)` ✅ | JSON → DataTable 変換して登録 |
| データバインド（直接 Table セット） | ✅（要注意：後述） | `tds.Table` + `tds.Reference` 両方セット必須 |
| `.frx` 読み込み | `report.Load(path)` ✅ | 正常動作 |
| `report.Prepare()` | ✅（要注意：後述） | WebMode=false で実行必須 |
| `WebMode=true` 時の制約 | **`[DataSource.Field]` 式が CS0103 エラー** | `Prepare()` 前後で `WebMode=false/true` 切り替え |
| `TableDataSource.Connection=null` の挙動 | **`Table` も `null` にリセットされる** | Connection に触らず `Table` + `Reference` セット |
| `TableDataSource.IgnoreConnection` | **internal プロパティ** | リフレクションで `true` 設定（DB 再取得防止） |

#### 重要な実装上の注意点（トラブルシューティング済み）

1. **`WebMode=true` では `[DataSource.Field]` 式がコンパイルできない**
   - 対策: `Prepare()` 実行時だけ `WebMode=false` に切り替える
   
2. **`tds.Connection=null` にすると `tds.Table` も `null` になる**
   - 対策: Connection に触らず、`tds.Table` と `tds.Reference` の両方に DataTable をセット
   
3. **`.frx` の `<TableDataSource>` に直接バインドする場合**
   - `InitSchema()` が `Connection==null` 時に `table = Reference as DataTable` で復元する
   - `Reference` プロパティにも同じ DataTable をセットしないと `null` になる
   
4. **`IgnoreConnection=true` の必要性**
   - internal プロパティのためリフレクションで設定
   - `Connection` プロパティが常に `null` を返すようになり、DB からの再取得を完全に防ぐ

### RDLC を選ばなかった理由（方針維持）

デザイナーが Visual Studio プラグインとして実装されており、**実行時にデザイナーを起動する API が存在しない**。
FastReport OS 版でもデザイナー内蔵は不可だが、**スタンドアロン FastReport Designer exe で編集 → `.frx` を所定ディレクトリに配置**するワークフローが成立するため、FastReport OS を採用する。

---

## 8. 帳票デザインのワークフロー

```
① JSON 仕様確定
   DSServer_main 側の PR_PRINT.data{} の構造を確定する
           ↓
② デザイナー起動
   A) FastReport スタンドアロンデザイナー（別途 exe）を使用
      ※ FastReport.OpenSource には DesignerControl が含まれないため DSPrt 内蔵は不可
   B) DSPrt の「帳票設定」タブ → レイアウトを選択 →「デザイナーで開く」ボタン
      → .frx ファイルをデフォルト関連アプリで外部起動（OS のファイル関連付けに依存）
           ↓
③ レイアウト作成
   ・データソース（DataTable）を登録
   ・ヘッダー・データバンド（繰り返し行）・フッターをドラッグ配置
   ・フィールドをデータソースパネルからキャンバスへドロップ
           ↓
④ .frx ファイル保存
   ./Reports/ フォルダへ配置
   DSPrt.json の Layouts に layoutId と frxPath を登録
           ↓
⑤ プレビュー確認
   DSPrt の「プレビュー」タブ → ジョブログからジョブを選択 →「プレビュー生成」
   → HTML ファイルを WPF WebBrowser で表示（または「ブラウザで開く」）
           ↓
⑥ 運用
   DSServer_main から PR_PRINT 電文を送信するだけで自動印刷
```

### FastReport デザイナーで利用できる主要機能

| 機能 | DSPrt での活用場面 |
|------|------------------|
| データバンド（繰り返し行） | 選手結果一覧・エントリーリストの行繰り返し |
| グループヘッダー/フッター | 区分別・ラウンド別に集計行を挿入 |
| 集計式（Sum / Count / Avg） | 出場人数・平均点などの自動集計 |
| 条件付き書式 | 1 位を太字・金色表示など |
| テキストボックス自由配置 | 表彰状の文言・位置調整 |
| 画像フィールド | 競技団体ロゴ・印章画像の印刷 |
| 改ページ制御 | 区分ごとに新ページへ / 1 表彰状 1 ページ |
| バーコード / QR コード | 選手 ID のバーコード印刷 |

---

## 9. 表彰状印刷（デザイナー外部起動）

表彰状は一般的な帳票と異なり、**文言・フォントサイズ・印刷位置を運営者が都度調整**する必要がある。

> **⚠️ 設計変更（実装で判明）**
> FastReport.OpenSource（OS 版）には `DesignerControl` クラスが含まれていないため、
> **DSPrt 内へのデザイナー内蔵は不可能**。
> 代わりに「デザイナーで開く」ボタンで `.frx` ファイルをデフォルトアプリ経由で外部起動する。

### デザイナー外部起動の実装（実装済み）

```csharp
// MainWindow.xaml.cs：「デザイナーで開く」ボタン押下時
private void BtnOpenDesigner_Click(object sender, RoutedEventArgs e)
{
    if (LayoutGrid.SelectedItem is not LayoutSetting layout) return;

    string frxPath = ResolveFrxPath(layout.FrxPath);
    // .frx ファイルをデフォルト関連アプリ（FastReport Designer 等）で開く
    Process.Start(new ProcessStartInfo
    {
        FileName        = frxPath,
        UseShellExecute = true  // OS の関連付けに従って起動
    });
}
```

**FastReport Designer（スタンドアロン）を事前にインストール**し、`.frx` の関連付けを設定することで、
ボタン 1 クリックでデザイナーが起動する運用が可能。

### 表彰状帳票の設計指針

- **用紙サイズ**: A4 縦（または B5 縦）を標準とし、デザイナーで変更可能
- **固定要素**（デザイン時に配置）: 背景画像・罫線・団体ロゴ・「表彰状」タイトル
- **差し込み要素**（`data{}` から取得）:
  - `recipientName`（受賞者名）
  - `awardTitle`（賞の名称: 優勝・準優勝 等）
  - `compName`（競技会名）
  - `kbnName`（区分名）
  - `awardDate`（授与日）
- **印刷設定**: 部数は `DSPrt.json` で管理。通常 1 部（1 人 = 1 枚）

### ジョブ送信方式（1 組 1 ジョブ）

決勝 6 組の場合、サーバーは **6 ジョブを順次送信**する。ペアダンスで 1 人 1 枚が必要な場合は
`copies: 2` を指定するか、または `recipientName` をリーダー / パートナーに分けて 2 ジョブを送る。

```
サーバー（ボタン押下）
  → PR_PRINT { jobId:"20001_AWARD_001", ..., recipientName:"田中 太郎 / 山田 花子", awardTitle:"優勝" }
  → PR_PRINT { jobId:"20001_AWARD_002", ..., recipientName:"鈴木 次郎 / 佐藤 美花", awardTitle:"準優勝" }
  → PR_PRINT { jobId:"20001_AWARD_003", ... }
  ...（6 ジョブ）

DSPrt
  → 各ジョブを PrintJobQueue に順次投入
  → 1 枚ずつ印刷（AWARD_CERTIFICATE プリンターへ）
  → PR_DONE を jobId ごとに返信
```

### 再印刷（個別指定）

DSPrt の **ジョブログタブ**から特定ジョブを選択して再印刷できる。
例：12 枚中 2 枚だけ再印刷したい場合 → ジョブログで該当 2 件を選択 → 「再印刷」ボタン。

- 再印刷は **サーバーへの通知不要**（DSPrt 内で完結）
- 再印刷ジョブは元の `jobId` に **`_R`** サフィックスを付けて区別（例: `20001_AWARD_001_R`）
- ジョブログは直近 **200 件**保持（通常帳票 + 表彰状を想定して拡張）

### PR_PRINT 電文例（表彰状）

```json
{
  "jobId":    "20001_AWARD_001",
  "layoutId": "AWARD_CERTIFICATE",
  "copies":   1,
  "priority": 1,
  "data": {
    "recipientName": "田中 太郎 / 山田 花子",
    "awardTitle":    "優勝",
    "compName":      "第〇〇回 全日本ダンス選手権大会",
    "kbnName":       "スタンダード",
    "awardDate":     "令和〇年〇月〇日"
  }
}
```

---

## 10. 接続管理・再接続・起動オプション

### WebSocket 切断時の自動リトライ（クライアント側）

DSPrt は切断を検知した場合、**10 秒間隔で自動再接続を試みる**。

```csharp
// WebSocketClient.cs に追加
private async Task ReconnectLoop()
{
    while (!_isDisposed)
    {
        await Task.Delay(10_000, _cancellationToken);  // 10 秒待機
        if (!IsConnected)
        {
            LogAdd("再接続試行中...");
            await ConnectAsync(_lastUri);              // 前回接続先へ再試行
        }
    }
}
```

- 再接続成功後は `PR_LOGIN` を再送し、`PR_ANS_DA` / `PR_ANS_DS` を再受信してキャッシュを復元する
- 再接続中は UI のステータスバーに「● 再接続中（残 N 秒）」を表示する
- ユーザーが「切断」ボタンを押した場合は自動リトライを停止する

### サーバー側：DSPrt 未接続時の動作

- サーバーが `PR_PRINT` を送信しようとしたとき DSPrt セッションが存在しない場合は **エラーをログに記録し、オペレーター画面にエラー表示**を行う
- 「DSPrt が接続されていません」のアラートを GM クライアントに通知する（`GM_ERR_PRT_OFFLINE` 等）

### 競技会選択ダイアログ

`DSPrt.json` の `OrgCd` に対応する競技会がサーバーに複数ある場合、ログイン直後に選択ダイアログを表示する。
DSDsp の `CompetitionSelectDialog` と同一パターンで実装する。

```
PR_LOGIN 送信
  → SVR: PR_ANS_CMP_LIST（競技会リスト）を返す ─── 複数ある場合
  → DSPrt: CompetitionSelectDialog を表示
  → ユーザーが選択
  → PR_SEL_CMP 送信（選択した CmpNo）
  → SVR: PR_ANS_DA / PR_ANS_DS を Push して初期化完了

  → SVR: PR_LOGIN_OK ──────────────────────────── 競技会が 1 件の場合（直接 OK）
  → SVR: PR_ANS_DA / PR_ANS_DS を Push
```

| コマンド（追加） | 方向 | 説明 |
|----------------|------|------|
| `PR_ANS_CMP_LIST` | SVR → PRT | 競技会リスト返却（複数競技会時のみ） |
| `PR_SEL_CMP` | PRT → SVR | 競技会選択通知 |

### DSServer_main からの DSPrt 自動起動

複数の DSPrt インスタンスを自動起動できるよう、設定を **配列**で保持する。

```json
"DSPrtAutoLaunch": [
  {
    "Enabled":       true,
    "ExePath":       "C:\\DScore\\DSPrt\\DSPrt.exe",
    "ConfigPath":    "C:\\DScore\\DSPrt\\DSPrt_Award.json",
    "LaunchDelayMs": 2000
  },
  {
    "Enabled":       true,
    "ExePath":       "C:\\DScore\\DSPrt\\DSPrt.exe",
    "ConfigPath":    "C:\\DScore\\DSPrt\\DSPrt_General.json",
    "LaunchDelayMs": 2000
  }
]
```

- 同一 exe に **異なる設定ファイルを渡す**ことで複数インスタンスを起動する
  - `DSPrt.exe --config DSPrt_Award.json` のように起動引数で設定ファイルを指定
- `Enabled: false` のエントリは起動しない（環境に応じて ON/OFF）
- 同一 `instanceId` の二重起動は **Mutex（`instanceId` をキー）** で防止する（プロセス名ではなく）

```csharp
// DSPrt の App.xaml.cs 起動時
var instanceId = AppSettings.Instance.WebSocketSettings.InstanceId;
var mutex = new Mutex(true, $"DSPrt_{instanceId}", out bool isNew);
if (!isNew)
{
    MessageBox.Show($"DSPrt インスタンス '{instanceId}' はすでに起動しています。");
    Application.Current.Shutdown();
    return;
}

// サーバー側 F010_Main.cs
foreach (var entry in sysConfig.DSPrtAutoLaunch.Where(e => e.Enabled))
{
    await Task.Delay(entry.LaunchDelayMs);
    Process.Start(entry.ExePath, $"--config \"{entry.ConfigPath}\"");
    _log.LogAdd($"DSPrt 自動起動: {entry.ConfigPath}", _log.INFO);
}
```

---

## 11. DSServer_main 側追加実装

### 追加ファイル

| ファイル | 内容 |
|---------|------|
| `Handlers/PR_MessageHandler.cs` | `PR_` プレフィックス処理（サーバー側） |

### F010_Main.cs への追加

```csharp
// 既存パターン踏襲（DC_/DP_/GM_ の後に追加）
else if (command.StartsWith("PR_", StringComparison.Ordinal))
    await _prMessageHandler.HandleAsync(wSEventArgs);
```

### PR_MessageHandler（サーバー側）の責務

- DSPrt セッションを `ConcurrentDictionary<instanceId, sessionId>` で管理
- DSPrt 接続・切断時に `PR_NOTIFY_PRT_LIST` を全 GM クライアントへ Push（接続中インスタンス一覧を通知）
- `GM_PRT_PRINT` 受信時:
  1. `targetInstanceId` が指定されている → 対応セッションへ `PR_PRINT` を転送
  2. `targetInstanceId` が空 かつ DSPrt が 1 台 → 唯一のセッションへ自動転送
  3. `targetInstanceId` が空 かつ DSPrt が 2 台以上 → GM クライアントにエラー返却（宛先未指定エラー）
- DS_Status 系帳票印刷前に `PR_ANS_DS`（最新 DS_Status）を先送りしてから `PR_PRINT` を転送
- `PR_ACK` / `PR_DONE` を受信してオペレーターコンソールの状態表示を更新
- DSPrt が 1 台も接続されていない時は GM クライアントへエラー通知（`GM_ERR_PRT_OFFLINE`）

---

## 12. プロジェクト構成

```
DScore/
└─ DSPrt/
   ├─ DSPrt.csproj                    .NET 8 / WPF (net8.0-windows)
   ├─ DSPrt.json                      アプリ設定（接続先 + 帳票レイアウト設定）
   ├─ App.xaml
   ├─ App.xaml.cs
   ├─ MainWindow.xaml                 接続状態・ジョブログ・設定・デザイナー起動 UI
   ├─ MainWindow.xaml.cs
   ├─ AppSettings.cs                  設定クラス（DSDsp と同パターン）
   ├─ DSPrtClient.cs                  Facade（接続・送受信オーケストレーション）
   ├─ 通信/
   │   └─ WebSocketClient.cs          DSDsp から流用（共通ライブラリ化推奨）
   ├─ Handlers/
   │   └─ PR_MessageHandler.cs        PR_ コマンドハンドラ（クライアント側）
   ├─ 印刷/
   │   ├─ PrintJobQueue.cs            優先度付きジョブキュー
   │   ├─ PrintJobModel.cs            ジョブデータモデル・状態列挙体
   │   ├─ ReportLayoutRegistry.cs     layoutId → frxPath・印刷設定マッピング
   │   ├─ ReportRenderer.cs           FastReport レンダリングラッパー
   │   └─ PrinterController.cs        System.Printing ラッパー
   ├─ Reports/
   │   ├─ [DV_Result 系]
   │   ├─ Result_A4.frx               結果帳票（総合）
   │   ├─ Result_Detail_A4.frx        結果帳票（種目別詳細）
   │   ├─ Award_Certificate.frx       表彰状（デザイナー内蔵で編集可）
   │   ├─ [DA_Master 系]
   │   ├─ EntryList_A4.frx            エントリーリスト
   │   ├─ JudgeList_A4.frx            審判員リスト
   │   ├─ Program_A4.frx              プログラム
   │   ├─ [DS_Status 系]
   │   ├─ HeatList_A4.frx             ヒート組み合わせ表
   │   ├─ Schedule_A4.frx             進行スケジュール表
   │   └─ TestData/
   │       ├─ DA_Master_sample.json
   │       ├─ DS_Status_sample.json
   │       ├─ DV_Result_sample.json
   │       └─ AWARD_CERTIFICATE_sample.json
   └─ 仕様書/
       └─ DSPrt_アーキテクチャ設計書.md  （本ファイル）
```

---

## 13. 設定ファイル構造

### 設定ファイルの分離方針（マルチインスタンス対応）

複数インスタンスを起動する場合は、**インスタンスごとに設定ファイルを分ける**。
起動時に `--config` 引数で読み込むファイルを指定する。

```
DSPrt.exe --config DSPrt_Award.json    ← 表彰状インスタンス
DSPrt.exe --config DSPrt_General.json  ← 通常帳票インスタンス
```

| 設定ファイル | InstanceId | DisplayName | 担当帳票 |
|------------|-----------|-------------|---------|
| `DSPrt_Award.json` | `DSPrt_Award` | 表彰状プリンター | AWARD_CERTIFICATE |
| `DSPrt_General.json` | `DSPrt_General` | 通常帳票プリンター | RESULT_A4, ENTRY_LIST_A4 等 |

> 設定ファイルを分けることで、プリンター・帳票レイアウト・ログ出力先をインスタンスごとに独立させられる。

### DSPrt_Award.json（表彰状インスタンス例）

```json
{
  "WebSocketSettings": {
    "ServerIpAddress":     "10.3.2.108",
    "ServerPort":           7269,
    "InstanceId":          "DSPrt_Award",
    "DisplayName":         "表彰状プリンター",
    "OrgCd":               "JS",
    "ReconnectIntervalMs":  10000,
    "ConnectionTimeoutMs":  30000,
    "AutoReconnect":        true
  },
  "LogSettings": {
    "LogLevel": 3,
    "LogPath":  "./Logs"
  },
  "PrintSettings": {
    "DefaultPrinterName":  "Canon TR8600",
    "AwardPrinterName":    "Canon TR8600 Award",
    "SpoolDirectory":      "./Spool",
    "MaxQueueSize":         50,
    "JobLogMaxCount":       200
  },
  "Layouts": [
    {
      "layoutId":    "RESULT_A4",
      "frxPath":     "./Reports/Result_A4.frx",
      "dataType":    "DV_Result",
      "printerName": "Canon TR8600",
      "copies":       2,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "RESULT_DETAIL_A4",
      "frxPath":     "./Reports/Result_Detail_A4.frx",
      "dataType":    "DV_Result",
      "printerName": "Canon TR8600",
      "copies":       2,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "AWARD_CERTIFICATE",
      "frxPath":     "./Reports/Award_Certificate.frx",
      "dataType":    "DV_Result",
      "printerName": "Canon TR8600",
      "copies":       1,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "ENTRY_LIST_A4",
      "frxPath":     "./Reports/EntryList_A4.frx",
      "dataType":    "DA_Master",
      "printerName": "Canon TR8600",
      "copies":       1,
      "duplex":       "TwoSidedLongEdge",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "JUDGE_LIST_A4",
      "frxPath":     "./Reports/JudgeList_A4.frx",
      "dataType":    "DA_Master",
      "printerName": "Canon TR8600",
      "copies":       1,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "HEAT_LIST_A4",
      "frxPath":     "./Reports/HeatList_A4.frx",
      "dataType":    "DS_Status",
      "printerName": "Canon TR8600",
      "copies":       1,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    },
    {
      "layoutId":    "SCHEDULE_A4",
      "frxPath":     "./Reports/Schedule_A4.frx",
      "dataType":    "DS_Status",
      "printerName": "Canon TR8600",
      "copies":       1,
      "duplex":       "OneSided",
      "paperSize":    "A4"
    }
  ]
}
```

**`WebSocketSettings` フィールド説明（追加分）:**

| フィールド | 型 | 説明 |
|-----------|---|------|
| `InstanceId` | string | インスタンスを一意に識別する ID。電文の `From` フィールドに使用。Mutex のキー |
| `DisplayName` | string | GM クライアントの DSPrt 選択ダイアログに表示する名称（例: "表彰状プリンター"） |

**`Layouts` フィールド説明:**

| フィールド | 型 | 説明 |
|-----------|---|------|
| `layoutId` | string | 電文 `GM_PRT_PRINT.layoutId` と一致するキー |
| `frxPath` | string | FastReport レイアウトファイルのパス（実行ファイルからの相対パス） |
| `dataType` | string | 期待するデータ種別: `"DA_Master"` / `"DS_Status"` / `"DV_Result"` |
| `printerName` | string | 使用プリンター名（Windows のプリンター名と一致させる） |
| `copies` | int | 印刷部数デフォルト値（電文の `copies` で上書き可） |
| `duplex` | string | 両面印刷: `"OneSided"` / `"TwoSidedLongEdge"` / `"TwoSidedShortEdge"` |
| `paperSize` | string | 用紙サイズ: `"A4"` / `"A3"` / `"B5"` 等 |

---

## 14. MainWindow UI 構成（実装済み）

### タイトルバー

ウィンドウタイトルに **`DisplayName`（`InstanceId`）** を表示する。
例: `DSPrt - 表彰状プリンター（DSPrt_Award）`
→ 複数ウィンドウが並んでいてもどちらが何の DSPrt か一目でわかる。

### タブ構成（実装済み）

| タブ | 内容 |
|------|------|
| **ジョブログ** | DataGrid: 受信時刻 / jobId / layoutId / 部数 / 状態 / 完了時刻 / エラーメッセージ。直近 **200 件**保持。**複数選択 → 再印刷ボタン**（完了・エラーのジョブに対し有効）。ログクリアボタン |
| **プレビュー** | ジョブログで選択したジョブの帳票を `HTMLExport` で生成し WPF `WebBrowser` 内に表示。「ブラウザで開く」ボタンで外部ブラウザ起動も可能。プレビュー生成ボタン。**動作確認済み**（選手一覧 199 名表示成功） |
| **帳票設定** | デフォルトプリンター表示。`DSPrt.json` の Layouts 一覧（layoutId / frxPath / データ種別 / プリンター / 部数 / 用紙 / 両面）。**「デザイナーで開く」ボタン**（選択した `.frx` をデフォルトアプリで外部起動）。**「Reports フォルダを開く」ボタン**。**「▶ テスト印刷」「👁 プレビュー確認」ボタン**（サーバー接続不要・テストデータで即座に動作確認可能） |
| **接続ログ** | 通信・処理ログを Consolas フォントで表示。行数制限 500 行。ログクリアボタン |

> **変更点（設計時との差異）**:
> - 「帳票デザイン」タブは `DesignerControl` 非対応により廃止。代わりに「帳票設定」タブにデザイナー外部起動ボタンを統合。
> - 「接続ログ」タブを追加（旧設計では非タブのログエリア）。
> - **テスト印刷・プレビュー機能**を追加（サーバー接続不要で `.frx` テンプレート動作確認が可能）。

### テスト印刷・プレビュー機能（実装済み）

帳票設定タブで帳票を選択し、サーバー接続なしで単独動作テストが可能：

| 機能 | 動作 |
|-----|------|
| **「▶ テスト印刷」ボタン** | テストデータ JSON を選択 → 印刷キューへ投入 → 実プリンターへ送信 |
| **「👁 プレビュー確認」ボタン** | テストデータ JSON を選択 → HTML 生成 → プレビュータブの WebBrowser に表示 |
| **DA_Master 不足時の対応** | data=null かつ DA_Master が必要な帳票の場合、ファイル選択ダイアログで DA_Master.json を読み込み |
| **DS_Status 不足時の対応** | 同様に DS_Status.json を選択可能 |

**実装上の重要修正**（URI エラー対策）:
```csharp
// 相対パス→絶対パス変換が必須（相対パスのまま Uri() に渡すと例外）
string absoluteHtmlPath = System.IO.Path.GetFullPath(htmlPath);
PreviewBrowser.Navigate(new Uri(absoluteHtmlPath, UriKind.Absolute));
```

### ステータスバー（常時表示）

- 接続状態インジケーター（● 緑=接続済み / ● 橙=接続中 / ● グレー=未接続）
- サーバー IP・ポート表示
- **キューサイズ**表示（「キュー: N」）
- 接続・切断ボタン

---

## 15. 帳票一覧

### 15-1. 帳票種別の分類

帳票は出力形式によって **3 種別** に分類する。DSPrt が担当するのは「印刷帳票」と「PDF 出力」のみ。

| 種別 | 出力形式 | DSPrt の担当 | 備考 |
|------|---------|-------------|------|
| **印刷帳票** | FastReport (.frx) → プリンター | ✅ 担当 | 帳票レイアウトを .frx で管理 |
| **PDF 出力** | FastReport (.frx) → PDF ファイル | ✅ 担当 | 印刷の代わりに PDF 保存 |
| **CSV 出力** | テキストファイル | ❌ 担当外 | DSServer_main 側で生成・保存 |

> CSV 帳票は DSPrt を経由せず、DSServer_main が直接ファイル出力する。
> ただし「CSV を印刷したい」場合は、DSPrt が CSV を受け取って簡易レイアウトで印刷することも可能（要検討）。

### 15-2. 帳票一覧とデータ種別マッピング

| # | 帳票名 | 説明 | 出力形式 | データ種別 | layoutId（案） |
|---|--------|------|---------|-----------|--------------|
| 1 | 競技区分内容 一覧表 | 開催区分を一覧で印刷 | 印刷 | DA_Master | `KBNLIST_A4` |
| 2 | ヒート・アップ数一覧表 | 区分毎の各予選のヒート数・UP予定数 | 印刷 | DA_Master + DS_Status | `HEAT_UP_LIST_A4` |
| 3 | 競技結果一覧表 | 区分毎の各予選のヒート数・UP数の結果 | 印刷 | DS_Status + DV_Result | `RESULT_LIST_A4` |
| 4 | 競技番号一覧表 | 区分毎の各予選の競技番号 | 印刷 | DA_Master + DS_Status | `PROG_NUM_LIST_A4` |
| 5 | タイムテーブル | タイムテーブル（進行表） | CSV + 印刷 | DS_Status | `TIMETABLE_A4` |
| 6 | 審判担当競技 | 審判員毎の担当区分タイムテーブル | CSV + 印刷 | DA_Master + DS_Status | `JUDGE_SCHEDULE_A4` |
| 7 | 審判員チーム一覧表 | 審判チーム毎の審判名一覧 | CSV + 印刷 | DA_Master | `JUDGE_TEAM_A4` |
| 8 | 審判員チーム配置一覧表 | 区分毎の各予選の担当審判グループ一覧 | CSV + 印刷 | DA_Master + DS_Status | `JUDGE_ASSIGN_A4` |
| 9 | 選手名簿ファイル | 項目指定 CSV 出力 | **CSV のみ** | DA_Master | — |
| 10 | 名前順リスト | あいうえお順に背番号をリスト | **CSV のみ** | DA_Master | — |
| 11 | 選手一覧表 | 背番号順に選手名と出場区分（所属あり・なし選択可） | 印刷 | DA_Master | `PLAYER_LIST_A4` |
| 12 | 受付用チェックシート | 選手一覧にチェック欄を追加 | 印刷 | DA_Master | `CHECK_SHEET_A4` |
| 13 | 競技実施状況（規則離反項目） | 各区分毎に規程離反があった場合のリスト | 印刷 | DV_Result | `VIOLATION_A4` |
| 14 | 昇級資格報告書 | 本部報告用 | 印刷 | DV_Result | `PROMOTION_REPORT_A4` |
| 15 | 昇級組数確認書 | 掲示用 | 印刷 | DV_Result | `PROMOTION_CONFIRM_A4` |
| 16 | 決勝出場者名簿 | 決勝進出者一覧 | 印刷 | DS_Status + DA_Master | `FINAL_ENTRY_A4` |
| 17 | 審査表 | ジャッジペーパー | 印刷 | DA_Master + DS_Status | `JUDGE_PAPER_A4` |
| 18a | 出場者連絡票（縦）| ヒート毎の出場者リスト（2列段組み・縦A4）| 印刷 | **DA_Master + DS_Status** | `PLAYER_NOTICE_A4` |
| 18b | 出場者連絡票（横）| ヒート毎の出場者リスト（横A4・6ヒート行×20組）司会向け | 印刷 | **DA_Master + DS_Status** | `PLAYER_NOTICE_HORIZONTAL_A4` |
| 19 | 賞状印刷 | 表彰状 | 印刷 | DV_Result | `AWARD_CERTIFICATE` |
| 20 | 決勝入賞者名簿 | 決勝の順位 | 印刷 | DV_Result | `FINAL_RESULT_A4` |
| 21 | 得点一覧表（順位法） | スケーティングシステムの採点結果一覧（総合結果・規定10検討・規定11検討・種目別順位） | 印刷 | DA_Master + DS_Status + DV_Result + Skating_Score | `RANK_SCORE_LIST_A4` |
| 22 | 昇級資格名簿 | 昇級対象者一覧 | 印刷 | DV_Result | `PROMOTION_LIST_A4` |
| 23 | 昇級結果報告書 | 昇級結果の報告 | 印刷 | DV_Result | `PROMOTION_RESULT_REPORT_A4` |
| 24 | 昇級結果一覧 | 昇級結果の一覧 | 印刷 | DV_Result | `PROMOTION_RESULT_LIST_A4` |
| 25 | JDSF公認料納付書 | JDSF向け納付書 | 印刷 | DA_Master | `JDSF_FEE_A4` |
| 26 | システム運用分担金納付書 | 運用分担金納付書 | 印刷 | DA_Master | `SYS_FEE_A4` |
| 27 | カエルシステム納付書 | カエルシステム向け納付書 | 印刷 | DA_Master | `KAERUL_FEE_A4` |
| 28 | 開催報告書 | 競技会の開催報告 | 印刷 | DA_Master + DV_Result | `EVENT_REPORT_A4` |
| 29 | メディカルサポート実態調査票 | メディカルサポート調査 | 印刷 | DA_Master | `MEDICAL_A4` |
| 30 | 競技結果一覧 | 全区分の競技結果一覧 | 印刷 | DV_Result | `COMP_RESULT_A4` |

### 15-3. 複合データ種別帳票の扱い

`DA_Master + DS_Status` のように複数データ種別を必要とする帳票は、DSPrt の `ReportRenderer` が
**キャッシュ済みの DA_Master と DS_Status を組み合わせてバインドする**。
`PR_PRINT` 電文の `data` は空 `{}` で送信し、DSPrt が内部で結合する。

```
例: ヒート・アップ数一覧表（DA_Master + DS_Status）
  PR_PRINT { layoutId: "HEAT_UP_LIST_A4", data: {} }
  → DSPrt: DataManager.DA_Master + DataManager.DS_Status を組み合わせて .frx にバインド
```

> `DS_Status + DV_Result` の帳票（競技結果一覧表 #3 等）は、DS_Status をキャッシュから取得し、
> DV_Result を `PR_PRINT.data` で受け取る。

#### PLAYER_NOTICE_HORIZONTAL_A4（出場者連絡票・横向き）のバインド方式

`dataType = "DA_Master+DS_Status+Horizontal"` として登録。
`ReportRenderer` の `BindPlayerNoticeHorizontal` メソッドが処理し、**テンプレート frx（`ヒート表_横.frx`）は既存のまま流用**する。
`PR_PRINT.data` には対象を絞り込むキーのみを含める：

```json
{
  "jobId": "20001_NOTICE_20250720154512_000",
  "layoutId": "PLAYER_NOTICE_HORIZONTAL_A4",
  "copies": 1,
  "priority": 2,
  "data": {
    "KbnNo":  "1",
    "RndNo":  "1",
    "DGrpNo": ""
  }
}
```

| data フィールド | 型 | 説明 |
|----------------|---|------|
| `KbnNo` | string | 区分番号（DA_Master の `DB_KbnNo`）|
| `RndNo` | string | ラウンド番号（DA_Master の `DC_RndNo`）|
| `DGrpNo` | string | 種目グループ番号（省略時 or "" → 最初のグループ）|

**バインドロジック概要（`BindPlayerNoticeHorizontal`）:**

1. `DS_Status.DS_FLOORs[].DS_PRGRSs[]` を `KbnNo` / `RndNo` / `DGrpNo` で絞り込み、対象ラウンドを取得
2. `DA_Master.DB_KUBUNs[KbnNo].DC_ROUNDs[RndNo]` から区分名・ラウンド名・種目グループ・種目コード・採点方式・UP予定数を取得
3. `DS_PRGDANCEs[].DS_PRGHEATs[]` から全ヒートの HeatId→HeatNo マップを構築し、最大ヒート数を算出
4. `PlayerAssignments[].AssignedHeatIds[0]` でヒート番号を解決し、`DA_Master.DM_MEMBERs` で選手名（L選手苗字）を解決
5. 1ヒートの出場者が20組を超える場合は2行に分割（最大6行）
6. `report.FindObject("TextObject名")` でオブジェクトを取得し、`Text` を直接上書き（`DataBand` は使用しない）
7. 未使用ヒート行（Table4〜Table9）は `Visible = false` で非表示

**frx TextObject 名とデータの対応:**

| frx オブジェクト名 | 設定内容 |
|-------------------|---------|
| `Title` | `"出場者連絡票"` |
| `SendTo` | `"【　司会　】"` |
| `PRGNO` | 進行番号（**3桁ゼロ埋め**。枝番付き: `"001"` / `"003-2"`）|
| `KubunName` | `区分番号（**2桁ゼロ埋め**） 区分コード 区分名 [種目グループ名] [フロア名フロア]` |
| `Round1`〜`Round7` | ラウンド名。現在ラウンドは背景色 DarkOrange |
| `TotalHeat` | `"{n} Heat"` |
| `TotalComp` | `"出場　{n}組"` |
| `UP` | `"UP数 {n} 組"` （シャッフル時は末尾に `" シャッフル"` を付加）|
| `ScoreMethod` | 採点方式名（`DC_RndScrMtd`）|
| `DS1`〜`DS5` | 種目コード（`DE_DncCd`）。**この帳票に含まれる種目のセルは背景色 DarkOrange**、含まれない（空欄）は Transparent |
| `DC1`〜`DC5` | SG種別（`DE_DncSG`）。**DS と同様に含まれる種目のセルは背景色 DarkOrange**、含まれない（空欄）は Transparent |
| `HeatNo1`〜`HeatNo6` | ヒート番号 |
| `No01_01`〜`No06_20` | 背番号（対応するヒート行の出場選手）|
| `Name01_01`〜`Name06_20` | L選手の苗字 |
| `Table4`〜`Table9` | ヒート行テーブル（未使用は `Visible=false`）|

### 15-4. 実装優先順位（案）

フェーズごとに着手する帳票の目安。詳細は別途指示にて確定する。

| 優先度 | 帳票 | 理由 |
|--------|------|------|
| **高**（Phase 2-3 で実装） | 選手一覧表・審査表・決勝入賞者名簿・賞状印刷 | 競技会当日の核心業務 |
| **中**（Phase 4-5 で実装） | ヒート・アップ数一覧表・競技番号一覧表・タイムテーブル・決勝出場者名簿 | 進行管理に必要 |
| **低**（後続フェーズ） | 各種納付書・報告書・メディカル票 | 競技後処理・事務系 |
| **CSV のみ（DSPrt 対象外）** | 選手名簿ファイル・名前順リスト | DSServer_main 側で対応 |

### 15-5. 未確認事項

以下の帳票は内容・データ種別・フォーマットが未確定。実装前に個別確認が必要。

| 帳票名 | 未確認内容 |
|--------|-----------|
| 審査表（ジャッジペーパー） | AJS31 採点方式固有のフォーマットか、汎用か |
| 昇級資格報告書 / 昇級組数確認書 | 昇級判定ロジックの結果をどの JSON フィールドから取得するか |
| スケート結果 | **実装済み**（§15-3 参照）。layoutId=`RANK_SCORE_LIST_A4`、frx=`得点一覧表_順位法_横.frx`、dataType=`DA_Master+DS_Status+DV_Result+Skating_Score`。 |
| 各種納付書（#25〜#27） | 金額・口座情報の出所（DA_Master に含まれるか、別設定か） |
| 出場者連絡票（縦）| ~~連絡情報（メール・電話等）は DA_Master の `DM_MEMBERs` に含まれるか~~ → **サンプル帳票確認済み**。ヒート毎の出場者リスト。DS_Status からヒート割り当てを取得し DA_Master で選手名を解決する。dataType=`DA_Master+DS_Status` に確定 |
| 出場者連絡票（横）| **実装済み**（§15-3 参照）。layoutId=`PLAYER_NOTICE_HORIZONTAL_A4`、frx=`ヒート表_横.frx`、dataType=`DA_Master+DS_Status+Horizontal`。data フィールドに `KbnNo`/`RndNo`/`DGrpNo` を指定。 |

#### RANK_SCORE_LIST_A4（得点一覧表・順位法）のバインド方式

`layoutId = "RANK_SCORE_LIST_A4"`、`dataType = "DA_Master+DS_Status+DV_Result+Skating_Score"` として登録。
`ReportRenderer` の `BindSkatingScoreList` メソッドが処理し、`得点一覧表_順位法_横.frx` を使用する。
`PR_PRINT.data` には対象ラウンドの `DV_Result` JSON（`DV_Result_J` クラスから生成）を含める。

**帳票構成（ページ）:**

| ページ | 内容 | frx ページ | オブジェクトサフィックス |
|--------|------|-----------|------------------------|
| Page1  | 総合結果（種目別順位・合計点・総合順位・決定規定）＋規定10検討表（全選手）＋規定11検討表（対象者のみ最大6行） | Page1 固定 | `_P1` |
| Page2  | 種目1の種目別順位（ジャッジ順位・規定5〜8・判定テキスト） | Page2 テンプレート | `_P2` |
| Page3〜 | 種目2以降の種目別順位（種目数-1 ページ動的複製） | Page2 を複製 | `_P03`/`_P04`... |

**TextObject 命名規則（フォーマット）:**

| オブジェクト名パターン | 内容 |
|----------------------|------|
| `PRGNO_P1`/`PRGNO_P2`/`PRGNO_P03`... | 進行番号（各ページヘッダー） |
| `KubunName_P1`/`KubunName_P2`... | 区分名（各ページヘッダー） |
| `SR_{nn}_C00`〜`SR_{nn}_C10` | 総合結果：背番号・種目別順位（10種目まで） |
| `SR_{nn}_C11`/`C12`/`C13`/`C14` | 総合結果：合計点・総合順位・決定規定・決定値 |
| `R10_{nn}_C00`〜`R10_{nn}_C07` | 規定10検討：背番号・上位合計データ（1〜1-6）・判定順位（全選手、最大20行） |
| `R11_{nn}_C00`〜`R11_{nn}_C07` | 規定11検討：背番号・上位合計データ（1〜1-6）・判定順位（対象者のみ、最大6行） |
| `SK_{nn}_C00_P2`/`_P03`... | 種目別：背番号（P2〜） |
| `SK_{nn}_J01_P2`〜`SK_{nn}_J18_P2` | 種目別：各ジャッジ付与順位（最大18ジャッジ対応） |
| `SK_{nn}_R5_P2`〜`SK_{nn}_R8_P2` | 種目別：規定5〜8の値（確定規程に応じて黄色ハイライト） |
| `SK_{nn}_Jdg_P2` | 種目別：判定テキスト（確定規程名） |
| `SK_{nn}_Rank_P2` / `SK_{nn}_Rank2_P2` | 種目別：判定順位 |

**ページ動的複製（`DuplicateSkatingDancePagesInReportXml`）:**

- frx の Page2 を種目数分複製（種目が2以上の場合）
- 複製時に `_P2` サフィックスを `_P03`/`_P04`... に置換
- 種目1ページ（Page2）のオブジェクトはサフィックス `_P2`（frx テンプレートのまま）

**DV_Result JSON の必要フィールド（順位法・実際のDB出力形式）:**

```json
{
  "区分番号": "1", "ラウンド番号": "1", "区分名": "...", "ラウンド名": "...", "採点方式名": "順位法",
  "総合結果": [
    {
      "背番号": "203", "総合順位番号": 1, "総合得点": 14,
      "総合順位表記": "1位",
      "総合順位決定規定": "規定9",
      "総合順位決定値": "14"
    }, ...
  ],
  "総合規定10検討": [
    { "背番号": "203", "判定順位": null, "列データ": [...] }, ...
  ],
  "総合規定11検討": [
    {
      "背番号": "209", "判定順位": 2,
      "列データ": [{ "上位合計順位まで": "1&2", "合計数": 3 }, ...]
    }, ...
  ],
  "種目結果": [
    {
      "種目記号": "W", "種目名": "Waltz", "種目順": 1, "有効ジャッジ数": 9,
      "選手結果": [
        {
          "背番号": "257", "種目順位番号": 1, "種目順位表記": "1位",
          "ジャッジ詳細結果": [{ "ジャッジ記号": "A", "素点": 0, "順位点": 1.0 }, ...],
          "順位法詳細": {
            "確定規程": "規定5",
            "規定5_過半数順位": 1,
            "規定6_過半数以上の数": 5,
            "規定7a_過半数以上の合計": 3.0,
            "規定7b_過半数より下の合計": 8.0
          }
        }, ...
      ]
    }, ...
  ]
}
```

> **互換性（新旧フォーマット対応）**:
> - `ReportRenderer` は新形式（`確定規程`/`規定5_過半数順位`等）と旧形式（`規定5適用`/`規定5過半数`等）の両方に対応。
> - **古いデータ向けフォールバック**: `総合規定10検討` が空の場合、`種目結果` の各選手の種目順位から規定9〜11・規定10/11検討を `RecalcOverallSkating` メソッドでリアルタイム再計算する。
> - **ジャッジ付与順位**: 新形式は `順位点`、旧形式（テストデータ）は `素点` にジャッジ付与順位が格納される。
> - **最大ジャッジ数**: `MaxJudges = 18`（frx の `SK_H_J01_P2`〜`SK_H_J18_P2`、データ行 `SK_{nn}_J01_P2`〜`SK_{nn}_J18_P2`）。
> - **DBキー名の注意**: 実DBに格納される `順位法詳細` のキー名は「**規程**（ていけい）」（例: `確定規程`、`規程5_過半数順位`）。仕様書・新フォーマットでは「**規定**（きてい）」表記を使用。`ReportRenderer` は両方のキーにフォールバック対応済み。

---

#### スケーティングシステム 総合順位決定規定（規定9〜11）

参照: [JDSF スケーティングルール5-1](https://kyougi.jdsf.or.jp/SkatingRule5-1.pdf)

**種目別順位の確定（規定5〜8）**: 各種目で各ジャッジが付けた順位（付与順位）から、過半数原則に基づいて種目順位を確定する。

| 規定 | 適用条件 | 内容 |
|------|----------|------|
| 規定5 | 常に最初に評価 | 過半数のジャッジが同一順位以上を付けた「過半数順位」を算出 |
| 規定6 | 規定5で同点選手が複数 | 過半数以上の票数が多い方を上位 |
| 規定7(a) | 規定6でも同点 | 過半数以上の付与順位の合計が小さい方を上位 |
| 規定7(b) | 規定7(a)でも同点 | 過半数より下の付与順位の合計が小さい方を上位 |
| 規定8 | 規定7(b)でも同点 | 繰り上げ（次位の票数・合計を比較） |

**総合順位の確定（規定9〜11）**: 各選手の種目別順位番号（1位=1, 2位=2, ...）を使って総合順位を決定する。

| 規定 | 適用条件 | 内容 | 計算 |
|------|----------|------|------|
| **規定9** | 常に最初に評価 | 全種目の種目順位番号の**合計点**（小さいほど上位） | `Σ 種目順位番号` |
| **規定10** | 規定9で合計点が同じ選手が複数 | 1位以内・1&2位以内・... の種目数を順に比較（多い方が上位） | `count(種目順位 ≤ N)` を N=1,2,... で順番に比較 |
| **規定11** | 規定10でも区別できない | **再スケーティング**を実施して順位を確定 | — |

**「決定規定」列の表示ルール**:

| 表示 | 意味 |
|------|------|
| `規定9` | その選手の合計点が前グループと異なり、規定9で単独確定 |
| `規定10` | 合計点が同じ選手との間で規定10（票数比較）で確定 |
| `同順位` | 規定9・規定10すべて同点 → 規定11（再スケーティング）対象 |

> **重要**: 「規定11」は再スケーティングであり、「同順位」グループ**全員**が対象。帳票では「決定規定」列に「同順位」と表示し、規定11検討テーブル（3つ目のテーブル）に当該選手の検討データを掲載する。「規定9」で確定した選手が他選手と合計点が異なっていても、規定10の列で比較されるだけであり、規定11（再スケーティング）の対象にはならない。

**具体例**（種目数=2、選手7名、T・Q種目）:

| 背番号 | T順位 | Q順位 | 合計(規定9) | 規定10: 1位 | 規定10: 1&2位 | 決定規定 | 総合順位 |
|--------|-------|-------|-----------|-----------|-------------|---------|---------|
| 23 | 3 | 1 | **4** | 1 | 1 | 同順位 | 1位 |
| 36 | 1 | 3 | **4** | 1 | 1 | 同順位 | 1位 |
| 51 | 2 | 4 | 6 | — | — | 規定9 | 3位 |
| 68 | 4 | 5 | 9 | — | — | 規定9 | 4位 |
| 5 | 6 | 2 | 8 | — | — | 規定9 | 5位 |
| 73 | 5 | 6 | 11 | — | — | 規定9 | 6位 |
| 67 | 7 | 7 | 14 | — | — | 規定9 | 7位 |

→ 23番・36番は合計4点で規定10も同点のため「同順位」→ **規定11（再スケーティング）**対象
→ 5番(合計8)・73番(合計11)は合計点が異なるため各々**規定9で単独確定**

**`RecalcOverallSkating` フォールバック計算の実装ポイント**（`DV_Result` に `総合規定10検討` が空の場合）:

```
1. 各選手の種目順位番号リストを収集
2. 規定9 = 合計点 (sum of ranks)
3. 規定10比較リスト = [count(rank ≤ 1), count(rank ≤ 2), ..., count(rank ≤ N)]
4. ソート: 合計点昇順 → 規定10リスト辞書式降順 → 背番号昇順
5. グループ検出: 合計点・規定10リストが全一致 → 「同順位」グループ
6. 規定11テーブル = 「同順位」グループ全員（再スケーティング対象）
7. 規定10テーブル = 全選手
```

---

## 16. 主要 NuGet パッケージ（実装済み・確定）

| パッケージ | バージョン | 用途 | 備考 |
|-----------|-----------|------|------|
| `FastReport.OpenSource` | **2025.1.0** | 帳票エンジン（.frx 読み込み・レンダリング・ImageExport・HTMLExport） | 2024.4.0 は存在しないため 2025.1.0 に解決済み |
| `FastReport.OpenSource.Export.Pdf` | — | **存在しない**（NuGet に未公開） | PDF 出力不要。HTMLExport で代替 |
| `Newtonsoft.Json` | **13.0.3** | JSON パース（JToken / JObject / JArray による DataSet 変換） | DSDsp と同バージョンに統一 |
| `System.Drawing.Common` | **8.0.8** | `Bitmap`・`PrintDocument` による印刷送信 | FastReport の Print() 非搭載のため必須 |
| `System.Printing`（WPF 標準） | .NET 8 付属 | プリンター存在確認・一覧取得・デフォルトプリンター取得 | `LocalPrintServer` を使用 |

> **セキュリティ注意**: パッケージ追加時は最新安定版を使用し、既知の脆弱性（CVSS ≥ 7.0）がないことを確認すること。
> `System.Drawing.Common 8.0.8` は .NET 8 向けの最新安定版（2024年時点）。

---

## 17. 実装フェーズ計画（完了状況）

| Phase | 内容 | 状態 |
|-------|------|------|
| **Phase 1** 通信基盤 | `WebSocketClient`（10秒自動リトライ付き）、`PR_MessageHandler`・`DSPrtClient` 実装、競技会選択ダイアログ、設定ファイル分離（`--config` 引数）、Mutex 二重起動防止 | **✅ 完了** |
| **Phase 2/3** 帳票・印刷 | FastReport 2025.1.0 導入、`ReportRenderer`（PNG変換方式）・`PrinterController`・`ReportLayoutRegistry`・`PrintJobQueue`・`PrintService` 実装、`.frx` テンプレート作成、`PR_ACK`/`PR_DONE` 送信 | **✅ 完了** |
| **Phase 4** デザイナー連携 | `DesignerControl` 内蔵断念→「デザイナーで開く」外部起動ボタン実装、「Reports フォルダを開く」ボタン実装 | **✅ 完了**（方式変更） |
| **Phase 5** UI 整備 | `MainWindow` 全タブ実装（ジョブログ DataGrid・再印刷・プレビュー WebBrowser・帳票設定・接続ログ）、PR_DONE 自動送信、キューサイズ表示 | **✅ 完了** |
| **Phase 6** テスト・トラブルシュート | テスト印刷機能（WPF PrintDialog プリンター選択）、FastReport DataTable バインディング問題解決、WebMode 切り替え、URI エラー修正、プレビュー動作確認 | **✅ 完了** |
| **Phase 7** 印刷品質改善・出場者連絡票 | PNG 変換方式を廃止し `PreparedPages.GetPage(i).Draw(FRPaintEventArgs)` による直接描画方式に移行（高品質・高速）。`BindPlayerNoticeHorizontal` 実装（`ヒート表_横.frx` 流用）、テストデータ 3 件作成。 | **✅ 完了**（2026-07-30） |
| **Phase 8** 得点一覧表（順位法） | スケーティングシステム（順位法）用の得点一覧表を実装。`BindSkatingScoreList` 実装（`得点一覧表_順位法_横.frx` 新規作成）。総合結果（規定9〜11）・規定11検討（同一Page1下段）・種目別順位（規定5〜8）の構成。`DuplicateSkatingDancePagesInReportXml` で種目数分のPage2複製。テストデータ 1 件作成。 | **✅ 完了**（2026-08-01） |
| **Phase 8 継続** データ構造整合・表記統一 | `DV_Result_J` に `総合規定10検討`/`総合規定11検討`・`DV_規定検討_J`/`DV_規定検討列データ_J` クラス追加。`DV_総合結果_J` に `総合順位決定規定`/`総合順位決定値` 追加。`DV_種目結果_J` に `種目名`/`有効ジャッジ数` 追加。`SkatingMethodAggregator` に決定規定判定・規定10/11検討生成・種目名取得・確定規定グループロジック追加。`ReportRenderer` を新旧両フォーマット対応・PairsPerPage=20・R10/R11分離に修正。frx を A4縦ヘッダー・規定10全選手20行・規定11対象者6行・種目別下表幅拡張に再設計。「規程」→「規定」表記をAggregatorsのJSON出力・ReportRenderer・設計書で統一。 | **✅ 完了**（2026-08-02） |
| **Phase 8 拡張** ジャッジ列18対応・フォールバック | `ReportRenderer.cs` の `MaxJudges` を 13→18 に拡張（`BindSkatingScoreList`・`SetSkatingDanceRows` の2箇所）。`得点一覧表_順位法_横.frx` の Page2 上段をジャッジ列 J01〜J18（各幅20pt・20間隔）、判定順位列 Left=447.8 に再設計。古いデータ（`総合規定10検討` が空）向けに `RecalcOverallSkating` フォールバックメソッドを追加（種目結果から規定9〜11・規定10/11検討をリアルタイム再計算）。 | **✅ 完了**（2026-08-03） |
| **今後** DSServer_main 連携 | `PR_MessageHandler`（サーバー側）・`GM_PRT_PRINT` ルーティング・`F010_Main.cs` への追加 | 未着手 |
| **今後** `.frx` 帳票デザイン | 30 帳票分の `.frx` テンプレート作成（現在 6 枚: PlayerList_A4, FinalResult_A4, Award_Certificate, PlayerNotice_A4, ヒート表_横, 得点一覧表_順位法_横） | 未着手（優先度順に着手） |

### Phase 6 で解決した主要な問題

| 問題 | 原因 | 解決策 | ファイル |
|------|------|--------|---------|
| `[DM_Masters.DM_No]` 式が CS0103 エラー | `WebMode=true` がスクリプトコンパイルを無効化 | `Prepare()` 前後で `WebMode=false/true` 切り替え | `ReportRenderer.cs` |
| "Table is not connected" エラー | `InitSchema()` が `table = Reference as DataTable` で上書き | `tds.Table` + `tds.Reference` 両方セット | `ReportRenderer.cs` |
| DB 再取得防止 | FastReport が内部で DB 接続を試みる | リフレクションで `tds.IgnoreConnection=true` | `ReportRenderer.cs` |
| "Invalid URI" エラー | 相対パスを `Uri(, Absolute)` に渡した | `Path.GetFullPath()` で絶対パス化 | `MainWindow.xaml.cs` |

---

## 18. 印刷フロー（実装確定版）

```
① WebSocket 受信
   WebSocketClient → PR_MessageHandler.HandleMessageAsync()

② PR_PRINT 解析
   JSON Body → PR_PRINT_Request オブジェクト

③ PR_ACK 返信（即座に）
   PR_ACK を DSServer_main へ送信

④ PrintJob 変換・キュー投入
   PR_PRINT_Request → PrintJob に変換
   PrintJobQueue.Enqueue(job) → PriorityQueue へ追加（jobId 重複排除）

⑤ レイアウト解決
   ReportLayoutRegistry.Get(layoutId)
   → .frx パス・プリンター設定取得

⑥ データバインド（実装で確定した方式）
   layout.DataType に応じて:
   - DV_Result: PR_PRINT.data を DataTable に変換 → report.RegisterData()
   - DA_Master: DataManager.DA_Master キャッシュを参照
     → DM_MEMBERs[].DM_MASTERs[] をフラット化して DataTable "DM_Masters" 作成
     → 競技会ヘッダー（CompName/CompDate 等）を report.SetParameterValue()
     → BindTableToReport():
       ・tds.Table = table
       ・tds.Reference = table（InitSchema() 対策）
       ・tds.IgnoreConnection = true（リフレクション、DB 再取得防止）
   - DS_Status: DataManager.DS_Status キャッシュを参照 → 同様に BindTableToReport()
   - DA_Master+DS_Status+Horizontal（出場者連絡票・横）:
     BindPlayerNoticeHorizontal() が担当
     → job.Data の KbnNo/RndNo/DGrpNo で対象ラウンドを特定
     → report.FindObject() で TextObject を直接書き換え（DataBand 不使用）

⑦ 帳票レンダリング（WebMode 切り替え）
   FastReport.Utils.Config.WebMode = false;  // [DataSource.Field] 式のコンパイル有効化
   report.Prepare();  // FastReport がレイアウトとデータを結合
   FastReport.Utils.Config.WebMode = true;   // 復元

⑧ 印刷送信（PrintDirect 方式・Phase 7 で確定）
   System.Drawing.Printing.PrintDocument を作成
   doc.PrintPage イベントで:
     ・PreparedPages.GetPage(i) でページオブジェクト取得
     ・e.Graphics.PageUnit = Millimeter（mm 単位で描画）
     ・page.Width  = page.PaperWidth  * 3.78f（内部px単位に変換）
     ・page.Height = page.PaperHeight * 3.78f
     ・FRPaintEventArgs(g, scaleX=1/3.78, scaleY=1/3.78, cache) を生成
       ※ scaleX = 「内部単位(96dpi px) × scaleX = mm」の変換係数
     ・page.Draw(paintArgs) で PrintDocument の Graphics に直接描画
     ・PNG 変換なし → 高品質・高速
   doc.Print() でプリンタースプーラーへ送出

   ※ FastReport.Utils.Units.Millimeters = 3.78f（1mm = 3.78 内部単位）
   ※ テスト印刷は WPF PrintDialog でプリンターを選択後 PrintService.PrintDirectAsync() を呼ぶ
   ※ 「Microsoft Print to PDF」を選択すれば PDF 出力も可能

⑨ PR_DONE 送信
   PrintDone イベント → MainWindow → DSPrtClient.SendDoneAsync()
   { jobId, status: "done" | "error", message }
```

### プレビュー生成フロー（実装確定版）

```
① 帳票設定タブ「👁 プレビュー確認」または ジョブログタブ「プレビュー生成」ボタン押下

② 同じレンダリングフロー（⑤〜⑦）を実行

③ HTMLExport
   var htmlExport = new HTMLExport
   {
       EmbedPictures = true,    // 画像を埋め込んでシングルファイル化
       SinglePage    = true,    // 全ページを 1 HTML に統合
       Format        = HTMLExportFormat.HTML
   };
   report.Export(htmlExport, filePath);
   → ./Spool/Preview/{jobId}_{timestamp}.html に出力

④ WPF WebBrowser で表示
   string absoluteHtmlPath = Path.GetFullPath(htmlPath);  // ← 相対パス→絶対パス変換必須
   PreviewBrowser.Navigate(new Uri(absoluteHtmlPath, UriKind.Absolute));

⑤ プレビュータブに自動切り替え
   MainTabControl.SelectedIndex = 2;
```

---

## 19. 既知の問題・今後の改善点

### デバッグログの削除（未対応）

`ReportRenderer.cs` に以下のデバッグログが残存（本番運用前に削除が必要）：
- `[FR-DEBUG] after Load: DM_Masters=...`
- `[FR-DEBUG] before Prepare: DM_Masters table=...`

### 残り帳票テンプレート作成

現在実装済みの `.frx` ファイル（5 枚）：
- `PlayerList_A4.frx`（選手一覧表） — **動作確認済み**
- `FinalResult_A4.frx`（決勝結果）
- `Award_Certificate.frx`（表彰状）
- `PlayerNotice_A4.frx`（出場者連絡票・縦）
- `ヒート表_横.frx`（出場者連絡票・横 / `PLAYER_NOTICE_HORIZONTAL_A4`）— **バインドロジック実装済み（§15-3参照）**

未実装帳票（優先度順）：
1. **高優先度**（次フェーズ）: `CHECK_SHEET_A4.frx`（受付チェックシート）、`HEAT_UP_LIST_A4.frx`（ヒート・アップ数一覧表）、`TIMETABLE_A4.frx`（タイムテーブル）、`FINAL_ENTRY_A4.frx`（決勝出場者名簿）
2. **中優先度**: `JUDGE_LIST_A4.frx`（審判員リスト）、`PROG_NUM_LIST_A4.frx`（競技番号一覧表）
3. **低優先度**: 各種納付書・報告書（事務系帳票）

### DSServer_main 側の残タスク

- `Handlers/PR_MessageHandler.cs`（サーバー側）— `PR_` プレフィックス処理
- `GM_MessageHandler.cs` への `GM_PRT_PRINT` コマンド追加
- `F010_Main.cs` への PR_ ルーティング追加
- DSPrt 接続一覧の GM クライアントへの Push（`PR_NOTIFY_PRT_LIST`）
- DSPrt 自動起動機能（`DSPrtAutoLaunch` 配列）

---

*このドキュメントは設計変更があるたびに更新する。*
