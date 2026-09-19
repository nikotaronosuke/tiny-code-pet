# Owner Decision Log

日本語 | [English](OWNER_DECISIONS.en.md)

Tiny Code Pet は、AI にコードを書かせて一度で完成したものではありません。

実際に使いながら、**誤通知・重さ・プライバシー・表示の分かりにくさ**を見て仕様を何度も変更しています。
この文書ではコードの全履歴ではなく、プロジェクトオーナーとして「何を捨て、何を残したか」が分かる判断だけを抜き出します。

詳細な技術履歴は [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md) にあります。

---

## 1. 常駐ツールなので、機能より先に「軽さ」を優先した

### 課題

Claude Code の終了通知を見るためだけに、重い常駐アプリを増やしたくありませんでした。

### 検討した方向

WinForms / WPF / Electron / WebView のような一般的な UI 基盤も候補になり得ますが、
常駐時の小ささと、追加ランタイムや常時サーバーを持たないことを優先しました。

### 判断

**C# + pure Win32 / P/Invoke** を採用しました。

- layered window
- event-driven
- polling なし
- localhost server なし
- Node 常駐なし
- Windows 標準の .NET Framework 4.8 の `csc.exe` でビルド

初期版では idle 時 CPU 0%、GPU 0%、private working set 約14MBを確認しています。
忍者アニメーション追加後は同じ数値だとは主張せず、READMEでも旧ヒヨコ版の測定値として分けています。

