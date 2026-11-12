---
title: Coding agent comparison requirements
description: Approved version-one scope, domain decisions, and feasibility gates.
ms.date: 2026-11-11
---

## Status and scope

The requirements below were approved through the design interview, including
the replacement of WPF with WinUI 3. Approval covers these documents only.
Application scaffolding and implementation have not been authorized or performed.

Canonical terms are defined in the [glossary](../GLOSSARY.md).

Version 1 is a single-user Windows desktop application built with C#/.NET,
WinUI 3, and the Windows App SDK. It uses the official GitHub Copilot SDK to
compare coding-agent Configurations on reproducible repository-editing Tasks.
Results stay local unless the user explicitly exports them. Inference still
connects to Copilot services; local storage does not mean offline inference.

Task execution uses Linux containers through the native
`Microsoft.WSL.Containers` API, not a Docker Desktop dependency. Windows-only
projects and native Windows task execution are outside version 1.

## Decision tree

The product decisions are settled. The technical gates below remain unproven.

* Local Windows application
  * WinUI 3, C#/.NET, and the official Copilot SDK
  * Native WSL container integration for Linux task execution
  * Explicit sign-in, local history, and user-initiated exports
* Reproducible coding Tasks
  * Fixed project snapshot, prompt, environment, and resource limits
  * Fresh isolated Runs with no interactive coaching
  * Protected acceptance checks and separate Evaluation
* Configuration comparisons
  * Fixed models, Auto, and Fusion when available through the user's SDK
  * Explicit capabilities, contextual inputs, and permissions
  * Sequential repeated attempts with rotated competitor order
* Evidence-based results
  * Separate Correctness, timing, tokens, usage units, and Estimated cost
  * Explicit failures, interruptions, missing telemetry, and routing uncertainty
  * No composite winner score or global leaderboard

## Task definition and preparation

Each Task specifies:

* A local Git repository and explicit starting commit
* The coding prompt and predefined acceptance checks
* A Linux container image or build recipe and dependency setup commands
* CPU, memory, and other applicable execution resource limits
* Evaluation commands and their independent time limit
* Any explicitly supplied initial contextual inputs

Uncommitted repository changes are excluded in version 1. Agents implement a
feature or fix a bug by editing the Task's disposable project copy, not the
original repository.

Resolve container images to immutable digests and record the prepared
environment identity. Complete dependency setup outside timed agent execution.
Every competitor receives the same starting project snapshot and prepared
environment for a given Task.

Verify that baseline Evaluation can execute before launching a Comparison.
Task-specific checks may fail on the starting commit; an unrunnable evaluator
must not be mistaken for an expected failing check.

Version 1 accepts user-supplied environment definitions. Automatic support for
arbitrary languages and repositories is not promised.

## Model strategies and Configurations

Allow selection of every model strategy actually available to the user through
the installed, compatible Copilot SDK/runtime and account, including Auto and
Fusion when supported. Do not silently substitute an unavailable strategy.
Bring-your-own-provider credentials are outside version 1.

Capture the requested strategy identifier and reported underlying model
identifiers. Auto and Fusion are not presented as measurements of one fixed
model. Preserve available per-model usage; mark undisclosed routing, constituent
models, or usage as unknown.

A Configuration includes its model strategy, capability settings, permissions,
and explicit contextual inputs. Snapshot the effective inputs for each Run.
Show differences between competitors, and support identical capability settings
for controlled model-only comparisons.

Expose documented controls supported by the pinned SDK/runtime. Candidate
control groups include:

* Built-in, custom, and MCP tool availability
* MCP server configuration and server enablement
* Web-tool availability and separate task network policy
* Memory, cross-session retrieval, and session-store behavior
* Skills, repository instructions, and other instruction discovery
* Custom or built-in agent delegation
* Model-supported reasoning effort and reasoning-summary settings
* Tool search, context management, and infinite-session behavior
* Documented telemetry, citation, and file-change-tracking settings

This list is a requirements inventory, not a verified .NET API binding map.
The feasibility milestone establishes exact supported controls and their
semantics. Advanced settings cover documented controls, not arbitrary internal
feature flags.

Indicate supported, unsupported, experimental, and unverified settings.
Block execution if a requested restriction cannot be enforced. Unknown or
ignored settings must not produce a success-shaped configuration.

Tool filtering, permission denial, and network isolation are distinct.
Reasoning summaries being disabled does not establish that reasoning is off.
Hiding web tools does not establish that task internet access is blocked.

## Isolation, contextual state, and permissions

