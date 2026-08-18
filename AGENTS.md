# claude-desktop-pet — Project Instructions (Claude Code / Codex 共通)

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

## Privacy

- Prompt 本文・Claude/Codex の応答本文を読まない。
- source code 本文を進捗推定に使わない。
- API key / secret を扱わない。
- Hook payload からは status metadata (hook_event_name / session_id / cwd /
  agent_id / tool_name / task の status・id) だけを読む方針を維持。
- ネットワーク送信・履歴 DB を追加しない。

## Progress philosophy

現在の Claude Code 側:

- Task / TodoWrite の structured status **だけ** から推定する。
- 計算式: `(completed + 0.5 × in_progress) / total`、floor して百分率。
- UI 表示は「全体 推定 N%」。Task 件数 (3/5 等) は出さない。
- activity 表示 (「● 活動中」) は進捗率と完全に分離。
- Task のない依頼では % を捏造しない (進捗非表示)。
- ETA を出さない。
- 経過時間や tool 実行回数を進捗率として使わない。
- 追加の LLM call で進捗を問い合わせない。

Codex 対応を追加する場合も同じ哲学を守ること。Codex の公式 structured
metadata から妥当な進捗材料が取れる場合のみ % を表示する。

## Completion philosophy

- 100% = turn 完了ではない。Stop まで Working 表示を維持する。
- 完了 (Celebrating) / 明確な未完 (StoppedIncomplete) /
  判定不能 (Indeterminate) を区別する。
- async hook の到着順ゆれを吸収する Finalizing grace がある。
  現在の約 2 秒 (`FinalizeGraceMs`) を理由なく変更しない。
- false positive な「終わったよ！」を避けることを最優先する。

## Claude Code / Codex compatibility

Codex 対応は実装済み (docs/DESIGN_DECISIONS.md の「Codex support」節が根拠)。
以下は壊さないこと:

- Claude 側:
  - 正規化イベント 1〜10 の意味を変えない。
  - Claude payload は 3 行のまま (4 行目の turn_id は Codex 専用)。
  - nested Claude suppression を変えない。
  - status 本文 (Todo/Task/plan step/command/response) を読まない。
  - `structured-observed` は固定 metadata のみ。本文を乗せない。
  - `FinalizeGraceMs = 2000` (Claude の quiet grace) を理由なく変えない。
  - root Stop は常に Finalizing へ入る。grace 中の関連イベントで deadline を
    延長し、completion を決めるのは `FinalizeDue` だけ (早期 celebration 禁止)。
  - Claude の完了判定を Codex 仕様へ共通化しない。
- Codex 側 (`src/CodexNotify.cs` + `Pet.cs` の `OnCodexEvent`):
  - dwData 20〜27 が Codex 専用範囲。1〜10 へ混ぜない。
  - 内部 key は `codex:<session_id>`。状態は provider + session + **turn** で分ける。
    current turn 以外の遅延イベントを UI へ反映させない (実測 18.6 秒遅延あり)。
  - Codex の `Stop` は完了確定ではない。`CodexQuietGraceMs = 5000` の
    quiet grace を削らない・短縮しない・Claude の 2 秒と共通化しない。
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
