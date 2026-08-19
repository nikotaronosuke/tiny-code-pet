# Claude Desktop Pet 🐣

A tiny native Windows desktop pet that shows what Claude Code (and Codex) is doing — working, waiting for you, overall progress, and truthful completion — at a glance.

Claude Code / Codex の状態を、デスクトップ右下の小さなひよこを見るだけで
把握できる **超軽量デスクトップ通知キャラクター** です。
Claude Code と Codex を同時に使っても session / 状態は衝突しません。

完全 auto 運用向けに、見える状態は 3 つだけ:

| 状態 | 表示 | 意味 |
|---|---|---|
| Idle | 🐣 + `Claude` | 何もしていない。完全静止 |
| 作業中 | 🐣 + 「作業中…」(+ 「**全体 推定 N%**」) + project名 | 放置してよい。permission 待ちも、Stop 後の静穏待ちも、すべてこの表示 |
| 完了 | 🐣 + 「終わったよ！」+ project名 | root Stop の後 20 秒間その作業が再開されなかった (3回ピョコピョコ・通知音1回・約5秒後に Idle) |

**「未完了」表示は無い。** 完了と言い切れない停止は何も出さずに Idle へ戻る。

表示中の session には **provider + model** の 1 行が付く (`Claude · Opus 4.6` /
`Codex · GPT-5.6-codex`)。model を取れないときは provider だけ。
同行の右端の **`+N`** は「他に動いている session 数」で、
Working / Finalizing / Waiting の session だけを数える (0 なら非表示)。

確認要求 UI・警告音・activity indicator は廃止した。音が鳴るのは完了時の 1 回だけ。

## 主な特徴

