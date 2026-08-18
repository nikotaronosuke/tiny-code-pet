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

絶対に壊してはいけない設計原則 (AGENTS.md の正本を参照):

- native / event-driven / idle CPU ほぼ 0 / 小 RAM。
- status metadata のみを読む privacy 方針。
- 進捗を捏造しない (時間・tool 回数・LLM 問い合わせによる % 生成禁止)。
- 完了の断定は誠実に (false positive「終わったよ！」を出さない)。

## Codex extension direction (未実装)

将来 Codex 対応を追加する場合の第一候補は provider adapter 方式:

```
Claude Code Hooks → Claude adapter ┐
                                   ├→ normalized pet events → Pet 本体
Codex Hooks       → Codex adapter  ┘
```

- 現在の ClaudePetNotify.exe が事実上の Claude adapter。正規化イベント
  (1〜10) と WM_COPYDATA payload が provider 中立の内部プロトコル。
- Pet 本体の state machine・完了判定・進捗哲学は provider 共通とし、
  provider 固有の差異は adapter 側で吸収する。
- Claude と Codex の同時使用で session/state が衝突しない設計を検討する
  (session_id の名前空間分離等)。
- **注意: Codex 固有の hook / event / payload 仕様は本ドキュメント作成
  時点で未調査。実装フェーズで Codex 公式仕様と実 payload を確認する
  こと。想像で仕様を書かない。**