Each Run starts with a fresh workspace, conversation, and isolated session
storage. Do not inherit host memory, approval records, instructions, skills,
MCP configuration, or other ambient configuration.

Memory-enabled Runs start empty unless the Task supplies a fixed seed copied
identically to applicable Runs through a supported mechanism. Memory may evolve
within a Run, but must not carry into another repetition or competitor.
Explicitly capture selected repository instructions, skills, and MCP definitions.
Keep credentials separate from exported Configuration definitions.

Require a disposable sandbox with only explicitly granted files, credentials,
and network access. A Git worktree or working-directory setting alone does not
satisfy isolation. There is no unsandboxed fallback.

Permissions are fixed before execution. Runs receive no follow-up coaching or
interactive permission approvals. Deny and record out-of-policy actions; retain
requests for human help rather than selectively helping a competitor.

Configurations without a permitted editing mechanism remain selectable as an
intentional baseline, with a preflight warning. Retain their text response and
evaluate the unchanged project. Do not extract and apply a patch from their
answer on their behalf.

### Network and MCP boundaries

Expose task network policies separately from web-tool availability:

* Blocked
* Explicit endpoint allowlist
* Unrestricted

Necessary inference-service connectivity is distinct from task internet access.
A requested restriction must be verified across task commands, tools, and MCP,
not merely through one SDK flag.

Run local MCP server processes inside the sandbox. Remote servers require
explicitly permitted endpoints and separately granted credentials. Do not
inherit host MCP processes.

Warn that permitted remote MCP services can provide external information or
cause external side effects. Do not label such a Configuration as universally
unable to access external information. Block unsupported policy combinations.

### Authentication and host changes

Use the official Copilot CLI sign-in flow rather than asking users to paste
tokens into the application. Prefer OS-backed credential storage where
available. Task commands must not receive host credentials through their
filesystem or environment.

Grant no repository-push or publishing capability by default. Require approval
before publishing changes, installing prerequisites automatically, or making
elevated system changes.

The placement of the SDK runtime and the mechanism for authenticated inference
without exposing host credentials to task processes remain feasibility gates.
Neither placing the runtime on the host nor inside a container proves this
property by itself.

## Comparison scheduling and lifecycle

A Comparison fixes its Tasks, Configurations, and execution conditions.
Default to three independent repetitions per Task and Configuration, with a
configurable repetition count. Execute sequentially and rotate Configuration
order across repetitions. Three repetitions are a practical starting point,
not a claim of statistical significance.

Require confirmation of the planned Run count before execution. Do not retry
automatically or silently perform additional potentially billable work.

Default the agent execution limit to ten minutes, configurable before launch.
End the attempt when the agent finishes, the user cancels, an execution error
occurs, or the execution limit is reached. Terminate descendant task processes
as part of cancellation and timeout handling; an SDK abort request alone is
not proof that they stopped.

Distinguish agent-finished, error, timeout, cancelled, and interrupted outcomes.
Record Evaluation status and Correctness separately. Agent completion does not
mean Task success.

After an application crash or restart, mark unfinished Runs interrupted and
preserve persisted evidence. Clean up only app-owned containers. Reruns require
explicit approval and fresh starting state; do not resume an old conversation
as a fresh attempt.

## Evaluation and Correctness

Evaluate the resulting project in a separate clean environment without a live
agent. Apply the resulting project changes while restoring the Task's protected
acceptance checks from their trusted definitions, not agent-modified copies.
The agent must not be able to change the scoring checks.

Identify withheld evaluation checks separately from checks available during
execution. Each acceptance command counts as one check unless it provides
supported structured results. Version 1 supports JUnit XML for individual test
results. Retain command outcomes and report evidence so infrastructure failures
remain distinguishable from failed acceptance checks.

Report pass/fail counts, checks that could not execute, and optional human
review. Do not ask another LLM to manufacture an accuracy percentage.
Passing checks is evidence of Correctness, not proof of a defect-free solution.

Evaluation has its own execution limit. Preserve results and partial evidence
for failed, timed-out, cancelled, and interrupted attempts when available;
identify when a resulting project or an evaluation result could not be captured.

## Measurements and interpretation

Report these independently:

* Correctness and acceptance-check outcomes
* Agent elapsed wall-clock time
* Setup and Evaluation durations
* Model API duration and first-token latency when reported
* Input, output, reasoning, and cache token counts when reported
* Attributable runtime-reported billing or usage units
* Estimated monetary cost when a documented conversion is available

Measure agent elapsed time from prompt submission to its terminal outcome.
Do not substitute summed API durations for elapsed time. Setup and Evaluation
do not inflate agent execution time.

