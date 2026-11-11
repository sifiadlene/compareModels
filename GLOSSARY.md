---
description: Canonical vocabulary for comparing coding-agent configurations.
---

# Coding agent comparison

Coding-agent comparisons measure how different configurations perform the same
coding task under declared conditions.

## Language

**Task**:
A reproducible coding assignment consisting of a starting project state, a prompt,
an execution environment, and acceptance checks.
_Avoid_: Prompt alone, ticket

**Model strategy**:
The selected inference option, which may be a fixed model, automatic routing, or
a composite option such as Fusion.
_Avoid_: Model when referring to every strategy

**Configuration**:
A model strategy together with its declared capabilities, permissions, and
initial contextual inputs.
_Avoid_: Model when capability settings also differ, profile

**Run**:
One independent attempt by one Configuration to complete one Task from its
declared starting state.
_Avoid_: Session, conversation, comparison

**Comparison**:
A fixed collection of Tasks and Configurations evaluated through repeated Runs
under declared comparison conditions.
_Avoid_: Run, leaderboard

**Acceptance check**:
A predefined criterion used to assess a Task's resulting project state.
_Avoid_: Agent self-assessment, accuracy percentage

**Evaluation**:
The assessment of a Run's resulting project state against protected acceptance
checks, separate from the coding attempt.
_Avoid_: Agent completion

**Correctness**:
The observed satisfaction of acceptance checks, optionally supplemented by human
review, rather than a guarantee that the solution is defect-free.
_Avoid_: Accuracy, proof of correctness

**Efficiency**:
The relationship between observed Correctness and the resources consumed by a
Run, presented through separate measurements rather than a universal score.
_Avoid_: Overall winner score

**Reported usage**:
Resource or billing-unit measurements supplied by the inference service for a
Run, with their units and completeness identified.
_Avoid_: Invoice amount, estimated cost

**Estimated cost**:
A monetary approximation derived from attributable usage and documented pricing,
not the user's final invoiced charge.
_Avoid_: Amount billed, actual spend

**Run evidence**:
The retained inputs, outputs, observations, and resulting changes needed to
inspect a Run and interpret its measurements.
_Avoid_: Metrics alone