- Native Win32 (C# P/Invoke)。**Electron / WebView / Node 常駐 / localhost サーバー / DB 一切なし**
- 完全 event-driven (Claude Code 公式 Hooks 連携)。polling なし。アイドル時は `GetMessage` でブロック
- 背景完全透過・枠なし・タスクバー/Alt+Tab 非表示・常に最前面
  (通常ウィンドウより前。OS に topmost を剥がされても表示更新時に自動復帰)
- **クリック透過**: キャラの背後にある VS Code や Chrome をそのまま操作できる
- **通知領域 (system tray) の 🐥 アイコン**から 表示 / 隠す / 最前面に戻す / 終了 を操作
- 依頼全体の推定進捗表示 (新 Task システム / TodoWrite の両対応)
- 進捗と完了判定は完全に独立 (進捗 % は plan から、完了は Stop + 静穏から)
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
| 作業中表示中 | Idle と同じ (静止ビットマップ、タイマーなし)。イベント到着時だけ瞬間再描画 |
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
- 通知領域アイコンは `Shell_NotifyIcon` (純 Win32)。アイコン画像も実行時に
  System.Drawing で描く (外部画像ファイルなし)。taskbar ボタンや Alt+Tab には出ない
- ペット未起動時は Stop / UserPromptSubmit / permission_prompt で自動起動 (高頻度な PostToolUse では起動しない)
- **Prompt 本文・応答本文・ソースコードを送信・解析して進捗を推定しているわけではない**。
  扱うのは Hook が配る構造化 status metadata のみ

### 依頼全体の推定進捗

「依頼 (Request)」= そのセッションで最後に `UserPromptSubmit` が来てから Stop までの1ターン。
新しい依頼が始まると前回依頼の進捗はリセットされる。

表示される % は「**最初に投げた依頼全体が、工程表のどこまで進んだか**」の推定であって、
**今実行中の 1 タスクの進捗ではない**。structured plan/task を
「依頼完了までの工程表」とみなし、その全工程の status から計算する。

例: 「completion ロジックを変更して、テストして、docs も更新して」という依頼なら、
plan は「現状確認 / 実装 / UI 調整 / 回帰確認 / テスト追加 / 全 suite 実行 /
build 確認 / docs 更新」のように依頼全体を分解したものになる。この 8 工程から % を出す。
「今このファイルを編集中」だけを plan にすると、それは依頼全体の進捗にならない。

- 計算式: `(completed + 0.5 × in_progress) ÷ total × 100` (小数切り捨て)。
  in_progress は **「工程表の中の 1 工程が進行中なので 0.5 工程ぶん」** として数える。
  「その工程自体が 50% 終わった」という意味ではない
- **valid total >= 2 のときだけ % を出す**。total=1 の plan は「今やっている 1 個」でしかなく
  依頼全体の進捗としての根拠が弱いので % を表示しない (tracker 自体は保持する)
- total <= 0 (tracker なし / 空 / 解析不能) でも % は出さない。
  経過時間やツール実行回数から進捗を捏造することはしない
- **ETA ではない**。残り時間の予測は一切しない
- **途中で工程が増えると % が下がることがある**。これは嘘ではなく
  「依頼全体の見積もりが更新された」結果なので、単調増加させるための補正はしない
- Task 件数 (3/5 等) は UI に出さない
- **進捗は完了判定に一切関与しない**。100% でも Stop が無ければ完了しないし、
  50% でも Stop + 静穏があれば完了する

進捗のデータ源は2系統:

1. **TaskCreated / TaskCompleted hook + PostToolUse(TaskUpdate)**: 新 Task システム
   (`TaskCreate` / `TaskUpdate` ツール) のセッション。`task_id` の一意集合 (Set) で管理するため
   重複通知でも二重加算されない (上限 256 件/セッション)。in_progress・削除/キャンセルも反映
2. **TodoWrite スナップショット**: TodoWrite のセッションでは PostToolUse payload の
   `tool_input` 内の `"status"` 値の件数だけを数えて `completed/in_progress/total` を導出する
   (タスク本文は読まない・送らない)。全量スナップショットなので重複発火しても冪等

両方を同一依頼内で観測した場合は TodoWrite スナップショットを優先。

### 完了判定 (Completion semantics)

完了判定は **root Stop + 20 秒の静穏 (`CompletionQuietMs`) だけ**で決まる。
provider 共通で、Claude も Codex も同じ 20 秒を使う。

```
root Stop 受信
  └─ completion candidate。UI は「作業中…」のまま (進捗 % もそのまま)
        ├─ 20 秒以内に作業継続イベント → candidate 取消。Working へ戻る
        │    (次に完了できるのは新しい Stop が来てから)
        ├─ 20 秒以内に Stop 再受信     → 最新 Stop から 20 秒を数え直す
        ├─ 20 秒以内に新しい依頼        → 古い candidate は破棄。新しい作業を表示
        └─ 20 秒静穏で満了
              ├─ 他に動いている session が無い → 終わったよ！ (音1回・約5秒後に Idle)
              └─ 他に動いている session がある → 通知を出さずに片付ける (音も無し・再キューもしない)
```

**`終わったよ！` の意味**は「成果物が数学的に 100% 正しい」ではない。
Pet が prompt / 応答 / 成果物本文を読まずに判定できる範囲での

> **Claude Code / Codex が root Stop を出し、その後 20 秒間その作業を再開しなかった**

という事実だけを表す。

- **structured tracker は完了の証拠にしない**。進捗の推定材料にすぎない。
  94% でも / pending が残っていても / tracker が壊れていても / tracker が無くても、
  Stop + 20 秒静穏なら完了として通知する
- 逆に **100% でも Stop が来なければ完了しない**。interrupt のように Stop が
  来ないケースでは推測 timeout で完了させない
- **SessionEnd 単体は完了の根拠にしない**。Stop 済みの session なら SessionEnd が
  来ても candidate を維持し、Stop なしの SessionEnd では静かに片付けて Idle に戻る
- 「作業継続イベント」= Claude なら PostToolUse / Task 系 / TodoWrite / permission、
  Codex なら同一 turn の PostToolUse / update_plan / PermissionRequest /
  SubagentStart / SubagentStop。Codex の **old-turn の遅延イベントは current turn の
  candidate を取消さない** (provider + session + turn の分離は維持)
- 同一依頼への重複 Stop / 遅延イベントでも通知は1回だけ (debounce + tombstone)
- **Task の削除/キャンセル (`TaskUpdate` status=deleted/cancelled) には対応する hook が発火しない**
  (実測)。PostToolUse から検知して total から除外する (これを怠ると進捗 % が下振れする)

> **旧仕様 (現在は無効)**: 以前は Claude 2 秒 / Codex 5 秒の grace で
> 「structured task 全件 completed」を確認できたときだけ完了とし、
> それ以外を「未完了」表示にしていた。20 秒静穏方式へ置き換えたため、
> 2 秒 / 5 秒の grace も「未完了」UI も現在は存在しない。

### 最前面表示と通知領域 (tray)

Pet は**通常のアプリウィンドウより常に前面** (TOPMOST) に表示される。
ただし focus は決して奪わない: クリック透過 + `WS_EX_NOACTIVATE` なので、
VS Code で入力中に Pet の表示が切り替わっても入力先は変わらない。

`WS_EX_TOPMOST` は作成時の一度きりでは不十分で、fullscreen アプリや
Win+D、secure desktop 遷移などで OS が topmost band から外すことがある
(実運用で VS Code の背面へ回る現象を確認)。Pet は**表示内容が実際に
変わったとき**に `HWND_TOPMOST + SWP_NOACTIVATE` で Z-order を再保証する
(event-driven のみ。polling も常時タイマーも増やしていない)。

他の TOPMOST アプリとは Windows 標準の前後関係になる。押しのけ続けるような
争いはしないので、隠れた場合は tray の「最前面に戻す」で復帰させる。

通知領域の 🐥 アイコン:

| 操作 | 動作 |
|---|---|
| 左クリック | 最前面へ復帰 (hidden なら再表示 + 最新 state 描画 + 最前面) |
| 右クリック | menu: ヒヨコを表示 / ヒヨコを隠す / 最前面に戻す / ClaudePetを終了 |

「ヒヨコを隠す」は **visual hide** であって監視停止ではない。hidden 中も
hooks 受信・進捗更新・完了判定・session 管理はすべて継続し、再表示した
瞬間にその時点の最新 state を描く (過去の通知は再生しない)。
ユーザーが意図的に消しているため、**hidden 中は完了音も鳴らさない**。
tray の追加に失敗しても Pet 本体は通常動作する (fail-soft)。

### Nested 子 Claude の通知抑制

メイン Claude が Bash/PowerShell tool 内から起動した `claude -p` などの子 Claude は、
**プロセス祖先チェーン**で検出して UI 通知を完全に抑制する。

- 判定: hook helper の祖先プロセスに claude 本体が2つ以上あるか
  (1つ目 = hook を発火させた claude 自身、2つ目 = それを起動した親 Claude)
- PID 再利用による誤判定は「親の起動時刻 <= 子の起動時刻」検証で排除
- 手動で開いた別ウィンドウの Claude は祖先に claude がいないため抑制されない

### 複数セッション

内部状態は session_id 単位で分離。表示はペット1匹で、priority は

**Working / Waiting / Finalizing (= 動いている作業) > 終わったよ！ > Idle**、
同 priority なら最新イベントのセッション。

過去の完了通知が今動いている作業を隠さないことを最優先している。

- 完了通知を出すのは、静穏が満了した瞬間に**他に active な session が無いとき**だけ。
  他に動いていれば通知そのものを省略する (音も鳴らさず、後から再キューもしない)
- 通知中に新しい作業が始まれば、その場で作業表示へ切り替わる
- `+N` が数える active は Working / Waiting / Finalizing。
  Stop 後の静穏待ちはユーザーから見て「作業中…」なので active に含める。
  完了通知中の session と metadata だけの session は数えない
- Waiting の描画は「作業中…」と同じ (確認要求 UI は出さない)

- セッションテーブルは最大8件。超過時は最古を削除。4時間イベントの無いセッションも削除
- 全て in-memory。永続化なし

### Subagent の扱い

- Claude Code 内部の Subagent 完了は `SubagentStop` という別イベントであり、`Stop` hook は発火しない (実測確認済み)
- 防御として、`Stop` payload に `agent_id` が含まれる場合も通知しない
- `claude -p` 等で明示起動した別プロセスの Claude は独立セッションとして正当に監視される

## Codex 対応

Claude Code と同じペット・同じ UI で Codex の状態も見られる。
Codex 専用の画面は追加していない (作業中… / 終わったよ！をそのまま使う)。

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
| `PermissionRequest` | `.*` | sync | 完了候補取消 (確認 UI は出さない) |
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
  よって `Stop` は「完了候補」として扱い、**静穏 20 秒** (`CompletionQuietMs`) で確定する。
  静穏中に同じ turn の作業イベントが来たら候補を破棄し、
  2 回目の `Stop` なら 20 秒を最初から数え直す。
  (この静穏は Claude と共通。旧仕様では Codex 5 秒 / Claude 2 秒と別値だった)
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
| `Notification` | `permission_prompt` | 受信のみ (確認 UI は出さない) |
| `PostToolUse` | `*` | Waiting 解除・Task/Todo 進捗・grace 再起動 |
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
- **完了通知は「作業が止まった」ことの通知であって、成果物の正しさの保証ではない**
- **plan を使わない依頼では % が出ない**。完了通知自体は plan なしでも出る
- **total=1 の plan では % が出ない**。依頼全体を表す plan を作る運用とセット
- **Stop の 20 秒後まで完了通知は出ない**。速報性より false positive の回避を優先している
- **Stop 後 20 秒以内に届いた遅延イベントは「作業継続」とみなして candidate を取消す**。
  Claude の hook は async なので、Stop より前に発生したイベントが Stop の後から
  届いた場合も取消しになる。その turn は次の Stop が来るまで完了通知されない
  (誤って「終わったよ！」を出すより、出さない方を選んでいる)
- **完了通知は他に動いている session があると出ない**。まとめて再通知もしない
- 完了通知は約5秒で消えるため、画面から目を離していると見逃す
- **他の TOPMOST アプリには隠れることがある** (TOPMOST 同士は通常の前後関係)。
  tray の「最前面に戻す」か、次の表示更新の自動再保証で復帰する
- tray menu を開いた瞬間だけ Windows 標準の foreground 処理が入る
  (menu を外側クリックで閉じるために必要)。Pet はクリック透過なので
  その後の入力を奪い続けることはない
- nested 検出の限界: 子 Claude を起動した中間シェルが先に終了するとチェーンが切れて
  検出できない場合がある。exe 名が claude でない起動形態 (node 経由等) も検出不可
- `Stop` は「応答完了」ごとに発火する仕様のため、会話的なやり取りでも通知される
- permission 待ちは内部 state としてのみ扱い、確認 UI は表示しない
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