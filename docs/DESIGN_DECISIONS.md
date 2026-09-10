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

## Completion reconciliation (旧仕様の経緯)

> **注意: この節は履歴。** 現在の完了判定は
> 「root Stop + 20 秒静穏」だけで、structured tracker は完了判定に使わない。
> 最新仕様は後述の「completion = root Stop + 20 秒静穏」を参照。

「終わったよ！」の false positive / false negative を潰した経緯:

- **TaskUpdate status=deleted/cancelled には対応する hook が発火しない**
  (実測)。削除された Task が created 集合に幽霊として残り、
  「依頼完了なのに未完 Task が残っている」= **false incomplete** の主因
  だった。→ PostToolUse(TaskUpdate) の metadata から deleted/cancelled
  (ev=9) / in_progress (ev=10) / completed 保険 (ev=7) を導出して解決。
- TaskCompleted hook と TaskUpdate(completed) は二重発火する。
  task_id の Set 管理による冪等性が前提。
- async hook の到着順ゆれ対策として **Finalizing
  (約 2 秒 quiet grace、`FinalizeGraceMs = 2000`)**。UI は Working のまま。
  当初は「未完 Task が残っているときだけ」grace へ入っていたが、
  現在は **すべての root Stop** が grace を通る (後述「Claude の
  quiet grace」節)。
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


## structured tracker の「観測事実」と「現在の snapshot」を分ける

false positive な「終わったよ！」をさらに潰すための変更。

### なぜ分けるか

adapter は status を解析できないと event を通常 Activity へ落とす。
このとき Pet 側は「structured task を使っていない依頼」と見なし、
Stop だけで完了扱いしてしまう経路があった。

**「今回 status を読めなかった」≠「structured tracker は使われていない」** 。
同じ考え方は関連 OSS でも使われている (claude-hud は transcript から
Todo を復元できなくても過去の structured state を「存在しない」へ落とさない、
codex-pulse は新しい解析で plan を取れなくても plan の存在を忘れない)。

### 固定 marker `structured-observed`

新しい event ID は追加せず、既存 Activity (4 / 21) の `extra` に
**固定文字列 `structured-observed`** だけを載せる。
payload の行数契約 (Claude 3 行 / Codex 4 行) も event 1〜10 / 20〜27 の
意味も変えていない。marker は prompt / Todo 本文 / plan step / command /
response を一切含まない。3 つの source の同名定数を一致させること。

marker を出すのは:

- Claude: PostToolUse(TodoWrite) の snapshot 解析失敗、
  PostToolUse(TaskUpdate) を 7/9/10 へ変換できないとき、
  TaskCreated / TaskCompleted で task_id を取れないとき (以前は捨てていた)
- Codex: PostToolUse(update_plan) の plan 解析失敗

subagent (agent_id 付き) の structured task を root へ混ぜない点、
nested Claude suppression は従来どおり。

### `SawStructuredTasks` の意味

「現在 valid な snapshot があるか」ではなく
**「この依頼/turn で structured tracker を一度でも観測したか」**。
marker でも、正常な snapshot でも true になり、**`ResetRequest()`
(新しい UserPromptSubmit / CodexPromptSubmit) 以外では false へ戻さない**。

- snapshot の件数は問わない。`total=0` (リスト全消し) でも
  「tracker なし依頼」へは格下げしない (安易に complete としない)。
- marker 受信時に snapshot 値は一切触らない。既存の有効な snapshot を
  消さないし、進捗率も捏造しない。tracker の存在だけを sticky にする。
- Codex では subagent 抑制中の update_plan でも観測事実は残す
  (progress へは適用しないまま)。

## 完全 auto 運用向けの表示簡素化と厳格 completion (2026-08)

ユーザーは Claude Code / Codex を完全 auto で使う。知りたいのは
「本当に完了したか」と「今何%か」だけなので、表示を 3 種類へ絞った。

### visible state は 3 種類

| 表示 | 内部 state |
|---|---|
| Idle | (表示候補なし) |
| 作業中… | Working / Waiting / Finalizing |
| 終わったよ！ | Celebrating |

内部 state は既存 event 互換のために残し、描画だけ統合した。
廃止した表示: 「確認して！」「終わったか確認してね」「途中で止まったよ」
「● 活動中」、そして「未完了」。警告音と軽バウンドも廃止 (音は完了時の 1 回だけ)。
ユーザー操作を要求する UI は出さない。

### taskless fallback の廃止 (厳格 completion) — 旧仕様

> **注意: この節は履歴。** 「structured task 全件 completed」を完了の必須条件に
> していた時期の記録。現在は tracker と completion を完全に分離した
> (後述「completion = root Stop + 20 秒静穏」)。

以前は「structured task を観測していない依頼は Stop を完了根拠にする」
fallback があり、それを一度廃止して「tracker で全件 completed を確認できた
ときだけ完了」という厳格版にした。task なし / total=0 / 解析不能 /
pending 残 / in_progress 残 / interrupt / subagent はすべて「未完了」表示だった。

