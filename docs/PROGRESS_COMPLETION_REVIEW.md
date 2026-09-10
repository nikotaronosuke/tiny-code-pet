# 進捗・完了判定の再調査

調査日: 2026-09-10。対象: `aac3319` の実装と設計履歴、現在の公式仕様。
ローカルCLI確認値: Claude Code 2.1.258 / Codex 0.153.4。
CLIのバージョン確認は、Desktop内の全経路で同じ機能が使える証明ではない。

## 結論

既存の進捗表示を復旧することと、判定精度を改善することは別の作業。
現在の公式仕様にはPetが未利用の終了・中断・背景作業の情報があり、
「元の情報はすべて同じなので改善しない」という判断は不適切だった。
ただし、依頼全体の実作業量を直接測った百分率は、今回の調査では確認できなかった。

## 過去の対策と現在の挙動

- `src/Pet.cs` の完了判定は root Stop + 20秒の静穏。
  無反応が20秒続いただけでは完了しない。
- 静穏中に継続イベントを受けると候補を取り消す。新しいroot Stopが必要。
  これは長時間作業中の誤通知を避けるうえで維持すべき対策。
- `47b6f3b` で20秒の共通静穏方式へ変更。
  `docs/DESIGN_DECISIONS.md` には、計画の全項目完了を通知条件にした方式を
  更新忘れ・早すぎる完了登録への依存から取り下げた記録がある。
- 同資料の旧Codex実測では、Stop後に同一turnの作業が再開している。
  Stopを最終終了と同一視しない根拠になる。ただし旧バージョンの観測である。
- 最初の `5cb1ab6` は明示的な task_complete を受ける通知設計だった。
  ユーザーが経験した長時間無応答時の誤通知について、今回確認した履歴だけでは
  発生版・直接原因までは確定できない。単なるidle timeoutが原始仕様だったとは断定しない。

## 今取り込める情報と限界

| 対象 | 現在確認できる情報 | Petへの意味 |
| --- | --- | --- |
| Codex進捗 | PostToolUseのupdate_plan引数、App Serverのturn/plan/updated | どちらも計画項目の状態。受信方法だけで残作業量の実測値にはならない |
| Codex終了 | App Serverのturn/completedにcompleted / interrupted / failed | Stopより明確なturn終了の区別。ただし依頼の達成・成果物の正しさではない |
| Codex中断 | Interrupt Hook | 中断を時間切れで推測せず扱う材料。現行adapterは未対応 |
| Claude背景作業 | Stopのbackground_tasks / session_crons | 応答終了後も背景作業・再起動予定があることを見分ける材料。現行adapterは未利用 |
| Claude異常終了 | StopFailure Hook | APIエラーによる終了を通常Stopと分離する材料。現行adapterは未対応 |

公式資料:

- [Codex Hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex App Server](https://learn.chatgpt.com/docs/app-server)
- [Claude Code Hooks](https://code.claude.com/docs/en/hooks)
- [Codex 0.152.0 release](https://github.com/openai/codex/releases/tag/rust-v0.152.0)
- [Codex plan handler参考実装](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan.rs)

参照したplan handlerでは、update_plan引数を解析してPlanUpdateイベントを送る。
これは計画を元にすることの裏付けだが、mainブランチの参照実装であり、
Desktopのすべての経路・版で同一の配信順序になることまでは証明しない。
App Serverに変えれば自動的に精密な%が得られる、という説明にはできない。

Codex 0.152.0のリリース記録にはupdate_planの既定無効化と
`tools.update_plan.enabled = true` の有効化方法がある。
この会話の利用可能ツールにはupdate_planがなく、設定にも明示値がなかった。
進捗が表示されない原因候補として強いが、有効化後の実Hook到達は未検証。

## 推奨する変更順

1. **進捗入力の復旧を実測する。** 計画ツールの提供状況、Hook到達、Petの表示を順に確認。
   現行式 `(completed + 0.5 * in_progress) / total` は等重みの工程推定。
   表示復旧を「作業量の測定精度が上がった」とは説明しない。
2. **終了判定の補助情報を追加する。** Claudeの背景作業、StopFailure、CodexのInterruptを
   合成イベントで検証した後、実環境で観測する。従来の候補取消しは維持する。
   background_tasksの欠落と空配列は区別する。定期監視が存在するだけで永遠に
   完了できなくならないよう、依頼との対応・終了条件を設計する必要がある。
3. **Codex App Serverは接続可能性を先に調べる。** 別プロセスを起動しただけで
   今使っているDesktopの会話通知を購読できるとは確認できていない。
   現行AGENTS.mdも常駐App Serverを禁止しており、採用時は設計ルールの変更を伴う。
4. **検証済みは別の証拠にする。** 計画完了・応答終了・任意コマンドのexit 0だけでは
   要求達成の証明にならない。対象の版と必要な検証項目に結び付いた結果が必要。

新しいmetadataを利用する場合も、本文・コマンド文字列・cron promptは読まない。
件数、状態、種類、フィールド有無など必要最小限で処理する。

## 検証済みと未検証

現行 `test.ps1` を実行し47 assertionsがPASS。
500フレームの描画チェックはGDI delta=0。
これらは既存状態遷移・描画などの回帰検証であり、実AIの新Hookを確認した結果ではない。
本調査で稼働中Pet・Hook設定・完了ロジックは変更していない。

後続実装で必要な実環境検証:

| ケース | 確認点 |
| --- | --- |
| 20秒以上かかるツール・無応答 | root Stopなしで完了しない |
| Stop後の継続 | 候補取消し、新しいStopなしでは完了しない |
| 通常終了 | 対象turn終了から通知までの順序・重複の有無 |
| ユーザー中断・API失敗 | 通常完了音を出さず状態を解消できる |
| 背景shell・分身 | rootの応答終了だけで依頼終了と誤認しない |
| 定期監視 | 関係のない常設監視で通知が永久抑制されない |
| 2項目以上の計画 | 実Hookのstatus件数から進捗表示まで通る |
| 計画変更・分身・遅延イベント | 誤った高い進捗や他turnの結果を混ぜない |

新Hookの実機配信とDesktopへのApp Server接続は未検証。
したがって、本書は調査結果・実装方針であり、新機能の動作保証や実装完了報告ではない。
