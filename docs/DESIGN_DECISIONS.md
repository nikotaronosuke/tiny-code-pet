# Design Decisions

過去の重要な設計判断と「試したが採用しなかったもの」の記録。
現在仕様の正確な記述は README.md と source を正とする。
ここは「なぜそうしたか」を残すためのファイル。

出典: `src/Pet.cs` / `src/Notify.cs` / README.md / git history /
開発時の実測記録 (2026-08 時点)。

## Native/lightweight architecture

- **最重要要件は常駐の軽さ**: idle 時 CPU/GPU ほぼ 0、常駐 RAM 十数 MB。
  これが WinForms / WPF / Electron / WebView ではなく **純 Win32 P/Invoke**
  (`UpdateLayeredWindow` + layered window) を選んだ理由。
- 完全 event-driven。idle 時は `GetMessage` でブロックし、メッセージが
  来ない限り CPU を一切使わない。polling・常時描画・常時 timer は禁止。
  timer は animation / Finalizing grace / 表示期限の **one-shot のみ**
  (`SetTimer` したら必ず `KillTimer`)。
- 開発機に C/C++ toolchain・dotnet SDK が無いため、Windows 標準の
  `csc.exe` (.NET Framework 4.8 同梱、**C# 5 構文のみ**) でビルドする
  制約を受け入れた (`build.ps1`)。追加インストール不要という利点にもなった。
- 描画は状態変化時のみ。`_shownKey` (session|state|project|progress|active)
  が同一なら再描画しない (PostToolUse 連発対策)。
- 実測 (開発環境 Windows 11、README 記載): 待機時 Private Working Set
  約 13.5〜17 MB でプラトー、待機時 CPU 0%、GPU エンジンインスタンス 0、
  hook helper 1 回 約 60〜70 ms、GDI/ハンドルリークなし。

## Hooks architecture

```
Claude Code Hooks (user-level settings.json、全て async: true)
  → ClaudePetNotify.exe   … Hook Adapter。stdin JSON から status metadata
                            だけ抽出し、正規化イベントへ変換して即終了
  → WM_COPYDATA           … dwData = イベント種別 (1〜10)、
                            payload = "session_id\nproject名\nextra"
  → ClaudePet.exe         … 常駐本体。session_id 単位の state machine
```

- hook helper は短時間 (~数十 ms) で終了し、常駐本体と完全分離する。
  常に exit 0 で Claude Code を絶対にブロックしない
  (`SendMessageTimeout` + `SMTO_ABORTIFHUNG` 1 秒)。
- helper 側は完全な JSON parser を積まず、正規表現による軽量抽出。
  誤抽出リスクは表示・振り分け用途のみなので許容 (`Notify.cs` コメント参照)。
- 頻度の高い低優先イベント (PostToolUse 等) では pet を自動起動しない。
  自動起動するのは Stop / UserPromptSubmit / permission_prompt のみ。
- Codex は別 adapter (`CodexPetNotify.exe`) + 別 dwData 範囲 (20〜27)。
  上記 1〜10 の意味は Claude 専用で変更しない (「Codex support」節)。

## Request definition

- 「依頼 (Request)」= そのセッションで**最後の UserPromptSubmit から Stop
  までの 1 turn**。
- 新しい UserPromptSubmit で前依頼の進捗状態を完全リセットする
  (`Session.ResetRequest()`)。進捗は依頼単位であり、セッション累積ではない。

## Progress experiments

採用に至った経緯と不採用案:

- 最初は `completed / total` 的な単純進捗だった。着手済み Task が反映されず
  進捗が跳ねるため、**in_progress を 0.5 加点する現在式**
  `(completed + 0.5 × in_progress) / total` (floor、上限 100) へ改善。
- Task 件数表示 (3/5 等) は UI から削除。「全体 推定 N%」だけにした
  (件数は Claude の Task 分割粒度に依存し、ユーザーには意味が薄い)。
- activity indicator (「● 活動中」) を進捗とは**別に**追加
  (詳細は後述の Activity indicator 節)。
- **ETA 案は不採用**: 残り時間は structured status から誠実に導けない。
- **elapsed time interpolation 不採用**: 経過時間で % を補間すると
  「進んでいるように見えるだけ」の捏造になる。
- **tool count ベース進捗 不採用**: tool 実行回数は仕事量と相関しない。
- **LLM への進捗問い合わせ 不採用**: 追加コスト・追加レイテンシ・
  本文読み取りが必要になり、privacy 方針とも衝突する。

進捗のデータ源は 2 系統 (README 参照): TaskCreated/TaskCompleted hook +
PostToolUse(TaskUpdate) の task_id 一意集合 (上限 256/セッション) と、
PostToolUse(TodoWrite) の status 件数スナップショット。同一依頼内で両方を
観測した場合は **TodoWrite スナップショットを優先** (全量スナップショット
なので冪等で信頼できる)。TodoWrite のカウントは `tool_input` 領域のみ対象
(`tool_response` の echo による二重カウント防止)。

### 強制 Task 細分化実験 (不採用)

「6〜8 個の milestone Task を維持せよ」という指示をプロンプトへ付ける実験。

- 進捗の刻みは最大 33 ポイント跳びから最大 7 ポイント刻みへ滑らかになった。
- しかし同一依頼で turns が 5 → 30 に増加、実行時間 +184%、
  コスト・output token も大幅増 (開発時の実測記録より)。
- ツール側でのグローバル強制は**却下**。README に「滑らかな進捗が欲しい
  長時間依頼では自分のプロンプトに付ける」opt-in レシピとしてのみ記載。

## Completion reconciliation

「終わったよ！」の false positive / false negative を潰した経緯:

- **TaskUpdate status=deleted/cancelled には対応する hook が発火しない**
  (実測)。削除された Task が created 集合に幽霊として残り、
  「依頼完了なのに未完 Task が残っている」= **false incomplete** の主因
  だった。→ PostToolUse(TaskUpdate) の metadata から deleted/cancelled
  (ev=9) / in_progress (ev=10) / completed 保険 (ev=7) を導出して解決。
- TaskCompleted hook と TaskUpdate(completed) は二重発火する。
  task_id の Set 管理による冪等性が前提。
- async hook の到着順ゆれ対策として、Stop 時に未完 Task が残っていても
  即断定せず **Finalizing (約 2 秒 grace、`FinalizeGraceMs = 2000`)**。
  UI は Working のまま。grace 中に完了イベントが届けば通常の
  Celebrating (未完表示は一度も見せない)。
- grace 満了時の判定:
  - pending (未着手) が残る → **StoppedIncomplete**「途中で止まったよ」
  - 残りが in_progress のみ → **Indeterminate**「終わったか確認してね」
    (status 更新忘れの可能性があり、完了とも未完とも断定しない)
- stale な状態を無理に complete 扱いしない。断定できないものは
  断定しないと表示するのがこのツールの価値。
- 付随する防御: 同一依頼への重複/遅延 Stop は debounce、celebration/
  SessionEnd 後 120 秒の tombstone で遅延 async イベントによる幽霊
  セッション再作成を防止 (UserPromptSubmit / permission_prompt で解除)。
  SessionEnd は Celebrating / Finalizing 中のエントリを削除しない
  (削除すると完了・未完通知そのものが消えるため)。

## Nested Claude suppression

- Claude のツール内から `claude -p` 等で起動された子 Claude の hook が
  親と同じ pet へ届き、通知が混ざる問題があった。
- **環境変数では区別できなかった** (実測): CLAUDECODE / CHILD_SESSION /
  SESSION_ID 等は親 VS Code セッションも同値を持つ。
- 採用したのは **process ancestor chain**: helper の祖先プロセスに
  claude 本体が 2 つ以上あれば nested (1 つ目 = hook を発火させた claude
  自身、2 つ目 = それを起動した親)。
- PID 再利用による誤判定は「親の起動時刻 <= 子の起動時刻」検証で排除。
- 判定不能 (例外) 時は**抑制しない** (安全側 = 通常動作。正当な通知を
  失うより誤通知の方がまし、ではなく「不確実なら抑制しない」)。
- 既知の限界: 中間シェルが先に終了するとチェーンが切れる /
  exe 名が claude でない起動形態は検出不可 (README Limitations 参照)。
- テスト時の注意: 独立セッションを装うには WMI
  (Invoke-CimMethod Win32_Process Create) で起動する必要がある
  (通常のツール実行だと自分の子になり抑制されてしまう)。
- 関連: Claude Code 内部 Subagent の完了は SubagentStop であり Stop hook
  は発火しない (実測)。防御として agent_id 付き Stop / TaskCreated /
  TaskCompleted も通知・集計しない。

## Activity indicator

- 進捗 % が動かない時間帯に「stuck では?」と不安になる問題への回答。
- PostToolUse 等の**実 activity** を観測したときだけ「● 活動中」(緑) を表示。
- TTL 15 秒 (`ActivityTtl`)。消灯は one-shot timer (常時 timer にしない)。
- **progress 値には一切影響させない**。「% が止まっていても Claude は
  動いている」ことだけを伝える表示で、進捗と混ぜると両方の意味が壊れる。

## Multi-session

- 内部状態は session_id 単位で完全分離 (最大 8 セッション、4 時間
  イベントなしで削除。全て in-memory)。
- 表示は pet 1 匹で priority 制:
  Waiting / StoppedIncomplete / Indeterminate (要ユーザー確認) >
  Celebrating > Working。同 priority は最新イベントのセッション。
- StoppedIncomplete / Indeterminate は気付くまで残すが、終了済み
  セッションが他の表示を塞ぎ続けないよう最大 10 分で自動消滅
  (`IncompleteNoticeTtl`)。
- PostToolUse の cwd はツール実行ディレクトリで揺れるため、activity 系
  イベントでは project 名を上書きしない (最初に確定した名前を維持)。

## Privacy decision

Prompt 本文・応答本文・source code 本文・Task 本文 (subject/description)
を読まない設計にしている理由:

- このツールの目的は「状態が分かること」であり、内容理解は不要。
  status metadata (hook_event_name / session_id / cwd / agent_id /
  tool_name / task の status・id) だけで目的を達成できる。
- 本文を読む設計にすると、secret・個人情報・顧客コードが通知経路と
  ログに流れ込むリスクが生まれる。読まなければ漏れようがない。
- 保存も送信もしない (ネットワーク通信なし・履歴 DB なし・in-memory のみ)
  ことで、攻撃面と検証コストを最小にしている。
- debug ログ (`bin\debug.flag` 存在時のみ) も event 種別と metadata のみ。

この方針は Codex 対応でも変えない。

## Known limitations

再検討可能なもの (将来 Codex 対応等で見直してよい):

- マルチモニタ: プライマリモニタ右下固定。構成変更後は pet 再起動が必要。
- DPI はシステム DPI 基準 (セッション中の変更に追従しない)。
- nested 検出の限界 (中間シェル終了でチェーン切れ / claude 以外の exe 名)。
- Stop は「応答完了」ごとに発火するため、会話的なやり取りでも通知される。
- permission 待ち (Waiting) の発火は対話セッションのみ。

Codex 側 (詳細は「Codex support」節):

- SubagentStart / SubagentStop の実発火は未確認 (現 build で発火しない可能性)。
- subagent を含む turn では progress を表示しない (origin を証明できないため)。
- interrupt では Stop が来ないので Working 表示が残る。次の依頼まで解除しない。
- Codex 側に nested 抑制 (Claude の process ancestor chain 相当) は無い。
- 未実施: 実 Codex での live verification (Hooks 未インストールのため)。
- `ClaudePetNotify` のペット自動起動は未だハンドルを継承する
  (async hook のみなので潜在的。上記「ペット自動起動は ShellExecute」参照)。

絶対に壊してはいけない設計原則 (AGENTS.md の正本を参照):

- native / event-driven / idle CPU ほぼ 0 / 小 RAM。
- status metadata のみを読む privacy 方針。
- 進捗を捏造しない (時間・tool 回数・LLM 問い合わせによる % 生成禁止)。
- 完了の断定は誠実に (false positive「終わったよ！」を出さない)。


## Codex support (実装済み)

Codex 対応は Phase A〜F の実測調査 (2026-08) に基づいて実装した。
以下は「なぜこの形になったか」の記録であり、現在仕様の正は source と README。

### 実測環境 (この設計の前提バージョン)

- VS Code ChatGPT/Codex 拡張: 26.814.41407
- Codex CLI: 0.148.0-alpha.15
- Codex Hooks: `[features] hooks = true` + `hooks.json`
  (project は `<project>/.codex/hooks.json`、user は `$CODEX_HOME/hooks.json`)
- 公式 schema 上の hook: PreToolUse / PermissionRequest / PostToolUse /
  PreCompact / PostCompact / SessionStart / SessionEnd / UserPromptSubmit /
  SubagentStart / SubagentStop / Stop
- matcher は `\A(?:...)\z` の完全一致 regex (glob ではない)。全 tool は `.*`。

**バージョンが上がったらこの前提を再確認すること。想像で仕様を書かない。**

### Hooks-only を採用。rollout watcher / App Server 常駐は不採用

- Codex の状態は rollout JSONL や App Server (`turn/completed` 等) からも読めるが、
  どちらも **常駐監視 (file watcher / server 接続) が必要**で、
  「idle CPU ほぼ 0・polling なし・常駐は pet 1 プロセスだけ」という
  このツールの最重要要件と正面から衝突する。
- interrupt 検出のためだけに rollout watcher を入れる案も検討したが不採用。
  後述のとおり **completion candidate を作らない**ことで、
  watcher なしでも false positive を防げる (下記 interrupt 節)。
- subagent progress のためだけに App Server を入れる案も同じ理由で不採用。

### adapter を分離 (CodexPetNotify.exe)

```
Claude Code Hooks -> ClaudePetNotify.exe -> WM_COPYDATA (dwData 1-10)  -┐
                                                                        ├-> ClaudePet.exe
Codex Hooks       -> CodexPetNotify.exe  -> WM_COPYDATA (dwData 20-27) -┘
```

- `src/Notify.cs` (Claude adapter) は **1 文字も変更していない**。
  既存 payload 契約と正規化イベント 1〜10 の意味を変えないための最も確実な方法。
- 代償として JSON 抽出・WM_COPYDATA 送信のコードが 2 本に重複するが、
  「Claude 側を壊さない」ことの方が価値が高いと判断した。
- Codex payload だけ 4 行目に `turn_id` を持つ (`session\nproject\nextra\nturn`)。
  Pet 側は dwData が 20〜27 のときだけ 4 分割する。Claude payload の
  パース方法は従来どおり 3 分割のまま。

### Codex 正規化イベント (dwData 20〜27)

| ev | Codex hook | 用途 | extra |
|---|---|---|---|
| 20 | UserPromptSubmit | 新 turn 登録・進捗/permission/candidate リセット | - |
| 21 | PostToolUse | tool activity | - |
| 22 | PostToolUse(update_plan) | plan snapshot | `c/i/t` |
| 23 | PermissionRequest | 確認して！ | - |
| 24 | Stop | completion candidate (5 秒 quiet grace) | - |
| 25 | SessionEnd | 後片付け | - |
| 26 | SubagentStart | その turn を「subagent 含む」と mark | - |
| 27 | SubagentStop | mark 維持 (root completion にはしない) | - |

PreToolUse は production では使わない (PostToolUse で足りる)。
update_plan 専用の PostToolUse を追加登録すると同一 tool で helper が
2 回起動するため、**PostToolUse は 1 本だけ**登録し adapter 内で分岐する。

### progress は update_plan の全量 snapshot だけ

- canonical tool 名は `update_plan`。`tool_input.plan` は実測した 3 更新すべてで
  **全量 snapshot** だった (要素は `step` / `status`)。よって冪等に扱える。
- canonical status は `pending` / `in_progress` / `completed`。stable な step ID は無い。
- **step 本文は読まない。status の個数だけ数える。**
- 計算式は Claude と同じ `floor((completed + 0.5 * in_progress) / total * 100)`。
  実測どおり 1/1/1 -> 50%、2/1/0 -> 83%、3/0/0 -> 100%。
- `tool_response` は progress に使わない (`tool_input` 領域だけを数える。
  `tool_response` / `tool_use_id` 以降は二重カウント防止のため対象外)。
- **Hook snapshot の欠落を 1 件観測した**ため fail-closed:
  status を 1 つも取れなければ snapshot を送らず単なる activity として扱う。
  捏造せず、次の snapshot で自己修復する。本文や経過時間で補完しない。

### Codex の Stop は完了確定ではない -> 5 秒 quiet grace

実測 (Phase C):

- 最初の Stop -> 約 1.88 秒後に continuation 開始 (UserPromptSubmit の再発火なし・
  同一 turn_id) -> 約 3.69 秒後に 2 回目の Stop。
- 最初の Stop payload から「別 hook が continuation を返すか」は判別不能。
- `stop_hook_active` は最初 false / continuation 後 true になるが、
  **true でも「今回が最終 Stop」を保証しない**ので判定に使わない。
- Stop は rollout の `task_complete` より約 130〜165 ms 早い。

よって Codex では **Stop = completion candidate**、静穏 5 秒
(`CodexQuietGraceMs = 5000`) で確定する。grace 中に同一 turn の
PostToolUse / update_plan / PermissionRequest が来たら candidate を破棄し、
2 回目の Stop が来たら 5 秒を最初から数え直す。

**Claude の Finalizing 約 2 秒 (`FinalizeGraceMs = 2000`) とは別物。**
Claude 側は「async hook の到着順ゆれを吸収する」ためのもので、Codex 側は
「Stop の後に continuation があり得る」ためのもの。理由も値も別で、
片方を変えてももう片方に影響しないよう timer / 定数 / 判定経路を分離している。

### progress 100% と completion は別

- 実測で 100% 到達から Stop まで約 1.7 秒離れていた。
- grace 満了時の判定は既存の完了哲学をそのまま使う:
  信頼できる plan snapshot が無い / 全部 completed -> Celebrating、
  pending が残る -> StoppedIncomplete、in_progress だけ残る -> Indeterminate。

### PermissionRequest の限界

実測 schema: session_id / turn_id / cwd / hook_event_name / model /
permission_mode / tool_name / tool_input / description。
**結果 field も tool_use_id も無い。**

- 他の hook が allow/deny を返せるため、PermissionRequest 単独では
  「approval boundary に到達した」ことしか保証しない
  (実測では実際に承認 UI で人間待ちになるケースを確認済み)。
- production では PermissionRequest -> AwaitingPermission (「確認して！」)、
  同一 session+turn の後発 PostToolUse で Working へ戻す。
  PostToolUse が来なくても Stop / 新しい UserPromptSubmit / SessionEnd で解除する。
  **PreToolUse では解除しない** (そもそも登録しない)。
- approve / deny / cancel の区別は表示しない。permission 専用の timeout も足さない。
- observer は decision を返さない。allow/deny へ絶対に介入しない。
- ordering が重要で低頻度なので sync hook にする。

### interrupt では Stop が来ない -> fail-closed

実測 (Phase F):

- App Server: `turn/completed.status = interrupted`
- CLI rollout: `turn_aborted.reason = interrupted`
- **Stop hook は発火しない。SessionEnd も発火しない。**
- interrupt 後に PostToolUse が **約 18.6 秒遅れて**届いたケースがある。
  PostToolUse schema だけでは正常終了と区別できない。

production の方針:

- Stop が来なければ completion candidate を作らない。よって
  「終わったよ！」の false positive は構造的に発生しない。
- interrupt 後に Working 表示が残っても、**推測 timeout で
  Completed / Indeterminate へ移さない**。次の UserPromptSubmit が来たら
  新 turn として古い progress / permission / activity / Stop candidate を破棄する。
- 18.6 秒遅延イベントの実測があるため、内部 state key は必ず
  **provider + session + turn** を見る。current turn 以外の遅延イベントは
  現在 UI へ適用しない (adapter ではなく pet 側で捨てる)。

### subagent は fail-closed

- Phase F では現環境で実際の subagent を安全に発生させられず、
  **SubagentStart / SubagentStop の実発火は未確認** (Known limitation)。
- 公式 schema 上、SubagentStart は session_id (= parent) / turn_id / agent_id /
  agent_type / permission_mode を持ち、SubagentStop はさらに
  agent_transcript_path / stop_hook_active / last_assistant_message を持つ。
  **本文 field は読まない。**
- 問題: **PreToolUse / PostToolUse schema には agent_id / agent_type / parent が無い**。
  よって tool event の origin を structured metadata だけで証明できない。
- 方針:
  - SubagentStop を root completion として扱わない。
  - subagent 専用 UI を追加しない。
  - current turn で SubagentStart を検出したらその turn を「subagent 含む」と mark。
  - その turn では origin 不明の update_plan を root progress へ適用しない。
    既に表示していた Codex progress も検出時点で無効化する (% を消す)。
  - SubagentStop 後もその turn 中は root progress を再開しない (origin を証明できないため)。
  - tool activity 自体は Working / 「● 活動中」に使ってよい。
- subagent progress のために rollout / App Server を導入しない。

### provider / session / turn の分離

- 内部 key は `codex:<session_id>`。Claude の session_id と名前空間が衝突しない。
- turn は Codex セッションだけが持つ (`Session.TurnId`)。
  **Claude の session semantics には turn_id が無いので、Claude を Codex へ
  合わせる共通化はしない。** Codex 固有 field (`IsCodex` / `TurnId` /
  `TurnHasSubagent` / `CodexGraceDueUtc`) を Session に足すだけの薄い拡張にした。
- Pet 側の入口も分離: dwData 20〜27 は `OnCodexEvent`、1〜10 は従来の `OnEvent`。
  共通なのは表示・優先度・prune・進捗計算式・完了哲学だけ。

### async / sync の考え方

- Phase A/B の build では async hook が実際に動いた。ただし Codex の version 差で
  挙動が変わりうる。
- したがって **async は performance optimization であって、
  async でなければ正しさが壊れる設計にしない**。
  高頻度な PostToolUse だけ async、それ以外は ordering 優先で sync。
  Codex は SessionEnd を async 指定でも同期実行する (binary の warning 文字列で確認)。
- sync hook を待たせないよう、Codex adapter のペット自動起動待ちは
  Claude 側の 3 秒ではなく 1.5 秒 (`StartWaitMs`)。

### ペット自動起動は ShellExecute (ハンドルを継承させない)

`CodexPetNotify` がペットを自動起動するときは
`ProcessStartInfo.UseShellExecute = true` を使う。**戻さないこと。**

- `UseShellExecute = false` は CreateProcess を `bInheritHandles = TRUE` で呼ぶため、
  hook の stdin/stdout/stderr がそのまま常駐ペットへ継承される。
  helper が終了してもペットが hook の stdout を掘んだままになるので、
  stdout を EOF まで読む hook runner は**ペットの生存中ずっとブロック**する
  (リダイレクト先ファイルを排他オープンできないことで実測確認済み)。
- Codex は UserPromptSubmit / PermissionRequest / Stop を **sync hook** として登録し、
  この 3 つがそのまま自動起動対象なので、放置すると hook timeout になる。
- 標準ハンドルの継承フラグだけを `SetHandleInformation` で落とす案も試したが、
  .NET 側が標準ハンドルを渡すため効かなかった (実測)。
  ShellExecute はハンドルを一切渡さないのでこれを採用した。
- コストは実測で約 1.0 秒 (ペット起動 + `FindWindow` の 100ms ポーリング)。
  この経路を通るのはペット未起動時だけで、通常は `FindWindow` が成功して
  helper は 50〜90 ms で終わる。
  (ビルド直後の初回起動だけ AV スキャンで数十秒かかることがあるが、
   これは起動方式とは無関係)。

**既知: `ClaudePetNotify` 側には同じ問題が残っている。** Claude の hook は
全て async なのでセッションをブロックせず、またペットが既に起動していれば
この経路を通らないため潜在的。Codex 対応とは別の変更として扱う。

### privacy (Codex でも変えない)

読んでよい: hook_event_name / session_id / turn_id / cwd / tool_name /
permission_mode / agent_id / agent_type / stop_hook_active / plan[].status / plan 要素数。

読まない・送らない・保存しない: prompt / last_assistant_message / description 本文 /
plan step 本文 / tool command / tool response 本文 / transcript / reasoning /
source / API key / secret / credentials。
adapter は plan の status 個数を数えるだけで、step 文字列を抽出すらしない。

### install / uninstall

- `install-codex-hook.ps1` / `uninstall-codex-hook.ps1` を追加 (Claude 用は無変更)。
- **config.toml は絶対に書き換えない。** `[features] hooks = true` が無い場合は
  必要な 2 行を案内するだけ。model / sandbox / trust / notify に触れない。
- 追記のみ・イベント単位で冪等・実行前に hooks.json を自動バックアップ。
  uninstall は command に `CodexPetNotify` を含む entry だけを除去し、
  他人の hook を残す。`-DryRun` で全差分を事前確認できる。
- Codex は hooks.json に trust 承認 (trusted_hash) を要求する。
  **`--dangerously-bypass-hook-trust` を production 設定に入れない。**
  承認は人間が Codex 上で行う。
