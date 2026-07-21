# Task type: Chore

Maintenance work: scaffolding, dependency bumps, file moves, config, cleanup.
No new product behaviour.

## Definition of done

1. The objective is satisfied exactly — chores have crisp boundaries, so stay
   inside them. Scope creep is the characteristic failure of this task type.
2. Behaviour is unchanged. If the existing tests pass before and after, and you
   did not need to edit them, that is the evidence. If a test had to change,
   this was not a chore: say so in your summary.
3. The build is clean and the full test suite passes.
4. Any documentation that described the thing you moved, renamed, or configured
   is updated in the same task.

## Reporting

Your `done` summary should let a reviewer confirm the change was mechanical:
what moved or changed, why it is behaviour-preserving, and what you verified.
