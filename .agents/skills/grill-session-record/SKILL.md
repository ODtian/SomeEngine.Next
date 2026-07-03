---
name: grill-session-record
description: Grill sub-skill. Persist every grill session as a raw Q&A transcript. In preflight runs, write .agent-runs/<id>/grill.md; otherwise fall back to harness/grill/sessions/<timestamp>.md. Activate when ending a grill session.
---

# Grill: Session Record

Grill decisions are not durable in conversation memory. Persist the raw exchange before ending the grill.

## Primary path during preflight

When a numeric run id exists, write:

```text
.agent-runs/<id>/grill.md
```

This is the Step 1 handoff artifact for `intent.md` and `harness.md`.

## Fallback path outside preflight

When no run id exists, write:

```text
harness/grill/sessions/<YYYYMMDD-HHMMSS>.md
```

## Format

Keep the transcript pure Q&A:

```md
# Grill <id-or-timestamp>

## Q1: <question text>
A: <user answer>

## Q2: <question text>
A: <user answer>
```

Do not summarize, interpret, annotate, add decisions, or add open-branch sections. The user's words are the source; interpretation belongs in `intent.md`, `harness.md`, or later grill sessions.