この方式は完了通知の確実性を **依頼側の運用 (毎回 structured task を作る)** に
依存させていた。運用を守れないと完了通知が出ず、逆に status を先に completed へ
倒すと嘘の完了が出る、という構造的な弱さがあった。

### 「未完了」表示の廃止 — 旧仕様の終着点

「未完了」は最初は次の依頼まで出しっぱなし、次に約5秒の一時通知
(`NoticeDueUtc` + `TimerNotice`) へ変え、最終的に**表示そのものを廃止**した。

- 永続表示は、終わった作業の結果が今動いている作業より長く画面を占有する
- 一時通知にしても、伝えている内容が「Pet が完了を確認できなかった」という
  **ツール内部の都合**でしかなく、ユーザーが取れる行動が無い
- 完了判定を tracker から切り離した結果、「未完了」に相当する状態自体が
  無くなった (完了でなければ、まだ作業中か Idle のどちらか)

関連コード (`StoppedIncomplete` / `Indeterminate` / `NoticeDueUtc` /
`TimerNotice` / `RenderIncomplete` / `IncompleteNoticeTtl`) はすべて削除した。


## completion = root Stop + 20 秒静穏 (2026-08; 2026-09の補助情報は末尾参照)

### progress と completion を完全に分離した

それまでは structured tracker が両方を担っていた。

- progress: 「依頼全体の工程表のどこまで進んだか」の推定材料 (今も同じ)
- completion: 「全件 completed を確認できたか」の判定材料 (**廃止**)

tracker を完了の証拠に使うと、agent 側の status 運用の質がそのまま
通知の正しさになる。status を先に倒せば嘘の完了が出るし、
運用を守れなければ完了通知が出ない。どちらも Pet 側では検証できない。

現在は完了判定に tracker を一切使わない。`FinalizeDue` は
`GetProgress` / `SawStructuredTasks` / `SnapTotal` を読まない
(この不変条件はテストで固定している)。

### completion の定義

**root Stop を受け、その後 `CompletionQuietMs` = 20 秒のあいだ
その作業が再開されなかった。** これだけ。

これは「成果物が正しい」という意味ではなく、prompt / 応答 / 成果物本文を
読まない Pet が観測できる範囲での「一連の作業の終了」でしかない。
この意味を README にも明記している。

- 94% でも / pending が残っていても / tracker が壊れていても /
  tracker が無くても、Stop + 20 秒静穏なら完了通知する
- 100% でも Stop が無ければ完了しない。interrupt で Stop が来ない
  ケースを推測 timeout で完了させない方針は維持
- SessionEnd 単体は完了の根拠にしない。Stop 済みなら SessionEnd が来ても
  candidate を維持し、Stop なしの SessionEnd は静かに片付けて Idle へ戻る

### 継続イベントは「延長」ではなく「取消」

旧 grace は継続イベントで deadline を**延長**していた。20 秒方式では
**candidate を破棄して Working へ戻す**。次に完了できるのは新しい root Stop
が来てからになる。

延長のままだと「作業が止まってから 20 秒」ではなく
「最後のイベントから 20 秒」になり、interrupt 後の残イベントで
勝手に完了してしまう。Codex 側は元々この取消方式だったので、
Claude 側をそれに合わせて統一した。

**既知のトレードオフ**: Claude の hook は async なので、Stop より前に発生した
Task/Todo イベントが Stop の後から届くと、それも継続とみなして candidate が
消える。その turn は次の Stop まで完了通知されない。誤って
「終わったよ！」を出すより出さない方を選んでいる (false negative 優先)。

### 2 秒 / 5 秒 grace の統合

Claude の 2 秒と Codex の 5 秒は、もともと「別の理由による別の値」だった。
20 秒 quiet window はどちらの理由も包含し、意味が完全に同じになったので
1 つの定数・1 つのフィールド (`QuietDueUtc`)・1 本の timer (`TimerQuiet`) へ
統合した。中途半端に旧定数を残さず削除している。

統合したのは deadline 管理だけで、以下は provider ごとに分けたまま:

- Claude と Codex の state 遷移 (`OnEvent` / `OnCodexEvent`)
- Codex の turn 分離。old-turn の遅延イベントは current turn の candidate を
  取消さない (実測 18.6 秒遅延あり)
- Codex の subagent fail-closed (progress のみ無効化)
- MetadataOnly quarantine (tombstone)

### 他に active があれば完了通知を出さない

満了時に他の active session があれば、`終わったよ！` を出さずに
その session を静かに片付ける (音も鳴らさない・後から再キューもしない)。

完全 auto 運用では、終わった作業の通知より今動いている作業の表示の方が
価値が高い。表示 priority を active 優先にするだけでは不十分で、
「他が動いていない瞬間」を狙って通知が割り込むのを防ぐ必要があった。

