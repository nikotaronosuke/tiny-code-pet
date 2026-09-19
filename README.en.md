# Tiny Code Pet 🥷

English | [日本語](README.md)

[![Build](https://github.com/nikotaronosuke/tiny-code-pet/actions/workflows/build.yml/badge.svg)](https://github.com/nikotaronosuke/tiny-code-pet/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-blue)

> A tiny native Windows desktop pet for Claude Code and Codex.

Tiny Code Pet shows **working state, estimated whole-request progress, and completion**
for Claude Code and Codex as a small ninja in the corner of your screen.

Claude Code and Codex can be monitored at the same time without their session state colliding.

![Tiny Code Pet preview](docs/assets/ninja-working.gif)

## At a glance

| | |
|---|---|
| 🤖 **Claude Code + Codex** | One pet can monitor either provider or both at once |
| 🪟 **Native Windows UI** | Pure Win32 via C# P/Invoke — no Electron, WebView, resident Node process, localhost server, or DB |
| 🚫 **Stays out of the way** | Click-through, hidden from taskbar / Alt+Tab, never steals focus |
| 🥷 **System tray controls** | Show / hide / bring to front / exit |
| 📊 **Whole-request progress** | Estimated from the request's structured plan, not from tool-call counts or elapsed time |
| ✅ **Completion = root Stop + 20 s quiet** | Completion is independent from task tracker percentages |
| 🔒 **Privacy boundary** | Does not read prompt text, assistant responses, source code, tool command bodies, or transcripts |
| 🥷 **Ninja + shadow clones** | Idle / working / completed animation plus up to six visible subagents |

## Visible states

The UI intentionally exposes only three primary states:

| State | Display | Meaning |
|---|---|---|
| Idle | Ninja + `Tiny Code Pet` | No active work is being shown |
| Working | Ninja + `作業中…` + project | The observed request is active; estimated progress / current step may also be shown |
| Completed | Ninja + `終わったよ！` + project | A root Stop was observed and no continuation arrived for 20 seconds |

There is no visible "incomplete" state. If Tiny Code Pet cannot justify saying the work ended,
it does not invent a stronger conclusion.

A displayed session may also show **provider + model**, such as
`Claude · Opus 4.6` or `Codex · GPT-5.6-codex`.
If the model cannot be observed, only the provider is shown.

The `+N` indicator shows other active sessions. Subagent clones are a separate visual concept.

> The current app UI is Japanese. This English README documents the same behavior without changing runtime UI strings.

## Why completion is not "100%"

Progress and completion are intentionally separate.

### Progress

When a structured plan is available, Tiny Code Pet estimates whole-request progress using:

```text
(completed + 0.5 × in_progress) / total
```

Important constraints:

- the plan must contain at least 2 valid steps before a percentage is shown
- `in_progress = 0.5` does **not** mean that step itself is exactly 50% complete
- adding new plan steps may make the percentage go down
- there is no ETA
- elapsed time does not inflate progress
- tool-call counts do not inflate progress
- a 100% tracker does not prove completion

If no trustworthy plan exists, Tiny Code Pet shows no percentage instead of fabricating one.

### Completion

Completion is based on **root Stop + a 20-second quiet window**.

```text
root Stop
   ↓
completion candidate
   ├─ continuation within 20 s → cancel candidate
   ├─ another Stop            → restart 20 s window
   ├─ new request             → discard old candidate
   └─ 20 s quiet
         ├─ no other active session → completed notification
         └─ other work active       → clean up silently
```

This means:

- 100% progress without Stop is **not complete**
- 50% progress with Stop + 20 seconds of quiet **can complete**
- SessionEnd by itself is not completion proof
- interrupted work is not converted into completion by a guessed timeout
- delayed old-turn Codex events do not cancel the current turn because Codex state is separated by provider + session + turn

"Completed" therefore means:

> Claude Code or Codex emitted a root Stop and did not resume that work for 20 seconds.

It does **not** mean the produced code or artifact is guaranteed correct.

## Current step and elapsed time

When the provider exposes a structured current step, the HUD can also show:

- the current plan step
- elapsed request time

These values are display-only.

Tiny Code Pet does not treat elapsed time as progress, ETA, or completion evidence.

If request start was not observed — for example because the pet was launched mid-request —
the timer is presented as time observed from that point instead of pretending to know the real request start.

## Ninja animation and shadow clones

The original prototype was a small chick. The visual layer was later redesigned as a ninja
without replacing the lightweight Win32 event-driven foundation.

Current behavior includes:

- idle animation
- working animation
- one-shot completion pose
- short smoke effects
- up to 6 visible subagent clones
- overflow shown as `+N`
- animation timer paused while hidden
- monitoring and completion tracking continue while hidden

Subagent identity is only shown when Tiny Code Pet receives enough metadata to distinguish it.
It does not guess the number of agents.

## Architecture

### Claude Code

```text
Claude Code Hooks
  UserPromptSubmit / PostToolUse / Stop / SessionEnd / Task events / Subagent events
        │
        │ status metadata only
        ▼
ClaudePetNotify.exe
        │
        │ WM_COPYDATA
        ▼
ClaudePet.exe
        │
        ▼
Native Win32 layered window
```

### Codex

```text
Codex Hooks
  UserPromptSubmit / PostToolUse / PermissionRequest / Stop / SessionEnd
  SubagentStart / SubagentStop
        │
        │ status metadata only
        ▼
CodexPetNotify.exe
        │
        │ separate event range + turn identity
        ▼
ClaudePet.exe
        │
        ▼
same native UI
```

Claude and Codex share the visual layer and completion philosophy, but they do **not**
pretend to have identical event semantics.

Codex keeps turn identity because delayed events from an older turn can arrive after an interrupt.
Claude's existing session contract is not artificially reshaped to look like Codex.

## Privacy

Tiny Code Pet is designed around a narrow metadata boundary.

It may read status metadata such as:

- hook event name
- session id
- turn id where available
- cwd / project name
- provider / model metadata where available
- tool name
- task / plan status
- subagent identity metadata where available

It does **not** read, store, or transmit the following for progress estimation:

- prompt text
- assistant response text
- source code
- tool command body
- tool response body
- reasoning
- transcript
- API keys / secrets / credentials

State is in memory. There is no history database and no network service.

A debug log can be explicitly enabled with `bin\debug.flag`; normal operation does not keep that diagnostic log.

## Multiple sessions

Sessions are tracked independently and the pet displays the highest-priority recent state.

Working / Waiting / Finalizing states take priority over old completion notifications,
so a finished session does not hide another session that is still working.

A completion notification is skipped if another session is active at the moment the quiet window expires.
It is not queued for later replay.

## Codex notes

Codex support uses a separate adapter.

Important behavior:

- progress is shown only when a usable structured plan is observed
- the hook installer does **not** rewrite `config.toml`
- existing hooks are preserved
- trust confirmation is left to the user; production setup does not bypass hook trust
- an interrupt does not become a fake completion
- old-turn delayed events are ignored for the current turn
- if subagent origin cannot be proven safely, progress is hidden for that turn instead of guessed

See the Japanese README for the full implementation notes and measured edge cases.

## Install

You can use Claude Code only, Codex only, or both.

### Option 1 — Release build

Download the latest Windows ZIP from [Releases](https://github.com/nikotaronosuke/tiny-code-pet/releases), extract it, then register the hook(s) you want:

```powershell
pwsh -File install-hook.ps1
pwsh -File install-codex-hook.ps1 -DryRun
pwsh -File install-codex-hook.ps1
.\bin\ClaudePet.exe
```

The distributed binaries are unsigned, so Windows SmartScreen may warn on first launch.

### Option 2 — Build from source

```powershell
git clone https://github.com/nikotaronosuke/tiny-code-pet.git
cd tiny-code-pet
powershell -ExecutionPolicy Bypass -File build.ps1
```

The build produces:

- `bin\ClaudePet.exe` — resident pet
- `bin\ClaudePetNotify.exe` — Claude Code hook adapter
- `bin\CodexPetNotify.exe` — Codex hook adapter

The project builds with the `.NET Framework 4.8` C# compiler that ships with Windows.
Visual Studio and the modern .NET SDK are not required for this build path.

> The public product name is **Tiny Code Pet**. The `ClaudePet*` binary names are retained for compatibility with existing hooks and IPC identifiers.

## Uninstall

Remove only the hooks installed for Tiny Code Pet:

```powershell
pwsh -File uninstall-hook.ps1
pwsh -File uninstall-codex-hook.ps1 -DryRun
pwsh -File uninstall-codex-hook.ps1
```

Then stop the resident pet:

```powershell
.\bin\ClaudePetNotify.exe --quit
```

The uninstall scripts target Tiny Code Pet's own hook entries and leave unrelated hooks alone.

## Requirements

- Windows 10 / 11 x64
- .NET Framework 4.8
- Claude Code with Hooks support
- optionally, Codex with Hooks support

## Validation

The local test suite covers areas such as:

- state transitions
- 20-second completion quiet window
- duplicate / out-of-order subagent events
- old Codex turn filtering
- transparent click-through behavior
- hidden animation timer behavior
- rendering resources
- multiple DPI scales

The current Japanese README contains the latest exact local assertion counts and environment-specific integration notes.
Those counts are intentionally not duplicated here so this shorter English overview does not become stale.

## Known limitations

- progress is a heuristic, not a guarantee
- no structured plan means no percentage
- a one-step plan does not produce a percentage
- completion notification is delayed by the 20-second quiet window
- delayed continuation events can suppress completion until another Stop is observed
- completion is skipped if another session is active at that moment
- the visible completion state is short-lived
- other TOPMOST windows can cover the pet
- multi-monitor placement currently targets the primary monitor
- DPI changes during a running session are not dynamically followed
- model display can lag behind an in-session Claude model switch because transcript polling is intentionally avoided
- Codex subagent hook emission has not been fully verified in every tested environment
- Codex interrupt may leave the working display until a later request resets the state
- Codex does not currently implement the same nested-process suppression used for Claude

These are documented limitations, not hidden fallback behavior.

## Development choices

A few notable rejected approaches:

- **Forced 6–8 step task splitting** made progress visibly smoother, but increased turns from 5 to 30 and wall time by 184% in the recorded experiment. It was not made the global default.
- Tracker completion was removed as completion proof because Tiny Code Pet cannot verify whether the tracker itself accurately represents the full request.
- Transcript / prompt inspection was rejected for progress estimation.
- Electron / WebView was not introduced when the chick UI was replaced by animated ninja sprites.
- Codex was added through a separate adapter rather than rewriting Claude semantics to match Codex.

The chronological design record is [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md) *(Japanese)*. It was written alongside development and records accepted, rejected, and measured approaches; current behavior is defined by the README and source.

## Development status

Tiny Code Pet started as a personal utility for seeing Claude Code's state at a glance and later expanded to support Codex, whole-request progress, ninja animation, and subagent visualization.

It is still intentionally small: no account system, no database, no hosted backend.

## AI-assisted development

This project was developed with assistance from tools such as ChatGPT and Claude Code.

Product direction, UX decisions, trade-offs, real-device / real-session verification, and final acceptance decisions are made by the project owner.

## License

[MIT](LICENSE)
