---
title: Domain documentation conventions
description: Single-context domain layout and consumption rules for engineering skills.
---

## Layout and consumer rules

This project has one domain context. Before exploring, read the root
[glossary](../../GLOSSARY.md) and relevant decisions in `docs/adr/`.
If a `GLOSSARY-MAP.md` is introduced later, follow its links for relevant
contexts.

Use canonical glossary terms in specifications, issues, tests, and
implementation discussions. Surface contradictions with existing ADRs rather
than silently overriding them.

If a glossary or ADR directory is absent, continue without creating
placeholders or flagging absence as a defect. Create domain documents lazily
when terms or decisions are resolved through `domain-modeling`.
