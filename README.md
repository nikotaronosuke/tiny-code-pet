# Claude Desktop Pet 🐣

A tiny native Windows desktop pet that shows what Claude Code (and Codex) is doing — working, waiting for you, overall progress, and truthful completion — at a glance.

Claude Code / Codex の状態を、デスクトップ右下の小さなひよこを見るだけで
把握できる **超軽量デスクトップ通知キャラクター** です。
Claude Code と Codex を同時に使っても session / 状態は衝突しません。

| 状態 | 表示 | 意味 |
|---|---|---|
| Idle | 🐣 + `Claude` | 何もしていない。完全静止 |
| Working | 🐣 + 「作業中…」+ project名 | 放置してよい (グレーのピル・静止) |
| Working (Taskあり) | 🐣 + 「作業中…」+ progress bar + 「**全体 推定 N%**」+ project名 | 今投げた依頼全体のおおよその進み具合。Task 件数 (3/5 等) は表示しない |
| Waiting | 🐣 + 「確認して！」+ project名 | **permission承認待ち。見に行く必要あり** (オレンジ吹き出し・軽く2回ピョコ・警告音1回) |
| StoppedIncomplete | 🐣 + 「途中で止まったよ」+ project名 | **未着手 Task を残したまま停止** (ベージュ吹き出し・警告音1回・最大10分表示) |
| Indeterminate | 🐣 + 「終わったか確認してね」+ project名 | Stop したが残 Task が in_progress のみ = **完了とも未完とも断定できない** (薄オレンジ吹き出し・警告音1回・最大10分表示) |
| Completed | 🐣 + 「終わったよ！」+ project名 | **依頼全体が本当に完了** (白吹き出し・3回ピョコピョコ・通知音1回・約5秒後に次の表示へ) |

表示中の session には **provider + model** の 1 行が付く (`Claude · Opus 4.6` /
`Codex · GPT-5.6-codex`)。model を取れないときは provider だけ。
同行の右端の **`+N`** は「他に動いている session 数」で、
Working / Finalizing / Waiting の session だけを数える (0 なら非表示)。

Working 中に最近の実 tool activity を観測すると「**● 活動中**」(緑) が添えられる。
これは進捗率ではなく「Claude が実際に動いている」ことだけを示す
(進捗%が動かない時間でも stuck ではないと分かる)。最後の activity から15秒で自動消灯。

## 主な特徴