同時に満了した場合は LastSeq の古い方から処理する。先に見る側からは
残りがまだ active に見えるので、通知が残るのは最新の作業になる。

### 表示 priority

**active (Working / Waiting / Finalizing) > Celebrating**。
Celebrating を active より下に置くことで、通知中に新しい作業が始まったら
その場で作業表示へ切り替わる。過去の完了で現在の作業を隠さない。

`+N` が数える active には Finalizing (Stop 後の静穏待ち) を含める。
ユーザーから見て「作業中…」と表示されている以上、active として数えるのが自然。

### total=1 の plan では % を出さない

`MinProgressTotal = 2`。1 工程だけの plan は「今やっている 1 個」でしかなく、
依頼全体の進捗としての根拠が弱い。50% と出すと誤解を招くので % を出さない。
tracker 自体は保持するので、plan が 2 工程以上に育てば % が出る。

plan が増えて % が下がることは許容する。単調増加させるための補正や
stale な高値の固定はしない (truthful を優先)。

## Claude の quiet grace (旧仕様: root Stop は常に 2 秒)

> **注意: この節は履歴。** Claude 2 秒 / Codex 5 秒の grace は
> provider 共通の 20 秒 quiet window (`CompletionQuietMs`) へ統合された。
> 「root Stop は必ず quiet window へ入る」「完了を決めるのは満了時の
> `FinalizeDue` だけ」という考え方はそのまま残っている。

### 常に grace へ入る

以前は「Stop 時点で全 completed / task なしなら即 Celebrating」だったが、
Claude の hook は async なので

```
TaskCompleted(A)   <- 実際には Stop より前に発生
Stop
TaskCreated(B)     <- だが Stop より後に届く
```

という順で届きうる。A だけを見て完了とするのは false positive。
OpenAI Codex の Hooks source でも async hook は本当に非同期に schedule され、
Anthropic 側でも実処理完了と Task/Todo lifecycle state のずれが報告されている。

よって **root Claude の Stop は、structured task を観測済みかどうかに関係なく
必ず Finalizing へ入る**。task を一切使わない短い依頼でも通知が約 2 秒
遅れるが、その代わり遅延 async event を吸収できる。

### fixed delay ではなく per-session quiet grace

「Stop から 2 秒」ではなく **「最後の関連イベントから 2 秒静か」**。
Session ごとに `ClaudeGraceDueUtc` を持ち、Finalizing 中に同じ session へ
Activity / TaskCreated / TaskCompleted / TaskSnapshot / TaskRemoved /
TaskInProgress が来たら、適用した上で deadline をそこから 2 秒後へ延長する。
Finalizing 中の重複 Stop も無視せず deadline を延長する。

Win32 timer は最短 deadline 用の 1 本だけ (`ArmClaudeFinalizeTimer`)。
発火時に期限の来た session だけを確定し、残りがあれば張り直す。
常時 timer / polling は増やさない。Codex の `ArmCodexGraceTimer` とは
timer ID も定数も別で、意味を共通化しない。

### 早期 celebration を削除

grace 中に 100% になっても「終わったよ！」を出さない。
**最終的な completion を決められるのは quiet grace 満了後の `FinalizeDue` だけ**。
以前あった「Finalizing 中に全完了になった瞬間 Celebrating」は廃止した。

### 判定 (旧仕様)

| 状態 | 結果 (表示) |
|---|---|
| total > 0 かつ done >= total | Celebrating (終わったよ！) |
| total > 0 で pending が残る | StoppedIncomplete (未完了) |
| total > 0 で残りが in_progress のみ | Indeterminate (未完了) |
| total <= 0 (tracker なし / 解析不能含む) | Indeterminate (未完了) |

この表は現在無効。今の `FinalizeDue` は tracker を一切見ない。

### bounded limitation

これで司れるのは **quiet な 2 秒以内に届く遅延 event まで**。
2 秒を超えてから届く Claude async event は完全には保証できない。
それを埋めるには transcript 監視等が必要になり、privacy と
軽量性の方針に反するので採用しない。

### Codex は別物 (変更なし)

Codex の 5 秒 quiet grace (`CodexQuietGraceMs = 5000`) は理由も値も別で、
今回変えていない。Codex 側の変更は「malformed update_plan でも
structured-observed を失わない」だけ。

## Provider / model 表示と `+N`

現在表示中の session に、コンパクトな metadata 1 行を出す。
左に provider + model、右に「他に動いている session 数」を `+N` で表示する。

```
作業中…
Claude · Opus 4.6            +2
tiny-code-pet
全体 推定 67%
```

### model の取得元 (provider ごとに違う)

- **Codex**: `UserPromptSubmit` の共通 input に `model` がある。新しい event ID は
  増やさず、既存 event 20 (CodexPromptSubmit) で空いていた `extra` に乗せる。
  payload は従来どおり 4 行 (session / project / extra / turn)。
  turn ごとに届くので、空なら前 turn の値を残さず「不明」へ戻す。
