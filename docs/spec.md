---
title: Compare coding-agent configurations on Windows
description: Version-one specification synthesized from the approved design interview.
ms.date: 2026-11-11
---

## Problem Statement

A developer wants to compare the coding capabilities of the model strategies
available through their GitHub Copilot account. Comparing chat responses alone
does not establish whether an agent can complete a real coding Task, produce
correct changes, or use time and tokens efficiently.

Existing ad hoc comparisons can mix different starting code, permissions,
memory, tools, and environments. A fast answer can be incorrect, a reported
cost field can represent nonmonetary units, and a single successful attempt can
hide repeated failures. Automatic or composite model strategies also make
attribution to a single model misleading.

The developer needs a local Windows application that runs controlled
Comparisons, safely executes generated commands, and retains enough evidence
to understand the results without overstating Correctness or actual spend.

## Solution

Provide a single-user WinUI 3 desktop application using C#/.NET, the official
GitHub Copilot SDK, and native WSL containers for Linux task execution.

Users define reproducible Tasks and reusable Configurations, select model
strategies including Auto and Fusion when supported, and launch repeated,
unattended Runs. Every Run receives fresh state and a disposable sandbox.
Protected acceptance checks evaluate its resulting code separately.

Present Correctness, timing, tokens, reported usage, and Estimated cost as
separate measurements. Show configuration differences, failures, unavailable
metrics, and routing uncertainty explicitly. Preserve local Run evidence and
provide user-initiated exports.

A Windows feasibility milestone must establish SDK compatibility, enforceable
isolation, credential separation, and process termination before full
application development. Failed guarantees require a design decision, not an
unsandboxed fallback.

## User Stories

1. As a developer, I want a local Windows desktop application, so that I can
   manage Comparisons without deploying a shared service.
2. As a developer, I want to sign in through the official Copilot flow, so that
   I do not have to paste access tokens into the application.
3. As a developer, I want to see missing prerequisites and compatibility
   problems before execution, so that failed setup is not confused with model
   performance.
4. As a developer, I want approval prompts before automatic prerequisite
   installation or elevated changes, so that I control changes to my machine.
5. As a developer, I want to select a local Git repository and starting commit,
   so that every competitor receives the same project state.
6. As a developer, I want uncommitted changes excluded from the Task snapshot,
   so that unfinished work does not accidentally affect a Comparison.
7. As a developer, I want to supply a coding prompt and acceptance checks, so
   that Task success is defined before any competitor attempts it.
8. As a developer, I want to supply a Linux container image or build recipe,
   so that Tasks have the dependencies their projects require.
9. As a developer, I want prepared environments pinned to recorded image
   digests, so that mutable image tags do not silently change the experiment.
10. As a developer, I want to declare setup commands and resource limits, so
    that competitors operate under the same execution conditions.
11. As a developer, I want baseline Evaluation checked before launch, so that
    unrunnable checks are distinguished from checks that correctly fail on
    unfinished code.
12. As a developer, I want to select any available Copilot model strategy,
    including Auto and Fusion when supported, so that I can compare the
    inference options my account actually offers.
13. As a developer, I want unavailable strategies rejected rather than
    substituted, so that the application measures the competitor I selected.
14. As a developer, I want requested and reported model identifiers retained,
    so that automatic or composite routing is not misrepresented as one fixed
    model.
15. As a developer, I want undisclosed routing or constituent usage marked
    unknown, so that missing attribution is not replaced with a guess.
16. As a developer, I want reusable Configurations combining a model strategy
    and capability settings, so that I can compare both model-only changes
    and complete agent setups.
17. As a developer, I want configuration differences displayed, so that I can
    recognize when a result cannot be attributed solely to the model.
18. As a developer, I want controls for supported tools, MCP servers, memory,
    skills, instructions, and other documented optional capabilities, so that
    I can test their effect on the same Task.
19. As a developer, I want supported reasoning settings exposed per strategy,
    so that I do not request settings the selected strategy cannot honor.
20. As a developer, I want unsupported, experimental, and unverified settings
    identified, so that I know which restrictions are established.
