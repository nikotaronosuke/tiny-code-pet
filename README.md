# Claude Desktop Pet 🐣

Claude Code の状態を、デスクトップ右下の小さなひよこを見るだけで把握できる
**超軽量デスクトップ通知キャラクター** です。

| 状態 | 表示 | 意味 |
|---|---|---|
| Idle | 🐣 + `Claude` | 何もしていない。完全静止 |
| Working | 🐣 + 「作業中…」+ project名 | 放置してよい (グレーのピル・静止) |
| Waiting | 🐣 + 「確認して！」+ project名 | **permission承認待ち。見に行く必要あり** (オレンジ吹き出し・軽く2回ピョコ・警告音1回) |
| Completed | 🐣 + 「終わったよ！」+ project名 | 完了 (白吹き出し・3回ピョコピョコ・通知音1回・約5秒後に次の表示へ) |

- 背景完全透過・枠なし・タスクバー/Alt+Tab 非表示・常に最前面
- **クリック透過**: キャラの背後にある VS Code や Chrome をそのまま操作できる
- 完全 event-driven。polling なし。アイドル時は `GetMessage` でブロック: **CPU 0% / GPU 0**
- Electron / WebView / Node 常駐 / localhost サーバー / DB 一切なし

## 実測値 (Windows 11, 2026-08-17, Phase 3 時点)

| 項目 | 実測 |
|---|---|
| 待機時 Private Working Set | 約 13.4 MB (起動直後) 〜 14.3 MB (多数の状態遷移後の定常値) |
| 待機時 CPU (60秒計測) | ほぼ 0 ms (イベント無し時は完全 0%) |
| 待機時 GPU | **0** (GPUエンジンインスタンス自体が0個) |
| Working / Waiting 表示中 | Idle と同じ (静止ビットマップ、タイマーなし) |
| アニメーション1回の CPU 累計 | 約 15〜50 ms |
| 連続 18 サイクル後のメモリ | 14.31 MB で増加停止 (GDI/USER/ハンドル数は完全に一定) |

## アーキテクチャ

```
Claude Code hooks (user-level settings.json / 全て async・fire-and-forget)
  Stop ── UserPromptSubmit ── Notification(matcher=permission_prompt)
  PostToolUse(matcher=*) ── SessionEnd
        │  stdin に JSON (session_id / cwd / hook_event_name / agent_id のみ読む)
        ▼
ClaudePetNotify.exe   … Hook Adapter。正規化イベントへ変換して即終了
        │  WM_COPYDATA: dwData=イベント種別, payload="session_id\nproject名"
        │    1=task_complete 2=prompt_submit 3=permission_prompt 4=activity 5=session_end
        ▼
ClaudePet.exe         … 常駐ペット。session_id 単位の状態機械
        │  (なし) → Working → Waiting → Working → Celebrating → (削除=Idle)
        ▼
Win32 layered window  … UpdateLayeredWindow で ARGB 描画 (状態変化時のみ)
```

- 実装: C# (P/Invoke による純 Win32)。**.NET Framework 4.8 同梱の csc.exe でビルドするため追加インストール不要**
- タイマーはアニメーション中と celebration 表示中のみ。終了後は必ず `KillTimer`
- ペット未起動時は Stop / UserPromptSubmit / permission_prompt で自動起動 (高頻度な PostToolUse では起動しない)

### 状態遷移 (session_id 単位)

```
(セッション未登録)
   ↓ UserPromptSubmit
Working ←──────────────┐
   ↓ Notification        │ PostToolUse / UserPromptSubmit
Waiting ────────────────┘   (permission承認後、次のtool完了で自動解除)
   ↓ Stop
Celebrating
   ↓ バウンド + 約5秒
(エントリ削除 = Idle)

SessionEnd → エントリ削除 (Celebrating中は celebration 終了まで維持)
```

### 複数セッション

内部状態は session_id 単位で分離。表示はペット1匹で、priority は

**Waiting > Celebrating > Working > Idle**、同priorityなら最新イベントのセッション。

例: `benri-mcp` が Waiting、`kimete-log` が Working なら「確認して！benri-mcp」を表示し、
benri-mcp が解消 (承認して作業再開 or 完了) すると「作業中…kimete-log」へ自動で戻る。

- セッションテーブルは最大8件。超過時は最古を削除。4時間イベントの無いセッションも削除
- 全て in-memory。永続化なし

### Subagent の扱い