- **Claude**: 公式仕様上、通常の hook の共通 input に model は無く、
  **`SessionStart` だけ**が受け取る。よって Claude にのみ
  **正規化イベント 11 = SessionMetadata** を追加した。
  既存 event 1〜10 の意味と Claude payload 3 行は変えていない。

### SessionStart を表示へ昇格させない

`SessionStart` は `UserPromptSubmit` より先に来るので、これだけで
「作業中…」を出してはいけない。Session に `MetadataOnly` state を持たせ、

- model を保存するだけ (進捗 / 完了候補 / state は一切触らない)
- 表示候補にしない (rank 0)
- `+N` の active にも数えない

とする。compact 等で Working 中の session へ再度来ても model だけ更新する。
その後 `UserPromptSubmit` が来たら保存済み model をそのまま使う
(`ModelId` は session 単位なので `ResetRequest()` では消さない)。

### Claude の model は mid-session の変更を即追跡できない (既知制限)

`/model` で途中変更しても、次の SessionStart 相当
(startup / resume / clear / compact) まで表示が古い可能性がある。
これを埋めるために transcript 監視 / statusline 乗っ取り / polling /
API call / 環境変数推測 / 追加 LLM call は導入しない。
model が確実に取れないときは **provider だけ表示**を優先する。

### model の sanitize と humanize

- adapter 側で `model` field だけを抽出し、control 文字 (CR/LF/TAB 含む) を除去し、
  40 文字で打ち切る。payload は行区切りなので、これを怠ると Codex の turn_id が壊れる。
- 表示名の短縮は **存在しないモデル名を作らない**ことを最優先に、
  確実に分かる形式だけ (`claude-opus-4-6` → `Opus 4.6`、`gpt-` → `GPT-`)。
  日付 suffix (`-20250805`) はバージョンに含めない。
  未知の形式は変換せず sanitized raw をそのまま出し、長ければ描画時に ellipsis する。

### `+N` の定義

「現在表示中の session とは別に存在する active session 数」。
active に数えるのは **Working / Finalizing / Waiting** の 3 つだけ。
Celebrating / StoppedIncomplete / Indeterminate / MetadataOnly / tombstone は数えない。
Claude と Codex を合わせた総数で、0 のときは表示しない。

### redraw key

`_shownKey` に provider+model 文字列と `+N` を含める。
別 session が開始/終了して表示中 session 自体は変わらない場合や、
SessionStart で model が後から届いた場合も再描画させるため。

### MaxSessions 超過時の evict 優先度

上限 (`MaxSessions`) を超えたときは、まず **MetadataOnly を最古から捨てる**。
metadata-only が 1 件も無いときだけ従来どおり最古 session を捨てる。
prompt 未投入の SessionStart が並ぶだけで実作業中の session が
押し出されるのを避けるため (失うのが model 表示だけで済む方を優先)。
evict された session から後で UserPromptSubmit が来たら通常の Working として
再作成され、model 不明なら provider だけ表示になる (model は推測しない)。

### 完了判定への影響

provider / model / session count は **表示だけの情報**で、
完了判定 (quiet grace / structured tracker / FinalizeDue) には一切関与しない。

## TOPMOST 再保証と通知領域アイコン (2026-08)

### WS_EX_TOPMOST だけでは背面へ回る

実運用で「最初は VS Code より前面 → いつの間にか背面」という現象を確認した。
問題は、TOPMOST の指定が **CreateWindowEx の一度きり**で、
**何らかの理由で TOPMOST を失った場合に再保証する経路が無かった**こと。

- `MoveTo` (bounce の 30ms tick) は `SWP_NOZORDER` で Z-order を触らない
- `UpdateLayeredWindow` も Z-order を変えない
- つまり一度 topmost を失うと、二度と戻る経路が無かった

失った具体的な契機は特定できていない (実測できたのは結果としての背面化だけ)。
そのため修正は原因の除去ではなく「失っても次の機会に戻す」方向にした。

### EnsureTopmost (event-driven のみ)

`SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` を

- 表示内容が実際に変わった再描画のとき (render key が変わったときだけなので自然に debounce される)
- 明示的な Show / 最前面に戻す / tray 左クリック

でのみ呼ぶ。polling・常時タイマー・bounce 毎 tick の再 assert はしない。
`MoveTo` は従来どおり `SWP_NOZORDER` のまま (位置と Z-order の責務を分離)。

他の TOPMOST アプリと押しのけ合う実装 (foreground hook 等) は不採用。
TOPMOST 同士は Windows 標準の前後関係とし、隠れたら tray から復帰させる。

### 通知領域アイコン (Shell_NotifyIcon)

- taskbar ボタンは出さない方針 (`WS_EX_TOOLWINDOW`) は維持したまま、
  管理用の入口を tray に 1 つ置く。WinForms NotifyIcon / 別常駐 helper は不採用
  (純 Win32 P/Invoke、既存の GetMessage loop に WM_APP+1 callback を足すだけ)