21. As a developer, I want unenforceable requested restrictions to block a
    Run, so that the application never silently weakens my Configuration.
22. As a developer, I want web-tool availability separate from task network
    permissions, so that disabling one tool is not mistaken for blocking all
    external information access.
23. As a developer, I want blocked, allowlisted, and unrestricted task network
    policies, so that external access is an explicit comparison condition.
24. As a developer, I want local MCP processes confined to the sandbox and
    remote MCP endpoints explicitly permitted, so that MCP does not silently
    bypass the execution boundary.
25. As a developer, I want warnings about remote MCP information access and
    external side effects, so that I understand the consequences of enabling
    a service.
26. As a developer, I want fresh conversation and contextual state for every
    Run, so that earlier attempts do not benefit later competitors.
27. As a developer, I want memory to start empty or from an explicit fixed
    seed, so that memory-enabled Runs have reproducible initial conditions.
28. As a developer, I want repository instructions, skills, and MCP definitions
    explicitly selected and captured, so that ambient host configuration does
    not influence results.
29. As a developer, I want task commands denied access to host credentials,
    so that generated code cannot read my Copilot or other host secrets.
30. As a developer, I want disposable isolated execution without a host
    fallback, so that a failed sandbox does not result in unrestricted command
    execution.
31. As a developer, I want repository-push and publishing capabilities absent
    by default, so that coding attempts do not publish their changes.
32. As a developer, I want unattended Runs with fixed permissions, so that
    selective coaching or approval does not distort the Comparison.
33. As a developer, I want denied actions and requests for human help retained,
    so that I can explain why a competitor could not continue.
34. As a developer, I want a warning before running a Configuration without an
    editing mechanism, so that I can intentionally include a no-tools baseline.
35. As a developer, I want text-only answers retained without automatic patch
    application, so that the app does not perform coding work on behalf of
    one competitor.
36. As a developer, I want three fresh sequential repetitions by default with
    rotated competitor order, so that results do not depend on one attempt
    or an always-first competitor.
37. As a developer, I want to configure repetitions and confirm the planned
    Run count, so that I understand the amount of potentially billable work.
38. As a developer, I want setup excluded from timed agent execution, so that
    dependency preparation is not reported as coding-agent latency.
39. As a developer, I want a configurable execution timeout with a ten-minute
    default, so that an unattended attempt cannot run indefinitely.
40. As a developer, I want live status and available execution events, so that
    I can see what a Run is doing.
41. As a developer, I want cancellation to terminate descendant task processes,
    so that stopping the agent also stops its ongoing local work.
42. As a developer, I want completion, error, timeout, cancellation, and
    interruption distinguished, so that unsuccessful attempts remain visible.
43. As a developer, I want protected checks evaluated in a separate clean
    environment without a live agent, so that the competitor cannot change
    the scoring criteria while being evaluated.
44. As a developer, I want withheld checks identified, so that results show
    which criteria the agent could inspect during execution.
45. As a developer, I want command-level checks and JUnit XML results
    supported, so that I can inspect both simple acceptance commands and
    individual test outcomes.
46. As a developer, I want failed checks distinguished from checks that could
    not execute, so that infrastructure problems are not reported as ordinary
    correctness failures.
47. As a developer, I want optional human review alongside check results, so
    that I can assess qualities the automated checks do not cover.
48. As a developer, I want Correctness presented without claims of proof or
    invented LLM accuracy percentages, so that the result reflects the actual
    evidence.
49. As a developer, I want agent elapsed time, API duration, setup time, and
    Evaluation time separated, so that timing comparisons have clear meanings.
50. As a developer, I want token counts and cache or reasoning subsets
    identified without double-counting, so that token comparisons are
    interpretable.
51. As a developer, I want missing or partial telemetry explicitly marked, so
    that absent measurements do not look like zero consumption.
52. As a developer, I want reported usage units separated from Estimated cost,
    so that a premium-request or credit value is not mislabeled as money.
53. As a developer, I want monetary estimates to retain their documented
    pricing source and timestamp, so that I can understand their basis.
