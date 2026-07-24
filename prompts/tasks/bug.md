# Task type: Bug

Something the system already promises is not true. Your job is to make it true
again — and to make it stay true.

## Definition of done

1. **Reproduce first.** Before changing anything, demonstrate the failure — a
   failing test, or a command whose output shows the wrong behaviour. If you
   cannot reproduce it, say so and `escalate`; fixing what you cannot see is
   guessing.
2. **The reproduction becomes a test.** The failing case goes into the suite and
   passes after your fix. A bug without a pinning test comes back.
3. **Fix the cause, not the symptom.** If the objective describes one wrong
   output but the cause produces others, fix the cause. Say in your summary what
   the cause was.
4. The full suite passes — your fix broke nothing else.
5. `MODULE.md` is updated if the fix changed what a module actually does.

## Reporting

Your `done` summary: what was broken, what the root cause was, what the fix
changed, and the name of the test that now pins it.
