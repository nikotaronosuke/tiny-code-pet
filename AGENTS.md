# agent-desktop-pet (AgentPet) — Project Instructions (Claude Code / Codex 共通)

このファイルが project 固有ルールの正本。全 project 共通ルール
(Git 安全運用・secret 非表示・スコープ規律等) は global の
`C:\dev\agent-guidelines\AGENTS.md` にあり、ここには再掲しない。

## Project status

- この project は現時点で **完成** 扱い (README「Development status」参照)。
- Claude Code の状態を手元で確認するために作った個人用の小さなツール。
- 必要な機能だけを最小構成で実装している。
- **明示依頼なしに機能追加・大規模リファクタ・UI 刷新をしない。**
- 今後行うのは不具合修正、または明示された変更だけ。

## Core design (維持すべきもの)

- Windows native。C# + Win32 P/Invoke のみ (`src/Pet.cs` / `src/Notify.cs`)。
  .NET Framework 4.8 同梱の csc.exe でビルドするため **C# 5 構文のみ**。
- Electron / WebView / Chromium / Node 常駐を導入しない。
- localhost server / DB を導入しない。全て in-memory・永続化なし。
- 完全 event-driven。polling しない。idle 時は `GetMessage` でブロック。
- 常時 timer を増やさない。timer は animation / grace / 表示期限の one-shot のみ。
- idle 時 CPU ほぼ 0・RAM 十数 MB を維持する。
- click-through / 背景透過 / always-on-top / タスクバー非表示という
  現在の UI 特性を壊さない。
- TOPMOST の再保証は event-driven のみ (表示内容の変化時と明示操作時)。
  polling / 常時 timer / bounce 毎 tick の再 assert を追加しない。
  focus を奪う経路 (自動での SetForegroundWindow 等) を追加しない。
- 通知領域アイコンは Shell_NotifyIcon の純 Win32 実装 1 つだけ。
  WinForms NotifyIcon / 別常駐 helper を導入しない。
  「ヒヨコを隠す」は visual hide であり、hooks 受信・進捗・完了判定は
  hidden 中も継続する (hidden 中は完了音を鳴らさない)。

## Privacy

- Prompt 本文・Claude/Codex の応答本文を読まない。
- source code 本文を進捗推定に使わない。
- API key / secret を扱わない。
- Hook payload からは status metadata (hook_event_name / session_id / cwd /
  agent_id / tool_name / task の status・id) だけを読む方針を維持。
- ネットワーク送信・履歴 DB を追加しない。

## Visible states (完全 auto 運用)

- 見える状態は Idle /「作業中…」/「終わったよ！」の 3 つだけ。
  確認要求 UI・警告音・activity indicator・**「未完了」表示**は廃止済み。復活させない。
- Waiting も Finalizing (Stop 後の静穏待ち) も描画は「作業中…」。
- 「終わったよ！」は約5秒の一時通知で、その後は Idle。
- 表示priority は **active (Working/Waiting/Finalizing) > Celebrating**。
  過去の完了通知で進行中の作業を隠さない。
- 完了通知を出すのは満了時に他の active が無いときだけ。他が動いていれば
  通知を省略し、音も鳴らさず、後から再キューもしない。

## Progress philosophy

- % は **「最初に依頼した作業全体が工程表のどこまで進んだか」** の推定。
  **現在実行中の 1 タスクの進捗ではない。**
- structured plan/task を「依頼完了までの工程表」とみなし、その全工程の
  status **だけ** から推定する。
- 計算式: `(completed + 0.5 × in_progress) / total`、floor して百分率。
  in_progress は「工程表の 1 工程が進行中なので 0.5 工程ぶん」の意味。
- **valid total >= 2 のときだけ % を出す** (`MinProgressTotal`)。
  total=1 / total<=0 / tracker なしでは % を出さない。
- plan が増えて % が下がることは許容する。単調増加のための補正や
  stale な高値の固定はしない。
- UI 表示は「全体 推定 N%」。Task 件数 (3/5 等) は出さない。
- ETA を出さない。経過時間や tool 実行回数を進捗率として使わない。
- 追加の LLM call で進捗を問い合わせない。

## Completion philosophy

- **progress と completion は完全に独立**。structured tracker は進捗の
  推定材料であって、終了の証拠ではない。
- 完了 = **root Stop + `CompletionQuietMs` (20 秒) の静穏**。これだけ。
  provider 共通で、Claude / Codex とも同じ値を使う。
- `FinalizeDue` は tracker (`GetProgress` / `SawStructuredTasks` / `SnapTotal`)
  を読まない。この不変条件を壊さない。
- 静穏中の作業継続イベントは deadline を延長せず **candidate を取消す**。
  次に完了できるのは新しい root Stop から。
- 重複 Stop は最新 Stop から 20 秒を数え直す。
- Stop が来ないもの (interrupt / SessionEnd 単体) を推測 timeout で
  完了にしない。
- 「終わったよ！」の意味は「成果物が正しい」ではなく
  「root Stop の後 20 秒その作業が再開されなかった」。docs にもそう書く。

## Claude Code / Codex compatibility

Codex 対応は実装済み (docs/DESIGN_DECISIONS.md の「Codex support」節が根拠)。
以下は壊さないこと:

- Claude 側:
  - 正規化イベント 1〜10 の意味を変えない。
  - Claude payload は 3 行のまま (4 行目の turn_id は Codex 専用)。
  - nested Claude suppression を変えない。
  - status 本文 (Todo/Task/plan step/command/response) を読まない。
  - `structured-observed` は固定 metadata のみ。本文を乗せない。
  - event 11 (SessionStart) は model 表示用 metadata 専用。これだけで
    「作業中…」にしないし active session にも数えない。
  - root Stop は常に Finalizing (quiet window) へ入る。completion を決めるのは
    `FinalizeDue` だけ (早期 celebration 禁止)。
- Codex 側 (`src/CodexNotify.cs` + `Pet.cs` の `OnCodexEvent`):
  - dwData 20〜27 が Codex 専用範囲。1〜10 へ混ぜない。
  - 内部 key は `codex:<session_id>`。状態は provider + session + **turn** で分ける。
    current turn 以外の遅延イベントを UI へ反映させない (実測 18.6 秒遅延あり)。
  - Codex の `Stop` は完了確定ではない。`CompletionQuietMs` の静穏を
    削らない・短縮しない。
  - interrupt では `Stop` が来ない。**推測 timeout で Completed / Indeterminate へ移さない。**
  - `update_plan` の status 件数だけを数える。snapshot を取れなければ進捗を出さない。
  - subagent を検知した turn では progress を信用しない (fail-closed)。
  - rollout watcher / App Server 常駐 / PreToolUse hook を導入しない。
  - `install-codex-hook.ps1` は `config.toml` を書き換えない。
    `--dangerously-bypass-hook-trust` を production 設定へ入れない。
- 両 adapter 共通:
  - ペット自動起動は `UseShellExecute = true`。false へ戻すと hook の
    stdin/stdout/stderr をペットが掴んだままになり、stdout を EOF まで
    読む hook runner がブロックする (実測)。

## Before changing behavior

動作変更前に必ず以下を確認すること:

- README.md
- AGENTS.md (このファイル)
- docs/DESIGN_DECISIONS.md (過去の判断と不採用案)
- 該当 source (`src/Pet.cs` / `src/Notify.cs`)
- 該当 git history
