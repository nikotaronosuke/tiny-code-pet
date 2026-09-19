# Owner Decision Log

[日本語](OWNER_DECISIONS.md) | English

Four decisions are worth keeping here because they came from measurement, a specification change, or a concrete failure.

## 1. Measured forced task splitting — and rejected it

Forcing the agent to maintain 6–8 milestone tasks made the pet's progress display much smoother.

On the same small task:

- maximum progress jump: **33 → 7 points**
- turns: **5 → 30**
- wall time: **+184%**

The UI improved, but making the real AI task heavier for the pet's sake was the wrong trade-off. I did not make it the global default.

**Evidence:** [task granularity experiment](https://github.com/nikotaronosuke/tiny-code-pet/commit/0404171cbc9f82aaffd886111536d38f328c8da8)

## 2. Rebuilt completion semantics four times

Completion changed through:

1. Stop → complete
2. Stop + short grace
3. all structured tasks completed
4. **root Stop + 20 seconds of quiet**

Tracker-gated completion can produce both false positives and false negatives because the tracker itself is agent-maintained.

Codex testing also observed an old PostToolUse arriving **about 18.6 seconds after an interrupt**. That pushed the design toward a single conservative rule: tracker state is for progress only; completion is based on observed Stop + quiet.

**Evidence:** [20s quiet window](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95) / [delayed Codex event](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

## 3. Did not force Codex into Claude's state model

Codex has turn identity, can be interrupted without Stop, and can deliver delayed events from an old turn.

Instead of pretending the lifecycle is identical, Codex uses a separate adapter / event range and internal identity is **provider + session + turn**.

Claude's existing contract remains unchanged.

**Evidence:** [Codex support](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

## 4. Added the ninja without moving to Electron

The original chick became an animated ninja with subagent shadow clones.

I kept the existing pure Win32 / event-driven core instead of switching UI stacks for convenience.

The old ~14 MB private-working-set measurement belongs to the chick version and is not reused as a claim for the animated version.

**Evidence:** [ninja sprites and shadow clones](https://github.com/nikotaronosuke/tiny-code-pet/commit/6b85569a953250a46a2c571a5780432c12112cdf)