54. As a developer, I want warnings that time limits and cancellation do not
    guarantee a hard dollar cap, so that I do not confuse execution controls
    with an invoice guarantee.
55. As a developer, I want individual Runs and summaries with explicit
    denominators and included outcomes, so that failures and unsuccessful
    attempts are not hidden by averages.
56. As a developer, I want separate quality and resource measurements rather
    than a universal winner score, so that a fast incorrect answer is not
    automatically ranked best.
57. As a developer, I want code diffs, check output, configuration snapshots,
    and execution evidence retained locally, so that I can explain and audit
    a result after container cleanup.
58. As a developer, I want JSON and CSV result exports and patch export, so
    that I can analyze or inspect results outside the application.
59. As a developer, I want credentials omitted and known secrets redacted
    from exports, with residual sensitivity warnings, so that exporting does
    not imply the evidence is safe to share.
60. As a developer, I want saved Comparisons deletable without automatic
    upload, so that I control local evidence retention.
61. As a developer, I want unfinished Runs marked interrupted after restart,
    so that a crash is not reported as success.
62. As a developer, I want reruns to require approval and start fresh, so that
    recovery does not silently incur charges or reuse an old conversation.
63. As a developer, I want cleanup restricted to app-owned resources, so that
    unrelated WSL workloads and files remain untouched.

## Implementation Decisions

### Application and execution boundary

* Build a Windows-only WinUI 3 application using C#/.NET and the Windows App
  SDK. WPF is not the selected UI framework.
* Use the official .NET Copilot SDK and native
  `Microsoft.WSL.Containers` integration. Do not require Docker Desktop.
* Separate desktop presentation from the public Comparison workflow. That
  workflow is the primary behavioral contract for orchestration, terminal
  outcomes, persisted evidence, and exports.
* Keep SDK communication, container execution, Evaluation, and local evidence
  persistence behind that workflow. Their exact internal decomposition is not
  prescribed by this spec.
* Require the Windows feasibility milestone before full application
  development. SDK/runtime placement and authenticated inference without
  exposing credentials to task processes must be proven, not assumed.

### Domain and persistence contracts

* A Task combines a fixed starting commit, prompt, prepared environment,
  resource limits, and acceptance checks. Exclude uncommitted project changes.
* A Model strategy identifies a fixed model, Auto, or Fusion when actually
  supported. A Configuration adds capabilities, permissions, and initial
  contextual inputs.
* A Run is one independent attempt by one Configuration on one Task. A
  Comparison fixes the participating Tasks, Configurations, and conditions.
* Persist local history in a database with artifact storage. No existing
  schema requires migration because the codebase currently contains
  documentation only.
* Retain effective Configuration and Task inputs, runtime/environment
  identifiers, requested and reported model identifiers, terminal outcomes,
  Evaluation outcomes, code changes, telemetry completeness, and available
  execution events.
* Keep execution outcomes separate from Correctness. Agent-finished, error,
  timeout, cancelled, and interrupted must remain distinguishable from
  acceptance-check outcomes and Evaluation infrastructure failures.
* Resolve container images to immutable digests and record the prepared
  environment identity. Do not claim that a pinned snapshot makes
  nondeterministic inference or external services reproducible.

### Capabilities and external access

* Discover available model strategies and validate selections against the
  installed runtime and account. Do not substitute unavailable strategies.
* Capture observed underlying-model usage where available. Unknown routing,
  composite constituents, or usage remain unknown.
* Expose documented, verified optional controls supported by the pinned
  .NET SDK/runtime. Include tools, MCP, memory and retrieval, skills and
  instructions, agent delegation, supported reasoning settings, tool search,
  context management, and optional telemetry-related features where supported.
* Mark unsupported, experimental, and unverified settings explicitly.
  Unsupported requested restrictions fail preflight rather than being ignored.
* Distinguish tool availability, permission decisions, and network policy.
  Disabling reasoning summaries does not disable reasoning; hiding a web tool
  does not prevent shell or MCP network access.
