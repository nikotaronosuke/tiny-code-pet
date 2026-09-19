# Owner Decision Log

[日本語](OWNER_DECISIONS.md) | English

Tiny Code Pet was not generated once and left as-is.

While actually using it, I repeatedly changed the specification in response to
false notifications, runtime overhead, privacy concerns, and UI ambiguity.

This document focuses on **what I rejected, what I kept, and why** as the project owner.
For the longer technical history, see [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md) *(Japanese)*.

---

## 1. Keep a resident utility small before adding features

### Problem

The original goal was simple: show when Claude Code had finished.

For a utility that stays resident all day, I did not want the notification layer
to become heavier than the work it was observing.

### Decision

I chose **C# + pure Win32 / P/Invoke**.

The architecture intentionally avoids:

- Electron
- WebView
- a resident Node process
- a localhost server
- polling

The first chick prototype measured approximately 0% idle CPU / GPU and about
14 MB private working set in the recorded environment.

I do **not** present those numbers as current ninja-version measurements.
The animated version redraws while visible and needs its own runtime measurement.

**Evidence:** [initial native implementation](https://github.com/nikotaronosuke/tiny-code-pet/commit/d6d495a49d4f948647c7f536e974c8ee15aa06b8)

---

## 2. Do not inspect prompts or source code just to make progress look smarter

### Problem

Progress could look more intelligent if the pet parsed prompt text, assistant responses,
commands, source code, or transcripts.

That would turn a small status utility into a content-monitoring tool.

### Decision

Tiny Code Pet uses **structured status metadata from hooks**.

It does not read prompt bodies, response bodies, source-code bodies,
tool command / response bodies, or transcripts for progress estimation.

I also rejected:

- asking another LLM to estimate progress
- deriving progress from tool-call count
- inflating progress from elapsed time
- continuously watching transcripts

If metadata is insufficient, the UI stays less precise instead of inventing certainty.

**Evidence:** current README privacy contract / [progress implementation](https://github.com/nikotaronosuke/tiny-code-pet/commit/6a00d631018214bae2e973ad55313775a9d7f6ca)

---

## 3. Show whole-request progress, not task-count theater

### Problem

A display such as `3/5` looks precise, but task granularity is controlled by the agent.

The same request might be represented as 3 tasks or 8 tasks.
A raw completed/total ratio also jumps badly while the current step is still active.

### Decision

The UI moved to **estimated whole-request progress**:

```text
(completed + 0.5 × in_progress) / total
```

The `0.5` does not mean a step is objectively 50% complete.
It only places one active step between pending and completed for display purposes.

Additional constraints:

- no percentage for a one-step plan
- no ETA
- plan expansion may make the percentage decrease
- 100% does not imply completion

The goal is not a perfectly smooth number.
The goal is to avoid displaying precision that the available evidence cannot support.

**Evidence:** [whole-request progress](https://github.com/nikotaronosuke/tiny-code-pet/commit/b6d42df13297ad38941e957696631c136f058aef)

---

## 4. Reject forced task splitting when it makes the real work worse

I tested forcing the agent to maintain 6–8 milestone tasks so the progress bar would move more smoothly.

### Measured result

On the recorded small task:

- maximum progress jump: **33 points → 7 points**
- turns: **5 → 30**
- wall time: **+184%**

The display became smoother.

### Decision

I did **not** make this the global default.

Making the agent perform six times as many turns and take roughly 2.8× the wall time
just to improve the pet's visual progress would invert the product's priorities.

It remains an opt-in technique for long tasks where a user explicitly values smoother progress.

The development session also observed higher token / cost usage, but the public document
only repeats exact numbers that can be rechecked from the repository history.

**Evidence:** [task granularity experiment](https://github.com/nikotaronosuke/tiny-code-pet/commit/0404171cbc9f82aaffd886111536d38f328c8da8)

---

## 5. Remove tracker state from completion proof

Completion semantics changed several times:

1. Stop → complete
2. Stop + short grace
3. all structured tasks must be completed
4. **root Stop + 20 seconds of quiet**

### Why tracker-gated completion was removed

Tracker state is updated by the agent.

Tiny Code Pet cannot prove whether:

- the tracker accurately represents the full request
- the agent marked it complete too early
- a tracker update was simply omitted

Using tracker state as proof can create both:

- false positives: tracker says 100% but work continues
- false negatives: work ends but tracker metadata is incomplete

### Current decision

Tracker state is useful for **progress estimation only**.

Completion means:

> a root Stop was observed and no continuation of that work was observed for 20 seconds.

This is not proof that the artifact is correct.
It is only the strongest completion statement the pet can justify without reading the work itself.

Codex testing also observed a delayed old-turn PostToolUse roughly 18.6 seconds after interruption,
which contributed to choosing one conservative 20-second quiet window.

**Evidence:** [20s quiet-window completion](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95) / [Codex delayed-event measurement](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

---

## 6. Remove "incomplete" and "please check" as visible product states

Earlier versions experimented with visible states such as:

- stopped incomplete
- please check
- activity / uncertainty indicators

### Problem

Users mainly need to know whether work is active or whether the pet has enough evidence to announce completion.

Showing an internal uncertainty state does not necessarily give the user a useful next action,
and it can imply that the pet knows more about the actual artifact than it does.

### Decision

The visible product was simplified to three primary states:

- Idle
- Working
- Completed

If the pet cannot justify completion, it does not label the work as "incomplete" either.

**Evidence:** [simplified visible states](https://github.com/nikotaronosuke/tiny-code-pet/commit/c4b5a95)

---

## 7. Add Codex without forcing Claude semantics into the same state machine

### Problem

Codex exposed behavior that is not identical to Claude Code:

- turn identity exists
- work can continue in the same turn after Stop
- interrupt may happen without Stop
- events from an old turn may arrive late

Treating both providers as if they had the same lifecycle would make the shared UI simpler,
but the state model less honest.

### Decision

Codex uses a **separate adapter and event range**.

Internal identity is separated by:

```text
provider + session + turn
```

Old-turn events cannot overwrite current-turn completion or UI state.

Claude's existing event contract was left intact.
Only the display layer and common completion philosophy are shared.

**Evidence:** [Codex support](https://github.com/nikotaronosuke/tiny-code-pet/commit/c01126d)

---

## 8. Replace the chick with a ninja without replacing the lightweight core

The first version used a small chick.

It was enough to prove the status concept, but it did not have much identity as a product I wanted to keep using.

### Decision

I changed the visual system to:

- Idle / Working / Completed sprite animation
- subagents represented as ninja shadow clones
- up to six visible clones
- short smoke effects

I did **not** switch to Electron or another heavy UI stack to make that redesign easier.

The native event-driven foundation remained the same.

**Evidence:** [ninja sprites and shadow clones](https://github.com/nikotaronosuke/tiny-code-pet/commit/6b85569a953250a46a2c571a5780432c12112cdf)

---

## What this project prioritizes

Tiny Code Pet prioritizes:

- not guessing states it cannot observe
- changing the specification when measured behavior proves it wrong
- not making the agent's real work heavier for the pet's sake
- not reading prompt or source content unnecessarily
- keeping progress estimation separate from completion evidence
- preserving provider-specific lifecycle differences where they matter

The project uses AI-assisted implementation, but the part I wanted the repository to preserve is not code volume.

It is **which trade-offs were chosen, which approaches were rejected, and why**.
