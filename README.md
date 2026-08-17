# Claude Desktop Pet 🐣

Claude Code の作業完了を、デスクトップ右下の小さなひよこがピョコピョコ跳ねて
「終わったよ！」と知らせてくれる **超軽量デスクトップ通知キャラクター** です。

- 背景完全透過・枠なし・タスクバー/Alt+Tab 非表示・常に最前面
- **クリック透過**: キャラの背後にある VS Code や Chrome をそのまま操作できる
- アイドル時は完全静止 (`GetMessage` でブロック): **CPU 0% / GPU 0%**
- Electron / WebView / Node 常駐 / localhost サーバー / polling 一切なし

## 実測値 (Windows 11, 2026-08-17)

| 項目 | 実測 |
|---|---|
| 待機時 Private Working Set | **約 13.4 MB** |
| 待機時 Working Set | 0.3〜2 MB (トリム後) |
| 待機時 CPU (60秒計測) | **0 ms (完全に0%)** |
| 待機時 GPU | **0** (GPUエンジンインスタンス自体が0個) |
| 完了アニメーション1回の CPU 累計 | 約 15〜50 ms |
| 通知3回後のメモリ増加 | なし (13.33 → 13.39 MB で安定) |

## アーキテクチャ

```
Claude Code (Stop hook, user-level settings.json)
        │  stdin に JSON (cwd 等) を渡して起動
        ▼
ClaudePetNotify.exe   … Hook Adapter。cwd からプロジェクト名だけ抽出
        │  WM_COPYDATA (正規化イベント: dwData=1 task_complete + プロジェクト名)
        ▼
ClaudePet.exe         … 常駐ペット。イベント受信で状態遷移
        │  Idle → Celebrating(バウンド) → メッセージ表示 → (約5秒) → Idle
        ▼
Win32 layered window  … UpdateLayeredWindow で ARGB 描画 (状態変化時のみ)
```

- 実装: C# (P/Invoke による純 Win32)。**.NET Framework 4.8 同梱の csc.exe でビルドするため追加インストール不要**
- ウィンドウ: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`
- タイマーはアニメーション中のみ動作し、終了後は必ず `KillTimer`
- 通知音は `MessageBeep` (Windows 標準音) 1回
- ペット未起動時は Notify が同フォルダの ClaudePet.exe を自動起動 (PC再起動後も次の完了通知で自動復活)
- キャラ差し替え: `src/Pet.cs` の `PetRenderer` クラスを置き換えるだけ (将来は画像読み込みに変更可)

### プライバシー

Hook の stdin JSON から読むのは `cwd` のみ。プロンプト本文・応答本文・コード・APIキー等は
一切読み取らず、保存も送信もしない。履歴DBなし。ネットワーク通信なし。

## ビルド

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

`bin\ClaudePet.exe` (常駐本体) と `bin\ClaudePetNotify.exe` (Hookヘルパー) が生成される。

## 起動

```powershell
.\bin\ClaudePet.exe        # 常駐開始 (二重起動は自動防止)
.\bin\ClaudePetNotify.exe --test 名前   # 手動で通知テスト
.\bin\ClaudePetNotify.exe --quit        # 常駐ペットを終了
```

手動起動しなくても、Hook 発火時に自動起動される。
ログイン時に常駐させたい場合は `shell:startup` に ClaudePet.exe のショートカットを置く (任意)。

## Claude Code Hook との接続

```powershell
pwsh -File install-hook.ps1
```

これがユーザーレベル設定 `%USERPROFILE%\.claude\settings.json` に以下を **追記** する
(既存 hooks は一切変更しない。実行前に `settings.json.backup-claudepet-<日時>` を自動作成)。

```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "C:/dev/claude-desktop-pet/bin/ClaudePetNotify.exe",
            "timeout": 10,
            "async": true
          }
        ]
      }
    ]
  }
}
```

- `Stop` は Claude Code が応答を完了したときに発火する公式イベント (Claude Code 2.1.233 / 公式 docs で確認)
- `async: true` + exit code 常時 0 のため、**Claude Code を一切ブロック・減速させない**
- ユーザーレベル設定なので `C:\dev\kimete-log` など全プロジェクトで有効
- Hook は Claude Code セッション開始時に読み込まれるため、**設定後に新しいセッションを開始** (既存セッションには反映されない)

## アンインストール

1. Hook を外す:
   ```powershell
   pwsh -File uninstall-hook.ps1
   ```
   (`ClaudePetNotify` を含む Stop hook だけを削除。他の設定・hooks は無傷。実行前に自動バックアップ)
2. 常駐を止める: `.\bin\ClaudePetNotify.exe --quit` (または `taskkill /im ClaudePet.exe`)
3. フォルダ `C:\dev\claude-desktop-pet` を削除

### Hook を完全に元へ戻す方法

`install-hook.ps1` / `uninstall-hook.ps1` は実行のたびに
`%USERPROFILE%\.claude\settings.json.backup-claudepet-<日時>` を作成している。
完全に元へ戻すには、該当バックアップを `settings.json` に上書きコピーするだけ:

```powershell
Copy-Item "$env:USERPROFILE\.claude\settings.json.backup-claudepet-<日時>" "$env:USERPROFILE\.claude\settings.json" -Force
```

## 現在の制約 (Phase 1 時点)

- 通知イベントは「応答完了 (Stop)」のみ。進捗率・ETA・確認待ち通知は未実装 (Phase 2 以降)
- `Stop` は「タスク完了」ではなく「応答完了」ごとに発火する (短い質問への回答でも通知される)
- マルチモニタ: プライマリモニタの右下固定。モニタ構成変更後はペット再起動が必要
- DPI はシステム DPI 基準でスケーリング (セッション中の DPI 変更には追従しない)
- Codex / ChatGPT / サブエージェント (SubagentStop) 非対応
- キャラはコード描画の仮ひよこ (PetRenderer 差し替えで変更可能)
