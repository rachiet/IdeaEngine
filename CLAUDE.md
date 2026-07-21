# Forge — Orchestrator Implementation

You are implementing Forge: a C#/.NET service that builds software from client
requirements by orchestrating stateless LLM agents (PM, Principal, Engineer, QA,
Researcher). **Read `ORCHESTRATOR-SPEC.md` in full before writing any code.**
It is the authoritative design document; this file adds decisions made after it.

## Terminology
- **Orchestrator** = the whole service (scheduler, pipeline state machine, roles).
- **Harness** = the inner, deterministic layer wrapped around each LLM call:
  context assembly → LLM call → tool-call parsing → jailed tool execution →
  observation loop → budget/iteration enforcement → ledger + progress notes.
  One process, two layers; the orchestrator contains the harness.
- Everything in the harness is trusted mechanical code; everything from the
  model is untrusted output under supervision (spec Principle 6).

## Post-spec decisions (settled in design discussion — treat as [DECIDED])

### Prompt layering (do NOT store prompts in the tasks table)
Agent instructions are assembled at spin-up from three layers:
- **Layer A — role identity:** `prompts/roles/<role>.md` (versioned in git).
- **Layer B — task-type instructions:** `prompts/tasks/<type>.md`
  (e.g. design.md, feature.md, review.md, impact_analysis.md).
- **Layer C — task packet (DB only):** `objective`, `acceptance_criteria`,
  `context_paths`, `requirements_ref`, `progress_note` from the task row.
Extra one-off guidance travels as a task-anchored `messages` row, never as a
per-task prompt blob. Rationale: prevents prompt drift; fixing a template file
improves all future tasks (same self-improving property as CONVENTIONS.md).

### Routing ("who acts next")
Derived, not stored. `assigned_role` says who executes; every other handoff is
a static harness map from status → role (in_review → principal, qa → qa,
blocked → pm). Never add a "next actor" column — two sources of truth drift.

### DB column vs JSON rule
Anything the harness must query or enforce (status, budgets, roles, milestone)
is a real column with CHECK constraints. Anything only the LLM reads may be
TEXT/JSON (`context_paths` is JSON by design).

### Typed layer over SQLite (C#)
- One `sealed record` per table (e.g. `TaskRecord`); enums for `TaskType`,
  `TaskStatus`, `AgentRole` mirroring the CHECK constraints (keep both layers).
- Dapper + small type handlers (enum ⇄ snake_case TEXT, JSON list ⇄ TEXT).
- `RequirementsRef` is a parsed value type ("02-todos-read.md@v3" → File +
  Version); parse-don't-validate at the DB boundary, throw on malformed.
- `Message` is an abstract record with one sealed subtype per message type
  (Question, Answer, Review, Decision, Escalation, Status, ChangeRequest,
  SystemNudge); exhaustive switch for routing.
- Construction via factory methods enforcing invariants (budget > 0,
  non-empty packet), not naked inserts.
- Status changes go through a `TaskTransitions` legal-transition map that
  throws on illegal transitions — never raw `UPDATE tasks SET status=?`.
- Tasks/messages do NOT generate .md files. Repo .md files (MODULE.md, ADRs,
  requirements) are written by agents via write_file. The only markdown the
  harness renders is the task packet into the prompt (never to disk).

## Directory layout [DECIDED]
Two roots. Client project data NEVER lives inside the Forge source repo.
- Forge source repo (this repo): src/, prompts/, docs.
- Runtime data root: single config value `ForgeDataRoot` (env `FORGE_HOME`,
  default `~/forge-data`) — the only path the code hard-knows; derive all else:
  - `forge.db` (global DB), `vault/` (encrypted secrets)
  - `projects/<name>/project.db` — per-project SQLite (queue/board/ledger)
  - `projects/<name>/repo.git` — bare repo, source of truth; harness merges here;
    generated code + full docs tree (PROJECT.md, requirements, MODULE.md) live in it
  - `projects/<name>/workspaces/task-<id>/` — per-task working clone; this exact
    path is the tool executor's jail; created on claim, deleted after merge
Jail + DB locations are M0 concerns — build the path logic in M0.

## Build order (spec §12 — follow strictly, do not skip ahead)
M0 first: SQLite schemas, MeteredLlmClient (ledger + budget refusal as a
decorator), tool executor with working-dir jail + secret substitution,
`forge log`. No agents until M0 is done. Then M1 (single agent, single task,
kill-and-resume proof) → M2 (PM chat) → M3 (design) → M4 (review+CI) →
M5 (QA) → M6 (CRs). Anti-pattern: standing up all personas at once.

## Non-negotiables to preserve in code
- Budgets enforced by refusing the next LLM call, never by asking the model.
- All LLM calls flow through one `MeteredLlmClient` decorator (ledger + caps).
- Merge/CI/test state read from git and process output, never from agent claims.
- Secrets: agents see `{{secret:NAME}}`; substitution in the tool executor at
  exec time; values never in DB, context, or logs.
- Every feature of generated projects must be CLI-verifiable; Principal must
  design an observable side-channel for otherwise-internal behavior
  (e.g. an X-Cache header for a cache).
- .NET 8+ console host; Microsoft.Data.Sqlite + Dapper (no EF);
  System.CommandLine or Spectre.Console for the CLI.
