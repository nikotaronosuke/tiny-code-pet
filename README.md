# Claude Desktop Pet 🐣

A tiny native Windows desktop pet that shows what Claude Code is doing — working, waiting for you, overall progress, and truthful completion — at a glance.

Claude Code の状態を、デスクトップ右下の小さなひよこを見るだけで把握できる
**超軽量デスクトップ通知キャラクター** です。

| 状態 | 表示 | 意味 |
|---|---|---|
| Idle | 🐣 + `Claude` | 何もしていない。完全静止 |
| Working | 🐣 + 「作業中…」+ project名 | 放置してよい (グレーのピル・静止) |
| Working (Taskあり) | 🐣 + 「作業中…」+ progress bar + 「**全体 推定 N%**」+ project名 | 今投げた依頼全体のおおよその進み具合。Task 件数 (3/5 等) は表示しない |
| Waiting | 🐣 + 「確認して！」+ project名 | **permission承認待ち。見に行く必要あり** (オレンジ吹き出し・軽く2回ピョコ・警告音1回) |
| StoppedIncomplete | 🐣 + 「途中で止まったよ」+ project名 | **未着手 Task を残したまま停止** (ベージュ吹き出し・警告音1回・最大10分表示) |
| Indeterminate | 🐣 + 「終わったか確認してね」+ project名 | Stop したが残 Task が in_progress のみ = **完了とも未完とも断定できない** (薄オレンジ吹き出し・警告音1回・最大10分表示) |
| Completed | 🐣 + 「終わったよ！」+ project名 | **依頼全体が本当に完了** (白吹き出し・3回ピョコピョコ・通知音1回・約5秒後に次の表示へ) |

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

```
Stop 受信
  ├─ 未完 Task なし → 終わったよ！
  └─ 未完 Task あり → Finalizing (約2秒の grace。UI は作業中のまま)
        ├─ grace 中に完了イベント到着で全完了 → 終わったよ！ (未完表示は一度も出さない)
        └─ grace 満了
              ├─ 未着手 (pending) Task が残る → 途中で止まったよ (明確に未完)
              └─ 残りが in_progress のみ → 終わったか確認してね (断定しない)
```

- grace window は async hook の到着順ゆれを吸収するための一時 one-shot timer
- **Task の削除/キャンセル (`TaskUpdate` status=deleted/cancelled) には対応する hook が発火しない**
  (実測)。PostToolUse から検知して total から除外する (これを怠ると「完了したのに
  途中で止まったよ」という false incomplete になる)
- Task の無い依頼: 構造化された判定材料がないため、Stop を完了として扱う
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

## Privacy / Security

このツールが扱うのは status metadata のみ:

- Hook の stdin JSON から読むのは `hook_event_name` / `session_id` / `cwd` / `agent_id` /
  `tool_name` / task の status・id のみ
- **Prompt 本文・Claude の応答本文・ソースコード本文・API キー・secret を
  進捗判定のために収集しない**。タスクの本文 (subject/description) も読まない
- 保存も送信もしない (ネットワーク通信なし・履歴 DB なし・全て in-memory)

## Requirements

- Windows 10 / 11 (x64)
- .NET Framework 4.8 (Windows 10/11 に標準搭載。追加インストール不要)
- [Claude Code](https://claude.com/claude-code) (Hooks 対応バージョン。v2.1.233 で開発・検証)

## Build

```powershell
git clone https://github.com/nikotaronosuke/claude-desktop-pet.git
cd claude-desktop-pet
powershell -ExecutionPolicy Bypass -File build.ps1
```

`bin\ClaudePet.exe` (常駐本体) と `bin\ClaudePetNotify.exe` (Hookヘルパー) が生成される。
コンパイルには Windows 標準の `csc.exe` (.NET Framework 4.8 同梱) を使うため、
Visual Studio や .NET SDK は不要。

## Installation

```powershell
pwsh -File install-hook.ps1   # または powershell -File install-hook.ps1
.\bin\ClaudePet.exe           # 常駐開始 (Hook発火時に自動起動もされる)
```

`install-hook.ps1` はユーザーレベル設定 `%USERPROFILE%\.claude\settings.json` に
以下の hook を **追記** する (既存 hooks は一切変更しない。イベント単位で冪等。
実行前に `settings.json.backup-claudepet-<日時>` を自動作成)。

| イベント | matcher | 用途 |
|---|---|---|
| `Stop` | なし | 完了通知 |
| `UserPromptSubmit` | なし | Working 開始 / 依頼リセット |
| `Notification` | `permission_prompt` | 確認して！ (idle_prompt 等は発火させない) |
| `PostToolUse` | `*` | 活動表示・Waiting 解除・Task/Todo 進捗 |
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
2. 常駐を止める: `.\bin\ClaudePetNotify.exe --quit`
3. クローンしたフォルダを削除

Hook を完全に元へ戻すには、自動作成されたバックアップを上書きコピーする:

```powershell
Copy-Item "$env:USERPROFILE\.claude\settings.json.backup-claudepet-<日時>" "$env:USERPROFILE\.claude\settings.json" -Force
```

## Limitations

- 進捗はあくまで heuristic。Claude がタスクリストを整理し直すと数字が前後する
- Task を使わない依頼では Stop ベースの完了判定になる (短い応答でも「終わったよ！」)
- 完了判定は Claude 自身の task lifecycle (status 更新) に依存する。grace (約2秒) を超える
  極端な async 遅延では「終わったか確認してね」になることがある
- nested 検出の限界: 子 Claude を起動した中間シェルが先に終了するとチェーンが切れて
  検出できない場合がある。exe 名が claude でない起動形態 (node 経由等) も検出不可
- `Stop` は「応答完了」ごとに発火する仕様のため、会話的なやり取りでも通知される
- permission 待ち (Waiting) の発火は対話セッションのみ
- マルチモニタ: プライマリモニタの右下固定。モニタ構成変更後はペット再起動が必要
- DPI はシステム DPI 基準 (セッション中の DPI 変更には追従しない)
- キャラはコード描画のひよこ (`src/Pet.cs` の `PetRenderer` 差し替えで変更可能)

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