**Evidence:** [initial native implementation](https://github.com/nikotaronosuke/tiny-code-pet/commit/d6d495a49d4f948647c7f536e974c8ee15aa06b8)

---

## 2. 進捗を正確に見せるためでも、Prompt やソース本文は読まない

### 課題

AI の作業内容を詳しく読めば、より賢そうな進捗推定はできます。
ただし、それでは小さな通知ツールが Prompt・応答・コード本文まで監視することになります。

### 判断

Tiny Code Pet が読むのは **Hook が渡す status metadata だけ**にしました。

読まないもの:

- Prompt 本文
- Claude / Codex の応答本文
- ソースコード本文
- tool command / response 本文
- transcript

その代わり、情報が足りないときに進捗や状態を推測で埋めない方針を選びました。

### 却下したもの

- LLM に追加で進捗を問い合わせる
- tool 実行回数から進捗を推定する
- 経過時間から % を水増しする
- transcript を常時監視する

「表示を賢く見せる」より、**必要以上に内容を読まないこと**を優先しています。

**Evidence:** current README privacy contract / [progress implementation](https://github.com/nikotaronosuke/tiny-code-pet/commit/6a00d631018214bae2e973ad55313775a9d7f6ca)

---

## 3. 進捗は「件数」ではなく、依頼全体の推定に変えた

### 最初の問題

Task の `3/5` のような件数表示は分かりやすく見えますが、
AI が同じ仕事を 3 Task に分けるか 8 Task に分けるかで意味が変わります。

また、`completed / total` だけでは、現在進行中の工程が 0 と扱われ、表示が大きく跳ねました。

### 判断

表示は **「全体 推定 N%」** に変更し、Task件数はUIから削除しました。

現在の式:

```text
(completed + 0.5 × in_progress) / total
```

`in_progress = 0.5` は「その工程が50%終わった」という意味ではありません。
工程表の中で「1工程が進行中」という状態を、0と1の中間として表示するための heuristic です。

### さらに決めたこと

- total が 1 の plan では % を出さない
- plan が増えて % が下がることを許容する
- ETA は出さない
- 100%でも Stop が無ければ完了扱いしない

滑らかに見せることより、**根拠のない精密さを出さないこと**を優先しました。

**Evidence:** [whole-request progress](https://github.com/nikotaronosuke/tiny-code-pet/commit/b6d42df13297ad38941e957696631c136f058aef)

---

## 4. 進捗を滑らかにするための「Task強制細分化」は却下した

進捗表示を滑らかにするため、
「6〜8個の milestone Task を維持する」という指示をAIへ付ける実験も行いました。

### 実測

小さな同一タスクで:

- 最大の進捗ジャンプ: **33ポイント → 7ポイント**
- turns: **5 → 30**
- wall time: **+184%**

表示自体は明らかに滑らかになりました。

### 判断

**グローバルには採用しませんでした。**

Pet の進捗表示を良くするために、
本来のAI作業を6倍のturn数・約2.8倍の時間にするのは本末転倒だからです。

現在は、長時間依頼で滑らかな表示が必要なユーザーが自分で opt-in できる方法としてだけ残しています。

※ 開発時にはコスト・output token増加も確認していますが、公開文書では再確認できる数値だけを掲載しています。

**Evidence:** [task granularity experiment](https://github.com/nikotaronosuke/tiny-code-pet/commit/0404171cbc9f82aaffd886111536d38f328c8da8)

---

## 5. 完了判定から tracker を外した

完了判定は、このプロジェクトで最も大きく変更した部分です。

### 変遷

1. Stop → 完了
2. Stop + 短い grace
3. structured task が全件 completed のときだけ完了
4. **root Stop + 20秒静穏**

### tracker 必須方式をやめた理由

tracker の status はAI側が更新します。

そのためPet側では、

- statusを早く completed にしただけなのか
- 本当に作業が終わったのか
- tracker更新が漏れているだけなのか

を検証できません。

trackerを完了の証拠にすると、

- statusだけ先に100% → false positive
- tracker更新漏れ → false negative

の両方が起こり得ます。

### 現在の判断

trackerは **進捗表示だけ** に使います。

完了は、

> root Stop を受け、その後20秒間その作業が再開されなかった

ときだけ通知します。

これは「成果物が正しい」の保証ではなく、
Petが本文を読まずに観測できる範囲での「作業が止まった」という意味です。

Codexでは Stop 後に同じ turn で作業が継続し得ることを確認し、
さらに interrupt 後に約18.6秒遅れて PostToolUse が届くケースも観測したため、
短い grace ではなく20秒静穏へ統一しました。

**Evidence:** [20s quiet-window completion](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95) / [Codex delayed-event measurement](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

---

## 6. 「未完了」「確認して」を表示するのをやめた

途中では、

- 途中で止まったよ
- 終わったか確認してね
- activity indicator

など、Pet内部の判断状態を細かく見せる仕様もありました。

### 問題

ユーザーにとって重要なのは、
内部stateの名前ではなく「今AIが動いているのか、終わったのか」です。

「Petが完了を証明できなかった」というだけの通知を出しても、
ユーザーが取れる行動がありません。

### 判断

見える主状態を3つに絞りました。

- Idle
- 作業中…
- 終わったよ！

分からない状態を「未完了」と断定することもやめました。

**Evidence:** [simplified visible states](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95)

---

## 7. Codex対応は、Claude用state machineへ無理に混ぜなかった

Claude Code対応の後、Codexも同じPetで見たいという方向へ拡張しました。

### 問題

Codexでは、

- turn identity がある
- Stop後に同じturnで継続することがある
- interruptではStopが来ない
- 古いturnのイベントが遅れて届く

など、Claude Codeと同一には扱えない挙動がありました。

### 判断

Codex用の **別adapter / 別event range** を作り、
内部identityを

```text
provider + session + turn
```

で分離しました。

実測では interrupt 後、古い PostToolUse が約18.6秒遅れて到着したため、
old-turn event が現在のturnのUIや完了候補を壊さないようにしています。

Claude側の既存event contractは壊さず、その上にCodexを追加しました。

**Evidence:** [Codex support](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

---

## 8. ヒヨコから忍者へ変えたが、土台は作り直さなかった

初期版は小さなヒヨコでした。

機能確認には十分でしたが、使い続けるプロダクトとしての個性を強くするため、
表示キャラクターを忍者へ変更しました。

追加したもの:

- Idle / Working / Completed のsprite animation
- Subagentを「影分身」として表示
- 最大6体の分身
- 出入りの煙表現

### 判断

見た目を強化するために Electron 等へ移行せず、
**pure Win32のイベント駆動構成をそのまま維持**しました。

見た目と機能は変えても、常駐ツールとしての軽さ・privacy・event-drivenという土台は変えない判断です。

**Evidence:** [ninja sprites and shadow clones](https://github.com/nikotaronosuke/tiny-code-pet/commit/6b85569a953250a46a2c571a5780432c12112cdf)

---

## このプロジェクトで優先したもの

Tiny Code Pet では、機能数や表示の派手さより次を優先しています。

- 分からない状態を推測しない
- 実測で問題が出たら仕様を変える
- Petのために本来のAI作業を重くしない
- Promptやコード本文を必要以上に読まない
- 進捗の推定と完了の事実を混同しない
- Claude CodeとCodexの差を無理に同一視しない

AIを使って実装していますが、
このリポジトリで残したかったのはコード量ではなく、**どのトレードオフを選んだか**です。