* Provide blocked, allowlisted, and unrestricted task network policies.
  Necessary inference-service connectivity is distinct from task internet
  access. Unsupported combinations fail preflight.
* Start local MCP processes inside the sandbox. Require explicit permission
  for remote MCP endpoints and warn about their external information access
  and side effects.
* Use explicit contextual inputs rather than ambient host configuration.
  Each Run receives fresh conversation and session state. Memory starts empty
  or from a fixed Task seed through a supported mechanism, never from a
  previous Run.

### Credentials and safety

* Use official Copilot sign-in instead of an application token-entry flow.
  Prefer OS-backed credential storage where available.
* Keep credentials separate from exported Configurations and inaccessible
  through the task filesystem and command environment. MCP credentials are
  explicit separate grants.
* Require a disposable sandbox without an unsandboxed fallback. A working
  directory or Git worktree alone does not establish a security boundary.
* Fix permissions before launch. Deny and record out-of-policy actions without
  prompting for selective mid-Run approval or coaching.
* Grant no repository-push or publishing capability by default. Require
  approval before publishing, automatic prerequisite installation, or elevated
  system changes.
* Require explicit Windows verification of host-file restrictions, task-network
  restrictions, credential separation, and process termination across tools,
  commands, and MCP.

### Scheduling and Evaluation

* Default to three fresh sequential repetitions per Task and Configuration.
  Rotate Configuration order across repetitions and allow the repetition
  count to be set before launch.
* Complete dependency setup before timed execution and verify that baseline
  Evaluation can run. Expected failing checks on the starting commit are
  allowed; an unrunnable evaluator is a different outcome.
* Default to a configurable ten-minute agent execution limit. End on agent
  completion, user cancellation, execution error, or timeout.
* Cancellation and timeout handling must terminate descendant task processes.
  An SDK abort request alone is insufficient evidence of termination.
* Evaluate resulting code separately in a clean environment without a live
  agent. Restore protected checks from trusted definitions rather than trusting
  agent-modified scoring files.
* Treat each acceptance command as one check unless it provides supported
  structured results. Support JUnit XML for individual test outcomes. Record
  command outcomes alongside check results and distinguish checks that could
  not run.
* Give Evaluation its own time limit and timing measurement. Identify withheld
  checks separately and allow optional human review.
* For a Configuration with no editing mechanism, warn before execution, retain
  the response, and evaluate the unchanged project. Do not apply a generated
  patch on the competitor's behalf.
* Never retry automatically. On restart, mark unfinished Runs interrupted,
  preserve available evidence, and clean up only app-owned containers.
  Explicit reruns always start fresh.

### Metrics, presentation, and export

* Measure agent elapsed time from prompt submission to its terminal outcome.
  Record setup and Evaluation separately; summed API duration is not elapsed
  wall-clock time.
* Keep Correctness, timing, tokens, usage units, and Estimated cost separate.
  Do not compute a composite winner score.
* Preserve token categories without adding cache or reasoning subsets twice.
  Missing optional telemetry remains unavailable, never zero.
* Persist ephemeral usage events as received and retain attributable
  delegated-agent usage where available. Failed or cancelled Runs may have
  partial telemetry that must remain visible.
* Interpret cost-shaped runtime fields using their documented units. Show
  monetary estimates only when a documented conversion is available, with
  pricing source, timestamp, and completeness.
* Do not infer precise per-Run spend from account-wide quota changes or promise
  actual invoiced amounts and hard dollar caps.
* Show individual Runs and summaries, stating denominators and included
  terminal outcomes. Three default repetitions do not establish statistical
  significance.
* Provide Task setup, Configuration editing, launch confirmation, live events,
  cancellation, local history, check output, and code diffs.
* Support explicit JSON and CSV result exports and patch export. Omit
  credentials, redact known secrets, and warn that retained code and execution
  output can still contain sensitive information.

## Testing Decisions

### Primary seam

The user approved the public Comparison workflow as the primary testing seam:
submit Tasks and Configurations, then observe persisted Run outcomes and
exported evidence. Prefer tests at this boundary over tests of internal
orchestration classes or private helper methods.

