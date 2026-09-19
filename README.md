# Tiny Code Pet 🥷

[![Build](https://github.com/nikotaronosuke/tiny-code-pet/actions/workflows/build.yml/badge.svg)](https://github.com/nikotaronosuke/tiny-code-pet/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-blue)

> Claude Code と Codex のための、小さな Windows ネイティブのデスクトップ Pet。

Claude Code / Codex の作業状況・依頼全体の推定進捗・作業終了を、
画面右下の小さな忍者で確認できる **Windows ネイティブのデスクトップ Pet** です。
Claude Code と Codex を同時に使っても session / 状態は衝突しません。

![Tiny Code Pet preview](docs/assets/ninja-working.gif)

**一目でわかる特徴**

| | |
|---|---|
| 🤖 **Claude Code + Codex 対応** | 1 匹の Pet が両方を監視。どちらか片方だけでも使える |
| 🪟 **Windows native** | 純 Win32 (C# P/Invoke)。Electron / WebView / Node 常駐なし |
| 🚫 **邪魔をしない** | クリック透過・タスクバー/Alt+Tab 非表示・focus を奪わない |
| 🥷 **tray から操作** | 表示 / 隠す / 最前面に戻す / 終了 |
| 📊 **依頼全体の推定進捗** | 「今のタスク」ではなく依頼全体の工程表から算出 |
| ✅ **root Stop + 20 秒静穏で完了** | tracker の状態に依存しない誠実な完了判定 |
| 🔒 **privacy** | prompt / 応答 / ソース本文を一切読まない |
| 🥷 **忍者と影分身** | 待機・作業・完了のアニメーションと、サブエージェント最大6体の表示 |

> **なぜこの設計にしたか:** [Owner Decision Log](docs/OWNER_DECISIONS.md)  
> 実測で捨てた案、完了判定を作り直した理由、AIの作業を重くしてまで進捗表示を滑らかにしなかった判断をまとめています。

## 表示

完全 auto 運用向けに、見える状態は 3 つだけ:

| 状態 | 表示 | 意味 |
|---|---|---|
| Idle | 🥷 + `Tiny Code Pet` | 何もしていない。呼吸・瞬きの低速ループ |
| 作業中 | 🥷 + 「作業中…」(+ 「**全体 推定 N%**」) + project名 | 工程名と経過時間を併記。入力・承認待ちや終了通知後の待機中は補助行に表示 |
| 完了 | 🥷 + 「終わったよ！」+ project名 | root Stop の後 20 秒間その作業が再開されなかった (決めポーズを1回再生・通知音1回・約5秒後に Idle) |

**「未完了」表示は無い。** 完了と言い切れない停止は何も出さずに Idle へ戻る。

表示中の session には **provider + model** の 1 行が付く (`Claude · Opus 4.6` /
`Codex · GPT-5.6-codex`)。model を取れないときは provider だけ。
同行の右端の **`+N`** は「他に動いている session 数」で、
Working / Finalizing / Waiting の session だけを数える (0 なら非表示)。

確認要求 UI・警告音・activity indicator は廃止した。音が鳴るのは完了時の 1 回だけ。

## 主な特徴

- Native Win32 (C# P/Invoke)。**Electron / WebView / Node 常駐 / localhost サーバー / DB 一切なし**
- 状態監視は event-driven (Hooks 連携)。polling なし。表示中のみアニメーションtimerが動く
- 背景完全透過・枠なし・タスクバー/Alt+Tab 非表示・常に最前面
  (通常ウィンドウより前。TOPMOST を失っても表示更新時に自動復帰)
- **クリック透過**: キャラの背後にある VS Code や Chrome をそのまま操作できる
- **通知領域 (system tray) の 🥷 アイコン**から 表示 / 隠す / 最前面に戻す / 終了 を操作
- 依頼全体の推定進捗表示 (新 Task システム / TodoWrite の両対応)
- 進捗と完了判定は完全に独立 (進捗 % は plan から、完了は Stop + 静穏から)
- 複数セッションの同時追跡 (優先度付き表示)
- 別の Claude セッションのツール内から起動された子 Claude (`claude -p` 等) の通知抑制
- Subagent 完了の誤通知防止
- **Codex 対応** (別 adapter / provider + session + turn で状態分離)

## 忍者アニメーションと分身

- メイン: 待機は約4秒静止して短く瞬き・ごく小さな呼吸。作業は200ms間隔で手元だけ動かす。
  頭・足・マフラーは固定。完了は8フレームを1回再生する。
- 分身は400ms間隔でメインよりゆっくり動く。
- [待機プレビュー](docs/assets/ninja-idle.gif) / [作業中プレビュー](docs/assets/ninja-working.gif)
- 分身: 表示中の親セッションの `SubagentStart` / `SubagentStop` に連動。
  最大6体を小さく並べ、超過分は分身の横に `+N` で表示する。
  HUDの `+N` は従来どおり別セッション数で、分身とは別の情報。
- 同じPNGを共用し、分身の再生タイミングをずらす。出入りに短い煙の演出。
- identityが取れない場合は人数を推測しない。重複・終了先着を抑制し、
  新しい依頼・セッション終了・親の完了で分身をクリアする。
- 非表示中はアニメーションtimerを停止。状態監視・完了判定は継続する。
- 素材: `assets/ninja/ninja.png` (1024×384、128pxセル×8列×3行)。
  `ninja.json` に各動作の行・フレーム数・速度・ループ指定を持つ。
  PNG/JSONはexeに埋め込まれ、配布時に追加ファイルは不要。

## 軽さと検証

旧ヒヨコ版の「待機CPUほぼ0 / RAM十数MB」は忍者版の測定値ではありません。
忍者版は表示中に低FPSの再描画を行います。常駐版のCPU・メモリは実環境での計測が必要です。
テキストHUDはイベント時と経過秒が変わった時だけ再生成し、それ以外のフレームではキャラクターを合成します。
TOPMOSTの再保証は状態変更・明示操作時だけで、フレーム更新では行いません。

`./test.ps1` で状態遷移、分身の重複・順序逆転、旧ターン除外、20秒静穏、
透過、アニメーション、非表示timer停止、描画リソースを検証できます。
ローカル検証では85項目通過、500フレーム後のGDIオブジェクト増加は0でした。
100% / 125% / 200%スケールのオフスクリーン描画も確認しています。
実際のClaude/Codexが分身Hookを発火するところまでの結合検証は未実施です。
Codexデスクトップアプリでは、Hook承認後にアプリ本体と内部プロセスを再起動し、
実際の会話に連動した「作業中…」とprovider/model表示を確認しています。
この確認は、実セッションの分身Hookや完了音までの結合検証を意味しません。

## しくみ

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
Win32 layered window  … UpdateLayeredWindow で ARGB 描画 (表示中は低FPSでキャラクターを更新)
```

Codex は別の adapter を通る (Claude 側の契約は一切変えていない):

```
Codex Hooks (hooks.json)
  UserPromptSubmit ── PostToolUse(.*) ── PermissionRequest(.*)
  Stop ── SessionEnd ── SubagentStart ── SubagentStop
        │  stdin の JSON から status metadata のみ読む
        │  (hook_event_name / session_id / turn_id / cwd / tool_name / plan[].status)
        ▼