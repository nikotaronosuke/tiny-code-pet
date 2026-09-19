# Owner Decision Log

日本語 | [English](OWNER_DECISIONS.en.md)

このリポジトリで残したい判断は4つだけです。どれも、実際に測ったか、仕様を変えたか、失敗から修正したものです。

## 1. 進捗を滑らかにするための Task 細分化を、実測して却下した

「6〜8個の milestone Task を維持する」と指示すれば、Pet の進捗表示は滑らかになります。

同じ小タスクで比較すると、

- 最大の進捗ジャンプ: **33 → 7ポイント**
- turns: **5 → 30**
- wall time: **+184%**

表示は改善しましたが、Pet のために本来のAI作業を重くするのは本末転倒だと判断し、グローバル設定にはしませんでした。

**Evidence:** [task granularity experiment](https://github.com/nikotaronosuke/tiny-code-pet/commit/0404171cbc9f82aaffd886111536d38f328c8da8)

## 2. 完了判定を4回作り直した

完了判定は、

1. Stop → 完了
2. Stop + short grace
3. structured task 全件 completed
4. **root Stop + 20秒静穏**

と変わりました。

tracker を完了条件にすると、status の更新漏れで false negative が出る一方、status を先に completed にすれば false positive も起こせます。

さらに Codex では interrupt 後に古い PostToolUse が**約18.6秒遅れて届く**ケースを観測しました。そこで tracker は進捗表示だけに使い、完了は「Stop の後に作業が20秒再開しなかった」という観測事実へ切り替えました。

**Evidence:** [20s quiet window](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95) / [delayed Codex event](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

## 3. Codex を Claude と同じ state machine に押し込まなかった

Codex には turn identity があり、interrupt では Stop が来ず、古い turn のイベントが遅れて届くことがあります。

共通化しすぎると誤通知が増えるため、Codex は別 adapter / 別 event range にし、内部 identity を **provider + session + turn** で分離しました。

Claude 側の既存 contract はそのまま残しています。

**Evidence:** [Codex support](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

## 4. 忍者化しても Electron へ移らなかった

ヒヨコから忍者へ変え、sprite animation と subagent の影分身を追加しました。

見た目を作りやすくするために Electron / WebView へ移す案ではなく、既存の pure Win32 / event-driven 構成を維持しました。

初期ヒヨコ版で測った約14MB private working set は忍者版の数値として使わず、READMEでも区別しています。

**Evidence:** [ninja sprites and shadow clones](https://github.com/nikotaronosuke/tiny-code-pet/commit/6b85569a953250a46a2c571a5780432c12112cdf)