- アイコンは PetRenderer と同じ配色でひよこを実行時描画し `GetHicon()`。
  外部画像ファイルを増やさない。`DestroyIcon` で解放
- menu は `TrackPopupMenuEx` + 事前 `SetForegroundWindow` + 事後 `WM_NULL` の
  標準 tray パターン。Pet window は click-through + NOACTIVATE なので
  menu 後に入力を奪い続けることはない
- menu 選択は `WM_COMMAND` で届く (TPM_RETURNCMD 不使用)。テストが同じ
  message を post して同一経路を検証できる
- NOTIFYICONDATA は V1 (szTip まで) レイアウト。バルーンや version 4 の
  機能は使わないため
- 追加失敗は fail-soft: tray が無くても Pet 本体は通常動作
- Explorer 再起動での tray icon 復元 (TaskbarCreated) は今回のスコープ外

### 「ヒヨコを隠す」= visual hide

hide しても process・HWND・mutex・session 管理・hooks 受信・完了判定は
すべて継続する。止めるのは描画 (`RenderCurrent` が early return) と完了音だけ。

- 再表示時は force 描画でその時点の最新 state に追いつく。
  hidden 中に起きた完了の音や bounce を後から再生することはしない
- hidden 中の完了でも Celebrating エントリは通常と同じ寿命で片付ける
  (TimerRevert を直接張る)。再表示した時に過去の完了通知が残らない
- 「hidden だから別 helper を立てる」ことはしない。増える state は
  bool 1 つ + tray handle だけ

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
- **Claude Code Desktop の直結親子は nested ではない (2026-08-27)**: Desktop は
  `Claude.exe` (アプリ) → `claude.exe` (Claude Code エンジン) の直接の親子から
  hook を発火させるため、当初の「claude 個数 >= 2」判定では Desktop の root
  セッションまで nested 扱いになり、全イベントが抑制されていた
  (`bin\debug.flag` での実測: `suppressed:nested ev=PostToolUse`)。
  counting を「**連続して隣接する claude は 1 グループ、グループ数 >= 2 で
  nested**」へ変更した。本物の nested 子 Claude は必ず間に tool のシェル
  (pwsh/bash 等) を挟んでグループが分かれるため、CLI / VS Code / Desktop の
  どの親からの nested も従来どおり抑制される。Desktop 固有の識別子・環境変数
  には依存しない (環境判定そのものを不要にする)。
- テスト時の注意: 独立セッションを装うには WMI
  (Invoke-CimMethod Win32_Process Create) で起動する必要がある
  (通常のツール実行だと自分の子になり抑制されてしまう)。
- 関連: Claude Code 内部 Subagent の完了は SubagentStop であり Stop hook
  は発火しない (実測)。防御として agent_id 付き Stop / TaskCreated /
  TaskCompleted も通知・集計しない。

## Activity indicator (廃止)

かつては PostToolUse 観測時に「● 活動中」(TTL 15 秒) を表示していたが、
完全 auto 運用への簡素化で廃止した。表示専用だった
`LastActivityUtc` / `ActivityTtl` / `TimerActivity` / `OnActivityExpire` も削除。
PostToolUse 自体は Waiting 解除・structured tracker・completion candidate
取消 (Claude / Codex とも) に今も必須で、event 処理は残している。

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
| 24 | Stop | completion candidate (quiet window) | - |
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

### Codex の Stop は完了確定ではない -> quiet window

実測 (Phase C):

- 最初の Stop -> 約 1.88 秒後に continuation 開始 (UserPromptSubmit の再発火なし・
  同一 turn_id) -> 約 3.69 秒後に 2 回目の Stop。
- 最初の Stop payload から「別 hook が continuation を返すか」は判別不能。
- `stop_hook_active` は最初 false / continuation 後 true になるが、
  **true でも「今回が最終 Stop」を保証しない**ので判定に使わない。
- Stop は rollout の `task_complete` より約 130〜165 ms 早い。

よって Codex では **Stop = completion candidate**、静穏
(`CompletionQuietMs = 20000`) で確定する。静穏中に同一 turn の
PostToolUse / update_plan / PermissionRequest / SubagentStart / SubagentStop が
来たら candidate を破棄し、2 回目の Stop が来たら 20 秒を最初から数え直す。

この「Stop の後に continuation があり得る」という理由は今も有効で、
Claude 側の「async hook の到着順ゆれを吸収する」という理由と合わせて
20 秒の quiet window が両方を包含している。

> **旧仕様**: Codex 5 秒 (`CodexQuietGraceMs`) / Claude 2 秒 (`FinalizeGraceMs`) と
> 別値・別 timer・別判定経路だった。意味が同じになったので統合した
> (「completion = root Stop + 20 秒静穏」節)。turn 分離・subagent fail-closed・
> old-turn 破棄といった Codex 固有の判断は分離したまま。

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
- 完全 auto 運用化に伴い、確認 UI (「確認して！」) と警告音は廃止。
  Waiting state は completion 互換のため残るが描画は「作業中…」と同じ。
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