A good test expresses observable behavior that matters to a developer. It
should fail when a required guarantee is broken, not when internal modules
are reorganized. Use controlled SDK and execution responses for repeatable
workflow cases; do not make deterministic tests depend on stochastic model
answers or live billing.

There is no application code, test framework, or existing test prior art in
the current codebase. No established testing pattern can be reused yet. Do
not add extra public seams solely to test internal implementation details.

### Workflow behavior

Cover these behaviors through the primary seam:

* Reject unsupported strategies, unenforceable restrictions, and unrunnable
  Evaluation before starting billable agent work.
* Preserve effective inputs and expose Configuration differences without
  including uncommitted project changes.
* Produce the requested repetition count sequentially with rotated competitor
  order and fresh state, without automatic retries.
* Keep permission denials, human-help requests, unavailable routing, and
  unsupported telemetry observable.
* Distinguish completion, timeout, error, cancellation, and interruption from
  Correctness and Evaluation failure.
* Evaluate protected checks against resulting code, not agent-rewritten
  scoring definitions.
* Distinguish command-level checks, JUnit XML check results, and evaluation
  commands that could not execute.
* Leave the project unchanged for text-only no-editing baselines while
  retaining their response.
* Preserve missing measurements as unavailable and partial telemetry as
  partial. Validate units, subset-aware token totals, and estimate provenance.
* Report timing boundaries and summary denominators correctly, including
  unsuccessful attempts rather than selecting only successful Runs.
* Persist evidence across cleanup and application restart; require explicit
  fresh reruns for interrupted work.
* Export the supported result shapes and code patches without credentials,
  and delete selected local history without affecting unrelated resources.

Use focused assertions for the behavior each scenario introduces. Avoid
tests that only restate configuration constants, trivial accessors, or
framework behavior.

### Windows feasibility and integration

Use real Windows integration tests for guarantees a simulated workflow cannot
prove:

* WinUI 3/.NET, compatible Copilot SDK/runtime, sign-in, and model-strategy
  discovery or validated selection
* Native WSL container creation, prepared-image execution, project transfer,
  result collection, and app-owned cleanup
* Isolation from host credentials, host files, and unrelated WSL workloads
* Effective blocked and allowlisted task network policies across commands,
  built-in tools, and local or remote MCP
* Actual descendant-process termination after cancellation and timeout
* Separate clean Evaluation with protected acceptance checks
* Real telemetry availability and attribution, including unavailable fields
  and strategy-dependent behavior

Pin and record compatible SDK/runtime versions for these tests. A successful
SDK call or container creation is not a proxy for the required isolation
guarantees. Never claim these tests passed based solely on a Linux build.

Live integration tests must use bounded, explicitly authorized potentially
billable work. They must not assert exact stochastic responses or a fixed
invoice amount.

### Desktop smoke coverage

Add a small WinUI smoke test for the user journey: configure a Task and
Configurations, confirm launch, observe progress, cancel a Run, and inspect
retained results. Keep orchestration correctness tests at the workflow seam,
not duplicated through every UI control.

## Out of Scope

* Application implementation under this documentation-only authorization
* A WPF frontend or browser-based desktop shell
* Docker Desktop as a prerequisite
* Native Windows task execution, Windows containers, and Windows-only projects
* Automatic environment support for arbitrary languages or repositories
* Unsandboxed task execution or inherited host configuration
* Interactive coaching, mid-Run approval, and automatic patch application from
  text-only responses
* Automatic retries, automatic billable recovery, and conversation resumption
  presented as a fresh Run
* Bring-your-own-provider credentials and an application token-entry flow
* Arbitrary internal SDK feature flags or unverified capability guarantees
* Cloud storage, team accounts, automatic uploads, and collaborative execution
* An interactive coding chat, global leaderboard, and composite winner score
* LLM-invented accuracy percentages or guarantees of defect-free code
* Exact invoiced spend, quota-delta-based per-Run charges, and unverified hard
  dollar caps

## Further Notes

The approved source requirements are recorded in the
[requirements](./requirements.md), with canonical terms in the
[glossary](../GLOSSARY.md). The testing seam was confirmed during specification
preparation.