- Native Win32 (C# P/Invoke)。**Electron / WebView / Node 常駐 / localhost サーバー / DB 一切なし**
- 完全 event-driven (Claude Code 公式 Hooks 連携)。polling なし。アイドル時は `GetMessage` でブロック
- 背景完全透過・枠なし・タスクバー/Alt+Tab 非表示・常に最前面
- **クリック透過**: キャラの背後にある VS Code や Chrome をそのまま操作できる
- 依頼全体の推定進捗表示 (新 Task システム / TodoWrite の両対応)
- 完了 / 未完 / 判定不能を区別する誠実な完了通知
- 複数セッションの同時追跡 (優先度付き表示)
- 別の Claude セッションのツール内から起動された子 Claude (`claude -p` 等) の通知抑制
- Subagent 完了の誤通知防止
- **Codex 対応** (別 adapter / provider + session + turn で状態分離)

## 軽さ (参考実測値)

開発環境 (Windows 11) での実測。環境により変動するため保証値ではありません。

| 項目 | 実測 |
|---|---|
| 待機時 Private Working Set | 約 13.5 MB (起動直後) 〜 17 MB (多数の進捗描画後の定常値・増加停止確認済み) |
| 待機時 CPU (60秒計測) | ほぼ 0 ms (イベント無し時は完全 0%) |
| 待機時 GPU | 0 (GPU エンジンインスタンス自体が0個) |
| Working / Waiting 表示中 | Idle と同じ (静止ビットマップ、タイマーなし)。イベント到着時だけ瞬間再描画 |
| アニメーション1回の CPU 累計 | 約 15〜50 ms |
| Hook helper 1回 | 約 60〜70 ms で起動〜終了 (async のため Claude Code を待たせない)。残留プロセスなし |
| GDI / USER / ハンドル | 大量イベント後も一定 (リークなし) |

## How it works

```
Claude Code hooks (user-level settings.json / 全て async・fire-and-forget)
  Stop ── UserPromptSubmit ── Notification(matcher=permission_prompt)
  PostToolUse(matcher=*) ── SessionEnd ── TaskCreated ── TaskCompleted
        │  stdin の JSON から status metadata のみ読む
        │  (hook_event_name / session_id / cwd / agent_id / tool_name / task status)
        ▼
ClaudePetNotify.exe   … Hook Adapter。正規化イベントへ変換して即終了
        │  WM_COPYDATA: dwData=イベント種別, payload="session_id\nproject名\nextra"
        ▼
ClaudePet.exe         … 常駐ペット。session_id 単位の状態機械 (依頼=Request 単位で進捗管理)
        ▼
Win32 layered window  … UpdateLayeredWindow で ARGB 描画 (状態変化時のみ)
```

Codex は別の adapter を通る (Claude 側の契約は一切変えていない):

```
Codex Hooks (hooks.json)
  UserPromptSubmit ── PostToolUse(.*) ── PermissionRequest(.*)
  Stop ── SessionEnd ── SubagentStart ── SubagentStop
        │  stdin の JSON から status metadata のみ読む
        │  (hook_event_name / session_id / turn_id / cwd / tool_name / plan[].status)
        ▼
CodexPetNotify.exe    … Codex Hook Adapter。dwData 20〜27 へ変換して即終了
        │  WM_COPYDATA: dwData=20〜27、payload は 4 行
        │  (session_id / project名 / extra / turn_id を改行区切り)
        ▼
ClaudePet.exe         … 同じ常駐ペット。provider + session + turn で状態を分ける
```

- 実装: C# (P/Invoke による純 Win32)。**.NET Framework 4.8 同梱の csc.exe でビルドするため追加インストール不要**
- タイマーはアニメーション・grace・表示期限の one-shot のみ。常時タイマーなし
- ペット未起動時は Stop / UserPromptSubmit / permission_prompt で自動起動 (高頻度な PostToolUse では起動しない)
- **Prompt 本文・応答本文・ソースコードを送信・解析して進捗を推定しているわけではない**。
  扱うのは Hook が配る構造化 status metadata のみ

### 依頼全体の推定進捗

「依頼 (Request)」= そのセッションで最後に `UserPromptSubmit` が来てから Stop までの1ターン。
新しい依頼が始まると前回依頼の進捗はリセットされる。

Claude がタスクリストを使って作業しているときだけ、Working ピルに
progress bar と「**全体 推定 N%**」を表示する。Task 件数 (3/5 等) は UI に出さない。

- **「推定」であって精密な実進捗ではない**。「現在 Claude 自身が認識している Task 群」に対する
  structured status ベースの heuristic
- **ETA ではない**。残り時間の予測は一切しない
- 計算式: `(completed + 0.5 × in_progress) ÷ total × 100` (小数切り捨て)。
  in_progress は「着手済み」として半分だけ加点する
- **Claude が途中でタスクを追加すると全体推定が下がる場合がある** (総仕事量の認識が増えたため。バグではない)
- 100% になっても Stop が来るまでは「作業中…」のまま。Task 完了率と turn 完了は別物
- **タスクリストを使わない依頼では進捗は表示されない**。経過時間やツール実行回数から進捗を捏造することはしない
- 「● 活動中」インジケータは進捗値に一切影響しない

進捗のデータ源は2系統:

1. **TaskCreated / TaskCompleted hook + PostToolUse(TaskUpdate)**: 新 Task システム
   (`TaskCreate` / `TaskUpdate` ツール) のセッション。`task_id` の一意集合 (Set) で管理するため
   重複通知でも二重加算されない (上限 256 件/セッション)。in_progress・削除/キャンセルも反映
2. **TodoWrite スナップショット**: TodoWrite のセッションでは PostToolUse payload の
   `tool_input` 内の `"status"` 値の件数だけを数えて `completed/in_progress/total` を導出する
   (タスク本文は読まない・送らない)。全量スナップショットなので重複発火しても冪等

両方を同一依頼内で観測した場合は TodoWrite スナップショットを優先。

### 完了判定 (Completion semantics)

「終わったよ！」は「**ユーザーが投げた依頼全体が完了した**」ときだけ出す。

Claude は **Stop を受けたら必ず約 2 秒待ち**、遅れて届く structured event が
ないことを確かめてから完了通知する (hook が async なため、Stop より前に
発生した Task/Todo イベントが Stop の後から届くことがある)。
grace 中に関連イベントが来たら、そこからまた 2 秒静かになるまで待つ。

```
Stop 受信
  └─ 常に Finalizing (約2秒の quiet grace。UI は作業中のまま)
        ├─ grace 中の関連イベントで grace を数え直す (100% になってもその場では通知しない)
        └─ grace 満了
              ├─ 全件 completed (または Task の無い依頼) → 終わったよ！
              ├─ 未着手 (pending) Task が残る → 途中で止まったよ (明確に未完)
              └─ 残りが in_progress のみ → 終わったか確認してね (断定しない)
```

- grace window は async hook の到着順ゆれを吸収するための一時 one-shot timer
- **Task の削除/キャンセル (`TaskUpdate` status=deleted/cancelled) には対応する hook が発火しない**
  (実測)。PostToolUse から検知して total から除外する (これを怠ると「完了したのに
  途中で止まったよ」という false incomplete になる)
- Task の無い依頼: 2 秒静穏の間に structured event が一つも来なければ
  Stop を完了として扱う
- 逆に **Todo / Task / update_plan を一度でも使った依頼**では、status を読めなかった
  場合でも「task なし依頼」へ格下げせず、全件 completed を確認できない限り
  「終わったよ！」は出さない
- 同一依頼への重複 Stop / 遅延イベントでは celebration は1回だけ (debounce + tombstone)
- StoppedIncomplete / Indeterminate は次の依頼 (UserPromptSubmit) で解除。終了済み
  セッションが表示を塞ぎ続けないよう最大10分で自動消滅

### Nested 子 Claude の通知抑制

メイン Claude が Bash/PowerShell tool 内から起動した `claude -p` などの子 Claude は、
**プロセス祖先チェーン**で検出して UI 通知を完全に抑制する。

- 判定: hook helper の祖先プロセスに claude 本体が2つ以上あるか
  (1つ目 = hook を発火させた claude 自身、2つ目 = それを起動した親 Claude)
- PID 再利用による誤判定は「親の起動時刻 <= 子の起動時刻」検証で排除
- 手動で開いた別ウィンドウの Claude は祖先に claude がいないため抑制されない

### 複数セッション

内部状態は session_id 単位で分離。表示はペット1匹で、priority は

**Waiting / StoppedIncomplete / Indeterminate > Celebrating > Working > Idle**、
同 priority なら最新イベントのセッション。

例: `project-a` が Waiting、`project-b` が Working なら「確認して！project-a」を表示し、
project-a が解消 (承認して作業再開 or 完了) すると「作業中…project-b」へ自動で戻る。

- セッションテーブルは最大8件。超過時は最古を削除。4時間イベントの無いセッションも削除
- 全て in-memory。永続化なし

### Subagent の扱い

- Claude Code 内部の Subagent 完了は `SubagentStop` という別イベントであり、`Stop` hook は発火しない (実測確認済み)
- 防御として、`Stop` payload に `agent_id` が含まれる場合も通知しない
- `claude -p` 等で明示起動した別プロセスの Claude は独立セッションとして正当に監視される

## Codex 対応

Claude Code と同じペット・同じ UI で Codex の状態も見られる。
Codex 専用の画面は追加していない (Working / 進捗 / 確認して！/
終わったよ！/ 途中で止まったよ をそのまま使う)。

### Setup

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
pwsh -File install-codex-hook.ps1 -DryRun   # まず差分を確認
pwsh -File install-codex-hook.ps1           # 実際に追記
```

1. `install-codex-hook.ps1` は `$CODEX_HOME\hooks.json`
   (既定は `%USERPROFILE%\.codex\hooks.json`) へ hook を **追記** する。
   既存 hook は一切変更しない。イベント単位で冪等。実行前に自動バックアップ。
   `-ProjectPath <dir>` で project 単位 (`<dir>\.codex\hooks.json`) へも入れられる。
2. **`config.toml` はこのスクリプトが書き換えない。**
   `[features] hooks = true` が無ければ必要な 2 行を案内するので自分で追記する。
3. Codex は次回起動時に hooks.json の内容確認 (trust) を求めるので承認する。
   `--dangerously-bypass-hook-trust` は使わない。
4. hook は新しい Codex セッションから有効。

登録される hook:

| イベント | matcher | async | 用途 |
|---|---|---|---|
| `UserPromptSubmit` | なし | sync | 新 turn 登録 / 依頼リセット |
| `PostToolUse` | `.*` | async | activity 表示・`update_plan` 進捗 |
| `PermissionRequest` | `.*` | sync | 確認して！ |
| `Stop` | なし | sync | 完了候補 (5 秒 grace 開始) |
| `SessionEnd` | なし | sync | 後片付け |
| `SubagentStart` / `SubagentStop` | なし | sync | subagent 検知 (完了にはしない) |

`PreToolUse` は登録しない (PostToolUse で十分)。
`PostToolUse` は 1 本だけ登録し、activity と `update_plan` 進捗を同じ helper で処理する
(1 tool あたり helper は 1 回だけ起動する)。
async はあくまで性能最適化であり、sync になっても正しさは壊れない。

### Codex の進捗

- **`update_plan` を使っているときだけ** 進捗を表示する。
  `tool_input.plan` は全量 snapshot なので status の件数だけを数える
  (plan の step 本文は読まない)。
- plan がない依頼では **% を捏造しない** (進捗非表示)。
  snapshot を取れなかった hook も同じで、推測で埋めず次の snapshot で自己修復する。
- 計算式は Claude と同じ `(completed + 0.5 × in_progress) ÷ total`。

### Codex の完了判定

- **Codex の `Stop` は完了確定ではない。** 別の hook が continuation を返すと
  同じ turn のまま作業が続き、もう一度 `Stop` が来る (実測)。
  よって `Stop` は「完了候補」として扱い、**静穏 5 秒**で確定する。
  grace 中に同じ turn の作業イベントが来たら候補を破棄し、
  2 回目の `Stop` なら 5 秒を最初から数え直す。
  (Claude 側の約 2 秒 Finalizing とは別理由・別定数。互いに影響しない)
- **interrupt (途中停止) では完了通知を出さない。**
  interrupt では `Stop` も `SessionEnd` も発火しない (実測) ので、
  完了候補自体が作られず「終わったよ！」の誤通知は構造的に起きない。
  代わりに「作業中…」表示が残る。推測 timeout で完了扱いにはしない。
  次の依頼 (UserPromptSubmit) で新 turn としてリセットされる。
- interrupt 後に古い `PostToolUse` が **約 18.6 秒遅れて** 届いた実測があるため、
  内部状態は provider + session + **turn** で分けている。
  現在 turn 以外の遅延イベントは UI へ反映しない。

### Known limitation: subagent

- `SubagentStart` / `SubagentStop` は公式 schema にはあるが、
  **検証環境で実発火を確認できていない** (現 build で発火しない可能性がある)。
- `PreToolUse` / `PostToolUse` の schema には `agent_id` が無く、
  tool event が root のものか subagent のものかを metadata だけで証明できない。
- よって fail-closed: `SubagentStart` を検知した turn では
  **その turn の進捗を信用せず % を表示しない** (表示中のものも消す)。
  `SubagentStop` を root の完了にはしない。tool activity 表示には使う。
- subagent の進捗のためだけに rollout watcher / App Server 常駐は導入しない。

## Privacy / Security

このツールが扱うのは status metadata のみ:

- Hook の stdin JSON から読むのは `hook_event_name` / `session_id` / `turn_id` / `cwd` /
  `agent_id` / `agent_type` / `tool_name` / `permission_mode` / `stop_hook_active` /
  task ・ plan の status・id のみ
- **Prompt 本文・Claude / Codex の応答本文・ソースコード本文・API キー・secret を
  進捗判定のために収集しない**。タスク本文 (subject/description)、
  Codex の plan step 本文、tool command / response 本文、transcript も読まない
- 保存も送信もしない (ネットワーク通信なし・履歴 DB なし・全て in-memory)

## Requirements

- Windows 10 / 11 (x64)
- .NET Framework 4.8 (Windows 10/11 に標準搭載。追加インストール不要)
- [Claude Code](https://claude.com/claude-code) (Hooks 対応バージョン。v2.1.233 で開発・検証)
- (任意) Codex — Hooks 対応バージョン。VS Code 拡張 26.814.41407 /
  Codex CLI 0.148.0-alpha.15 で仕様を実測して実装

## Build

```powershell
git clone https://github.com/nikotaronosuke/claude-desktop-pet.git
cd claude-desktop-pet
powershell -ExecutionPolicy Bypass -File build.ps1
```

`bin\ClaudePet.exe` (常駐本体)、`bin\ClaudePetNotify.exe` (Claude Hook ヘルパー)、
`bin\CodexPetNotify.exe` (Codex Hook ヘルパー) が生成される。
コンパイルには Windows 標準の `csc.exe` (.NET Framework 4.8 同梱) を使うため、
Visual Studio や .NET SDK は不要。

## Installation

```powershell
pwsh -File install-hook.ps1   # または powershell -File install-hook.ps1
.\bin\ClaudePet.exe           # 常駐開始 (Hook発火時に自動起動もされる)
```

Codex 側の導入は「Codex 対応 › Setup」を参照 (`install-codex-hook.ps1`)。
Claude 側と Codex 側は独立していて、片方だけ入れても動く。

`install-hook.ps1` はユーザーレベル設定 `%USERPROFILE%\.claude\settings.json` に
以下の hook を **追記** する (既存 hooks は一切変更しない。イベント単位で冪等。
実行前に `settings.json.backup-claudepet-<日時>` を自動作成)。

| イベント | matcher | 用途 |
|---|---|---|
| `Stop` | なし | 完了通知 |
| `UserPromptSubmit` | なし | Working 開始 / 依頼リセット |
| `Notification` | `permission_prompt` | 確認して！ (idle_prompt 等は発火させない) |
| `PostToolUse` | `*` | 活動表示・Waiting 解除・Task/Todo 進捗 |
| `SessionStart` | なし | model 表示用 metadata (これだけでは作業中にしない) |
| `SessionEnd` | なし | セッション後片付け |
| `TaskCreated` | なし | Task 進捗 |
| `TaskCompleted` | なし | Task 進捗 |

各エントリは `{"type":"command","command":"<clone先>/bin/ClaudePetNotify.exe","timeout":10,"async":true}`。
`async: true` + 常時 exit 0 のため **Claude Code を一切ブロック・減速させない**。

- ユーザーレベル設定なので全プロジェクトで有効
- Hook は Claude Code セッション開始時に読み込まれるため、**設定後は新しいセッションから有効**

### 操作

```powershell
.\bin\ClaudePet.exe                        # 常駐開始 (二重起動は自動防止)
.\bin\ClaudePetNotify.exe --test myproj    # 完了通知の手動テスト
.\bin\ClaudePetNotify.exe --quit           # 常駐ペットを終了
```

ログイン時に常駐させたい場合は `shell:startup` に ClaudePet.exe のショートカットを置く (任意)。

デバッグ: `bin\debug.flag` という空ファイルを置くと `bin\debug.log` へイベントが記録される
(通常時は完全に無効)。調査後は flag と log を削除すること。

## Uninstallation

1. Hook を外す: `pwsh -File uninstall-hook.ps1`
   (`ClaudePetNotify` を含む hook だけを全イベントから削除。他の設定は無傷。自動バックアップあり)
   Codex を入れていた場合は `pwsh -File uninstall-codex-hook.ps1`
   (`CodexPetNotify` を含む hook だけを削除。`-DryRun` で事前確認可。config.toml は無傷)
2. 常駐を止める: `.\bin\ClaudePetNotify.exe --quit`
3. クローンしたフォルダを削除

Hook を完全に元へ戻すには、自動作成されたバックアップを上書きコピーする:

```powershell
Copy-Item "$env:USERPROFILE\.claude\settings.json.backup-claudepet-<日時>" "$env:USERPROFILE\.claude\settings.json" -Force
```

## Limitations

- 進捗はあくまで heuristic。Claude がタスクリストを整理し直すと数字が前後する
- Task を使わない依頼では Stop ベースの完了判定になる (短い応答でも「終わったよ！」)。
  ただし Claude では遅延 event を待つため通知が約 2 秒遅れる
- 2 秒を超えてから届く Claude の async event までは司れない (bounded)
- 完了判定は Claude 自身の task lifecycle (status 更新) に依存する。grace (約2秒) を超える
  極端な async 遅延では「終わったか確認してね」になることがある
- nested 検出の限界: 子 Claude を起動した中間シェルが先に終了するとチェーンが切れて
  検出できない場合がある。exe 名が claude でない起動形態 (node 経由等) も検出不可
- `Stop` は「応答完了」ごとに発火する仕様のため、会話的なやり取りでも通知される
- permission 待ち (Waiting) の発火は対話セッションのみ
- マルチモニタ: プライマリモニタの右下固定。モニタ構成変更後はペット再起動が必要
- DPI はシステム DPI 基準 (セッション中の DPI 変更には追従しない)
- キャラはコード描画のひよこ (`src/Pet.cs` の `PetRenderer` 差し替えで変更可能)
- **Claude の model 表示は `SessionStart` 経由**なので、`/model` でセッション途中に
  切り替えると、次の startup / resume / clear / compact まで古いままになる。
  transcript 監視や polling を入れてまで追跡しない (model 不明時は provider だけ表示)
- Codex: `SubagentStart` / `SubagentStop` の実発火未確認。subagent を含む turn では
  進捗を表示しない (上記 Known limitation)
- Codex: interrupt では `Stop` が来ないため「作業中…」が残る
  (誤った完了通知を出さないための意図的な振る舞い)
- Codex 側に nested 抑制 (Claude の process ancestor chain 相当) はない

### Technical note: Task 粒度と進捗の滑らかさ

進捗の刻みは Claude が作る Task の数に依存する。実験では「6〜8個のマイルストーン Task を
維持せよ」という指示をプロンプトに付けると進捗が最大7ポイント刻みまで滑らかになったが、
turn 数・実行時間・コストが大きく増えるためツール側では強制していない。
滑らかな進捗が欲しい長時間依頼では、同様の指示を自分のプロンプトに付けることで opt-in できる。

## Development status

Claude Code の状態を手元で確認したくて作った個人用の小さなツールです。
その目的に必要な機能だけを最小構成で実装しており、現時点ではこれで完成としています。
今後は、不具合修正や実際に使って必要になった変更があれば更新する程度の予定です。

## AI-assisted development

This project was developed with AI assistance, including ChatGPT and Claude Code.

## License

[MIT](LICENSE)