`CodexPetNotify` / `ClaudePetNotify` の**両方**がペットを自動起動するときは
`ProcessStartInfo.UseShellExecute = true` を使う。**戻さないこと。**

- `UseShellExecute = false` は CreateProcess を `bInheritHandles = TRUE` で呼ぶため、
  hook の stdin/stdout/stderr がそのまま常駐ペットへ継承される。
  helper が終了してもペットが hook の stdout を掴んだままになるので、
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

`ClaudePetNotify` も同じ問題を持っていたため、Codex 対応とは別 commit で
同じ方式へ修正済み (Claude の hook は全て async なので潜在的だった)。
修正後の実測: 両 helper とも stdin / stdout / stderr のいずれも
helper 終了後に排他オープンできる (= ペットが持っていない)。
Claude 側の変更はこの 1 行だけで、event 1〜10 ・payload 解析・
nested 抑制は一切触っていない。

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


## 2026-09-10: 忍者とサブエージェント分身

ユーザーの明示依頼により、旧版の静止ヒヨコ・分身UIなしという方針を変更。
状態機械は維持し、SpriteAnimatorとSubagentRosterを分離した。
既存PetRendererはテキストHUDを担当し、生成した透過PNGを合成する。
Win32 / C# 5を維持し、.NET Framework同梱System.Web.Extensionsで小さな
アニメーション定義JSONだけを読む。新規パッケージは導入しない。

分身は各セッション内の一意identityを追跡する。start重複を無視し、stop先着を
tombstoneで記憶して遅延startの復活を防ぐ。active/stop集合は各256件まで。
上限以降は新規分身を表示しない安全側の扱い。表示は6体まで。
サブエージェントの状態はイベント観測の範囲であり、実プロセスの生存証明ではない。
Stopがサブエージェント自身の応答終了を示す場合、別Hookによる継続・再開まで
Petが把握できるとは限らない。新規startを伴わない同一IDの再開は表示しない。
Claudeにはturn_idがないため、新しい依頼をまたぐ遅延startを厳密に分離できない。
Codexは既存のturn照合を維持し、旧turn SessionEndで現在の分身を消さない。
親の終了・完了・新規依頼で分身をクリアする。子の終了は親の完了根拠にしない。

Claudeのevent 12/13を追加し、Codexの26/27はextraのみ拡張する。
両adapterはトップレベルagent_idのみを選びSHA-256化し、Petへ送る。
IDがなければ分身を作らない。本文、transcript、追加APIは使わない。

公式仕様を確認した出典:
- https://code.claude.com/docs/en/hooks#subagentstart
- https://code.claude.com/docs/en/hooks#subagentstop
- https://learn.chatgpt.com/docs/hooks#subagentstart

実発火テストは未実施。合成metadataを使用したローカル回帰テストを実施し、
正規イベントの取り扱いと描画を確認した。旧版のメモリ実測値は新版に適用しない。
常駐設定の変更・新しいHookのインストールは別途ユーザー承認後に行う。

### 常駐版の受信確認

忍者版の実ウィンドウに対し、Claude/Codex形式の開始・分身6体の出入り・
セッション終了を合成入力し、28件のWM_COPYDATA応答を確認した。
これは実アプリのIPC確認であり、Claude/Codex自身によるHook発火の検証とは区別する。
Codexの非管理Hookはユーザーの信頼操作が必要。信頼状態をツール側で偽装しない。

現行の公式仕様ではHookは既定で有効となっているため、install-codex-hook.ps1は
明示的な `hooks = true` がないことだけを理由に「無効」と断定しない。
config.tomlを変更しない方針は維持する。

## 2026-09: 背景作業・異常終了・中断の補助情報

ユーザー承認に基づき、2026-09-10の再調査結果をローカルへ反映。
既存の20秒静穏と継続時の候補取消しを維持し、次を追加した。

- Claude Stopのextraへbackground-unknown / background-clear / background-pendingを渡す。
  pendingならWorkingへ戻して候補を取り消す。新しいroot Stopが必要。
  欠落は旧方式を維持し、空配列とは区別する。不正metadataはpending。
- 公式のin-flight配列にあるmonitor以外の作業を保留対象にする。
  monitorとsession_cronsはこのturnの完了条件から外す。
  shellが常設かは本文を読まないので判断せず、保守的に保留する。
- Claude StopFailureはevent 14。状態削除とtombstoneで遅延Stop/activityを抑制する。
- Codex Interruptはevent 28。turn一致を必須とし、状態と候補を破棄する。
  中断後の遅延PermissionRequestもtombstoneを解除しない。
- 上記の中断・失敗で完了音を鳴らさない。進捗件数は完了判定に使わない。