- Claude Code 内部の Subagent 完了は `SubagentStop` という別イベントであり、`Stop` hook は発火しない (実測確認済み)
- 防御として、`Stop` payload に `agent_id` が含まれる場合も通知しない
- `claude -p` 等で明示起動した別プロセスの Claude は独立セッションとして正当に監視される

### プライバシー

Hook の stdin JSON から読むのは `hook_event_name` / `session_id` / `cwd` / `agent_id` のみ。
プロンプト本文・応答本文・tool 入出力・APIキー等は一切読み取らず、保存も送信もしない。
ネットワーク通信なし。

## ビルド

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

`bin\ClaudePet.exe` (常駐本体) と `bin\ClaudePetNotify.exe` (Hookヘルパー) が生成される。

## 起動・操作

```powershell
.\bin\ClaudePet.exe                       # 常駐開始 (二重起動は自動防止)
.\bin\ClaudePetNotify.exe --test 名前     # 完了通知の手動テスト
.\bin\ClaudePetNotify.exe --send 3 s1 名前 # 任意イベントの手動テスト (3=確認して!)
.\bin\ClaudePetNotify.exe --quit          # 常駐ペットを終了
```

ログイン時に常駐させたい場合は `shell:startup` に ClaudePet.exe のショートカットを置く (任意)。

### デバッグ

`bin\debug.flag` という空ファイルを置くと `bin\debug.log` へイベントが記録される
(通常時は完全に無効)。調査後は flag と log を削除すること。

## Claude Code Hook との接続

```powershell
pwsh -File install-hook.ps1
```

ユーザーレベル設定 `%USERPROFILE%\.claude\settings.json` に以下の5つの hook を **追記** する
(既存 hooks は一切変更しない。イベント単位で冪等。実行前に
`settings.json.backup-claudepet-<日時>` を自動作成)。

| イベント | matcher | 用途 |
|---|---|---|
| `Stop` | なし | 完了通知 |
| `UserPromptSubmit` | なし | Working 開始 |
| `Notification` | `permission_prompt` | 確認して！ (idle_prompt 等は発火させない) |
| `PostToolUse` | `*` | Waiting 解除 (承認後の作業再開検知) |
| `SessionEnd` | なし | セッション後片付け |

各エントリは `{"type":"command","command":"<path>/ClaudePetNotify.exe","timeout":10,"async":true}`。
`async: true` + 常時 exit 0 のため **Claude Code を一切ブロック・減速させない**。

- ユーザーレベル設定なので全プロジェクトで有効
- Hook は Claude Code セッション開始時に読み込まれるため、**設定後は新しいセッションから有効**

## アンインストール

1. Hook を外す: `pwsh -File uninstall-hook.ps1`
   (`ClaudePetNotify` を含む hook だけを全イベントから削除。他の設定は無傷。自動バックアップあり)
2. 常駐を止める: `.\bin\ClaudePetNotify.exe --quit`
3. フォルダ `C:\dev\claude-desktop-pet` を削除

### Hook を完全に元へ戻す方法

`install-hook.ps1` / `uninstall-hook.ps1` は実行のたびに
`%USERPROFILE%\.claude\settings.json.backup-claudepet-<日時>` を作成している。
完全に元へ戻すには該当バックアップを上書きコピーする:

```powershell
Copy-Item "$env:USERPROFILE\.claude\settings.json.backup-claudepet-<日時>" "$env:USERPROFILE\.claude\settings.json" -Force
```

## 現在の制約 (Phase 3 時点)

- 進捗率・ETA は未実装 (Phase 4 予定)
- `Stop` は「応答完了」ごとに発火する (短い質問への回答でも「終わったよ！」になる)
- Waiting の実発火は対話セッションのみ (`claude -p` ヘッドレスでは permission_prompt 通知自体が発生しない)
- `StopFailure` (エラー状態表示) は未実装
- PostToolUse hook はツール実行のたびに軽量ヘルパーを起動する (1回あたり数十ms・非同期。
  Claude Code をブロックはしないが、極端に活発なセッションでは短命プロセスが頻繁に生まれる)
- 同 priority の複数セッションが活発な場合、表示は最新イベント側へ切り替わり続ける (仕様)
- マルチモニタ: プライマリモニタの右下固定。モニタ構成変更後はペット再起動が必要
- DPI はシステム DPI 基準 (セッション中の DPI 変更には追従しない)
- Codex / ChatGPT 非対応
- キャラはコード描画の仮ひよこ (`src/Pet.cs` の `PetRenderer` 差し替えで変更可能)
