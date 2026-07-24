# Darvoza ADRs

Architecture decisions locked during the build, so later tasks build to them instead of re-deriving.
The raw decisions were captured in an internal planning workspace during development; these ADRs
promote the load-bearing ones into the code repo (the in-code `ADR-####` citations resolve here).

| ADR | Title | Binding on |
|---|---|---|
| ADR-0001 | MCP C# SDK 1.4.0 — adopted API surface | all tasks |
| ADR-0002 | Tool pipeline — decorator chain on a single upstream seam (+ lifecycle) | A01-T3, A01-T4 |
| ADR-0003 | Audit trail — outermost decorator + ambient decision context | A01-T4, A01-T5, A01-T6 |