既知の限界: Claudeの既存3行契約にはturn identityがなく、新しい依頼の後に
前依頼の非同期イベントが届いた場合の完全な識別はできない。
背景作業が終わっても新しいroot Stopが届かなければ通知しない。
予約された作業の達成や成果物の正しさを保証するものではない。

変更箇所: src/BackgroundWork.cs、src/Notify.cs、src/CodexNotify.cs、src/Pet.cs、
各Hookインストーラ、build.ps1、test.ps1、tests/NinjaTests.cs。
ローカルの未コミット変更であり、GitHub公開済みとは扱わない。

検証: test.ps1で66 assertions PASS、500frames GDI delta=0。
ステージングbuild成功、3つのexeをバックアップ後にローカルbinへ反映し、
Petのみ再起動、実行ファイルのハッシュ一致を確認。
Codex adapterの合成入力3件（Interrupt、計画件数、Stop）も正常。
これは実AIのInterrupt/StopFailure配信・長時間作業の結合確認ではない。

計画ツール: 利用者設定に tools.update_plan.enabled = true を追加。
現在の応答で提供済みのツール一覧は変化せず、次の応答での実提供・Hook到達は未確認。
Hook設定: StopFailureとInterruptを追加。公式hooks/listでは設定エラー0、
従来7イベントtrusted、新規Interruptのみuntrusted。信頼操作は未完了。

App Server: 現在のDesktopに関連するプロセスはlistener指定なし、または明示stdio。
外部購読可能なWebSocket/Unix listener指定は観測されなかった。
別App ServerはDesktop既存turnの通知購読を証明しないため採用しない。
一時的な診断プロセスではinitializeとhooks/listのみを実行して終了した。
モデル推論・会話開始・常駐監視は行っていない。

根拠: docs/PROGRESS_COMPLETION_REVIEW.md と公式Hooks/App Server仕様。

追記: 新規Interruptの信頼操作もユーザーから委任され、公式CLIのHooksレビューで
対象コマンド・イベント・3秒timeoutを確認して、その1件のみ承認した。
再度hooks/listを実行し、全8件enabled/trusted・設定エラー0を確認。
上記の「信頼操作は未完了」はこの確認により解消済み。

### 実受信の検証追記（2026-09-10）

- 現在のDesktop会話からPostToolUse(21)がPetへ届き、Working・candidate=falseとなることを確認。
- 実時間25秒のshell待機中、Stop 0件・完了遷移0件。Petは稼働を継続。
- 合成入力11件を実adapterへ与え、実Petの受信状態7条件を確認。
  計画1/1/4で37%、StopでFinalizing、Interruptで削除、遅延Stopは無効。
  Claude背景shellのStopはWorking継続、StopFailure後の遅延Stopも無効。
- 合成入力と実AIによるHook発火は別。計画ツールは現在の会話にまだ提供されていない。
- config/readのToolsV2出力schemaはweb_searchしか公開しない。このAPIにupdate_planが
  出ないことを「設定がfalse」と解釈してはいけない。ファイル上はtrueを維持。
- 実AIでの追加テストは自動承認レビューに拒否されたため未実行。
  理由は追加モデル呼び出しの利用枠・料金消費に対する明示承認不足。
  現行会話の観測とローカル合成テストのみを実施した。

診断時の個人情報保護のため、既存debug.flagで有効になるログをstatus-debug.logへ変更。
イベント種別、状態、推定進捗、候補有無、送信成否のみを記録し、session/turn/project/
prompt/応答/環境変数値/座標は出力しない。fixtureの識別子・本文が出力にないことも確認。
診断終了時に今回作ったdebug.flagを除去し、通常の無記録状態へ戻した。
変更はローカルの未コミット状態。実AIの進捗・完了・中断の結合検証は完了扱いにしない。

### 許可された実AI検証3回の結果

利用者が最大3回を明示承認し、以下の3実行を行った。追加実行はしていない。

1. Codex CLI（永続化なし）: 実update_plan -> Pet event22で25%、shellの25秒待機、
   実update_plan -> 100%、root Stop -> Finalizing、SessionEndでも候補維持。
   実行終了コード0。20秒後は他のactive（本会話）があるため既存仕様どおり通知抑制。
   完了音が実際に鳴った検証ではない。
2. Codex一時App Server: テストturnのcommandExecution開始後にturn/interrupt。
   実turn.status=interrupted、Pet event28受信でstate=0/candidate=falseを確認。
   一時プロセスは終了し、監視サービスは追加していない。
3. Claude CLI（永続化なし）: 実StopとSessionEndを受信。Stopのbackground_tasksと
   session_cronsフィールドは存在し、背景件数は0だった。ツール実行は観測されず、
   指定した背景sleepを起動するケースには到達していない。終了コード0は
   背景作業保留の実AI検証成功を意味しない。StopFailureも実AIでは未観測。

CLIで計画ツールが動いたので、設定とPet受信経路は実証された。一方、現在のDesktopの
本会話にはupdate_planがまだ提供されていない。この会話の進捗表示復旧は未完了。
Desktopの設定再読込・既存会話への適用は別途必要であり、再起動で直ると断定しない。
本会話を終了するアプリ再起動は今回行っていない。