[Microsoft's WSL container documentation](https://learn.microsoft.com/en-us/windows/wsl/wsl-container?tabs=csharp)
documents WSL 2.9.3 or later and the C# container API. API availability alone
does not verify this specification's isolation requirements.

The [Copilot SDK](https://github.com/github/copilot-sdk) has model-, binding-,
account-, policy-, and runtime-dependent capabilities. Revalidate exact .NET
bindings, Auto/Fusion selection, restriction semantics, and telemetry against
pinned versions during the Windows feasibility milestone. Experimental
features require explicit compatibility checks.

No application or Windows feasibility tests have been executed. Full
application development must stop if a required guarantee cannot be proven,
pending an explicit design revision.

The project uses GitHub Issues in
[`sifiadlene/compareModels`](https://github.com/sifiadlene/compareModels), as
configured in the [tracker conventions](./agents/issue-tracker.md) and
[triage label mapping](./agents/triage-labels.md). Following user approval,
this specification was published as
[issue #1](https://github.com/sifiadlene/compareModels/issues/1) with the
`ready-for-agent` label and no additional triage.

### WSL container isolation feasibility

Status on 2026-11-12: partial evidence for
[issue #2](https://github.com/sifiadlene/compareModels/issues/2). The native
WSL Container API runs Linux containers in WSL-backed sessions. Microsoft's
[WSLC architecture description](https://devblogs.microsoft.com/commandline/wslc-architecture-deep-dive/)
describes a session VM, per-session storage, and a session process running on
behalf of the calling user. This differs from simply running a process in a
normal WSL distribution.

The general [WSL security model](https://wsl.dev/technical-documentation/security/)
warns against assuming WSL and containers provide a host security boundary for
untrusted code. That warning does not by itself establish the outcome of the
specific native WSL Container configuration and tests required by this issue.
Neither the architecture description nor API availability establishes that the
required host-file, credential, and network restrictions are enforced.

The [feasibility harness](../src/WslContainerFeasibility/README.md) uses
`Microsoft.WSL.Containers` `3.0.5` and Alpine `3.22`, pinned to manifest digest
`sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f`.
It ran on Windows build `10.0.26300.9974`, WSL and WSL container runtime
`3.0.5.0`, WSL kernel `6.18.58.1-1`, and .NET runtime `10.0.13`.

The Windows .NET harness observed that its synthetic host-temp-file canary was
not readable at the corresponding `/mnt/c/...` path in a container configured
with no mounts. Its synthetic host environment-variable canary was not present
in the container process. A bridged control container reached a synthetic HTTP
endpoint listening on Windows loopback through `host.wslc.internal`; a
container configured with `ContainerNetworkingMode.None` could not resolve or
reach that endpoint. The harness deleted its containers and terminated its
named WSL session; a subsequent `wslc info` showed only the pre-existing CLI
session, and `wslc list --all` showed no containers.

Additional checks ran a child process that repeatedly wrote to a dedicated
synthetic host directory mounted into its container. After both the
cancellation-style `SIGTERM` stop and the timeout-style `SIGKILL` stop
returned, the container was `Exited` and the heartbeat stopped changing during
a one-second observation window. A separate harness-created control session's
container, started before the application-owned session, remained running and
accepted a process after the application-owned session was terminated. Cleanup
stopped and deleted this control container and terminated its session.

These checks do not prove general host-file isolation, protect real
credentials, or establish resistance to deliberate container escapes. They do
not test project transfer, WinUI, Copilot SDK authentication, MCP, process
descendant cleanup through the production application's cancellation and
timeout workflow, preservation of pre-existing unrelated resources, or restart
recovery. They establish only the behaviors observed for this pinned image and
configuration. No coding Task is enabled by this proof harness.

Run only controlled feasibility tests while these restrictions are unverified;
do not enable coding Tasks or treat passing canary tests as proof against every
possible escape. If a required guarantee fails or cannot be demonstrated, stop
and record the specific blocker rather than using an unsandboxed fallback.