Preserve units, pricing sources, pricing timestamps, and telemetry completeness.
Do not double-count cache or reasoning token subsets when presenting totals.
Missing measurements remain unavailable rather than becoming zero. Persist
ephemeral usage events as they arrive, including attributable delegated-agent
usage when reported.

The SDK's usage and cost-shaped fields must be interpreted according to their
documented units; they are not automatically currency. Label monetary values
Estimated cost, not Amount billed. Account-wide quota changes are not precise
per-Run spend. Do not claim a hard dollar cap when reporting or cancellation
cannot enforce it.

Show individual Runs and summaries, including Correctness success rates and
resource distributions. State the denominator and included terminal outcomes
for each summary. Never hide failed attempts or present only the best Run.
Keep efficiency measurements alongside Correctness; a fast incorrect result
does not automatically win.

## User experience and retained evidence

Version 1 includes:

* Task and environment setup
* Reusable Configuration editing with capability validation
* Comparison launch and preflight confirmation
* Live status and available execution events
* Cancellation
* Per-Run metrics, acceptance-check output, and code diffs
* Local history, deletion, and explicit result export

Persist results in a local database with artifact files. Retain Configuration
snapshots, environment identifiers, code patches, check results, and available
execution events. Evidence survives disposable-container cleanup.

Provide explicit JSON and CSV result exports and patch export. Omit credentials
and redact known secrets. Warn that prompts, code, diffs, and tool output can
still contain sensitive information; redaction is not a guarantee of
anonymization. Do not upload automatically.

Cloud/team accounts, an interactive coding chat, a global leaderboard, a
composite winner score, and automatic patch application from text-only answers
are outside version 1.

## Windows feasibility milestone

Before full application development, prove the following together on Windows:

1. WinUI 3/.NET application connectivity through a pinned official Copilot SDK
   and compatible runtime.
1. Account sign-in and discovery or validated selection of available strategies,
   including Auto and Fusion where available.
1. Native WSL container creation, prepared-image execution, project transfer,
   result capture, and app-owned resource cleanup.
1. Credential separation and enforceable host-file and task-network boundaries,
   including built-in tools, task commands, and MCP startup and execution.
1. Effective capability restrictions, with unsupported combinations rejected
   before execution rather than ignored.
1. Cancellation and timeout termination of descendant task processes.
1. Available usage telemetry, routing attribution, missing-data behavior, and
   durable capture of ephemeral events.
1. Protected-check Evaluation against resulting code in a separate clean
   environment.

Microsoft documents WSL container support starting with WSL 2.9.3 and provides
the C# `Microsoft.WSL.Containers` API. This establishes API availability, not
that all required isolation properties have been demonstrated.

SDK feature support can depend on the language binding, runtime version,
managed policies, account, and selected model. Pin compatible SDK/runtime
versions, record them, and reject versions that cannot enforce required
restrictions. Experimental APIs require explicit compatibility validation.

Some Windows feasibility checks have been executed, but the acceptance gates
remain incomplete. The
[WSL container feasibility status](./spec.md#wsl-container-isolation-feasibility)
records the evidence and remaining gaps. If a required guarantee fails or
cannot be demonstrated, stop and revisit the design rather than weakening it
or falling back to unsandboxed execution.

## Source references

Public documentation consulted during the interview on 2026-11-11:

* [GitHub Copilot SDK overview](https://github.com/github/copilot-sdk/blob/main/README.md)
* [SDK configuration and model types](https://github.com/github/copilot-sdk/blob/main/nodejs/src/types.ts)
* [SDK usage and billing](https://github.com/github/copilot-sdk/blob/main/docs/features/usage-and-billing.md)
* [SDK MCP configuration](https://github.com/github/copilot-sdk/blob/main/docs/features/mcp.md)
* [SDK authentication](https://github.com/github/copilot-sdk/blob/main/docs/auth/authenticate.md)
* [SDK compatibility guidance](https://github.com/github/copilot-sdk/blob/main/docs/troubleshooting/compatibility.md)
* [WSL container overview and C# API](https://learn.microsoft.com/en-us/windows/wsl/wsl-container?tabs=csharp)
* [WSL container API reference](https://wsl.dev/api-reference/)
* [WSL container settings](https://learn.microsoft.com/en-us/windows/wsl/wsl-container-settings)

SDK sources on `main` are mutable and include examples outside .NET. Revalidate
exact .NET bindings and runtime semantics against the pinned versions at the
feasibility milestone.