実行記録はbin/LIVE_TEST_USAGE.md（ignored）。診断記録はstatus metadataのみ。
診断終了後は今回作成したdebug.flagを除去する。

### Desktop進捗設定の反映調査

DesktopとCLIは同じCodex実行ファイルを利用。関連プロセスの起動引数に
計画ツールのfalse指定やprofile指定は確認されなかった。
公式mainのspec_plan.rsではturn_context.config.update_plan_enabledにより
PlanHandlerを登録する。既存Desktop会話が旧設定を保持している可能性を調べるため、
Desktopとその子孫に属するApp Serverを再起動して再読込する方針とした。
これは原因確定ではなく切り分けであり、再起動後のツール提供確認が必要。

以前の子プロセス型再起動helperがアプリと一緒に終了した経験を踏まえ、
一回限りのWindowsタスクで独立ランチャーを用意。Preflightを実行し、
IndependentLauncherReady=Trueを確認。停止対象は取得済みPID・起動時刻・実行パスを
照合したDesktop本体と、その子孫のApp Serverのみ。Petは停止しない。
ランチャーは実行後に自分の一回限りタスクを解除する。
新規モデル呼び出し・追加AIテストはしていない。

### Desktop進捗表示の復旧確認（2026-09-10 12:44 JST）

独立ランチャーの結果はAppRestarted=True。再起動後の本会話にupdate_planが提供され、
本会話から実際に更新した計画（completed 1 / in_progress 1 / total 3）が
Codex event22として実Petへ届き、state=Working / progress=50 / candidate=Falseを確認。
CLIだけでなく、このDesktopの既存会話についても進捗入力の復旧を確認できた。
再起動前の設定保持が原因だったことを支持する実測結果であり、追加モデルテストは不要だった。

再起動直後のランチャー記録ではPetRunning=Falseだった。Petを明示的に停止する命令は
出していないが、「Petは動いたまま」とした事前説明は結果と一致しなかった。
戻ったメッセージの後にはPetが自動起動しており、稼働と検証済みbinaryの一致を確認。
再起動用の一回限りタスクも解除済み。

この項目はDesktopの進捗復旧だけを完了する。Claudeの背景作業・StopFailureの実AI検証、
他のactiveがない場合の実完了音、GitHub反映は別の未完了項目として残る。


## Current work and elapsed time (2026-09-10)

ユーザーの明示依頼により、進捗率に計画上の現在工程と依頼の経過時間を追加。
本文非読取の例外は単一in_progressの工程名(step / activeForm / content)だけとする。
工程名は進捗計算・完了判定に使わず、最大120文字でメモリ内だけに保持する。
未知・並行工程・subagent由来の疑いは工程名を出さず、実行中のコマンドを推測しない。

既存snapshotイベントのextraをc/i/t|base64(label)へ拡張。改行数とイベント番号は変更せず、
新Petは旧c/i/tも受信する。旧Petは拡張snapshotを解釈できないため、Petとadapterを一緒に更新する。
status件数も同じ構造走査に移し、tool_responseや工程名内のstatus文字列を数えない。
Prompt・応答・commandは構造を飛ばすだけでdecodeしない。新しい依存package・Hookは追加しない。

経過時間はStopwatchの単調時計で、PromptSubmitの受信時にリセット。
開始を未観測ならsession初観測から計測し「観測から」と表記する。
表示中の既存animation tickを利用し、秒が変わったときだけHUDを更新。
非表示では停止、再表示時に追いつく。時計更新ではTOPMOST再保証を行わない。
経過時間は稼働の証明・ETA・進捗の補間・完了条件にしない。

HUDの上端領域を60px増やし、忍者の画面上の位置は維持する。
工程名は最大2行で省略し、進捗率のない場合にも経過時間を表示する。
Waiting / Finalizingはタイトルを維持し、補助行のみ実際の待機状態を示す。

検証: 85 assertions PASS、build成功、500フレーム後GDI delta=0。
status抽出境界、ラベルの制御文字・長さ制限、旧payload、旧turn、新依頼リセット、
並行工程・subagent抑制、単調時計の時表示境界、HUDキャッシュ、非表示停止と再表示を確認。
100/125/200%のオフスクリーン描画を生成し、通常工程名と長い工程名の収まりを確認した。

ローカルのPet/両adapterをバックアップ後に反映し、配置binaryがstageと一致することを確認。
実Desktop会話のupdate_planからevent22、progress=83、workLabel=Trueを受信した。
更新途中のPet再起動で開始Hookは未観測のためobservedStart=Falseとなり「観測から」で表示。
次のPromptSubmitで依頼開始の時計へ切り替わる。追加のモデルテスト・Hook設定変更は行っていない。
診断flagは検証後に除去。今回の変更はローカルのみで、commit/pushは未実施。
