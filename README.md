# compareModels

`compareModels` is an early-stage Windows project for comparing coding-agent
configurations. The repository currently contains feasibility experiments, not
the full comparison application. A bounded isolated Copilot Run and protected
Evaluation spike are implemented, but there is no Task/Run comparison workflow,
model configuration UI, production evaluator, or results database.

Start with the [project overview](docs/project-overview.md) for the implemented
components, architecture, technology stack, and verification boundaries. The
approved product scope is in [requirements](docs/requirements.md) and its
technical status and evidence are in the [specification](docs/spec.md).

The Windows desktop feasibility interface and its run instructions are
documented in [`src/WslContainerFeasibility/README.md`](src/WslContainerFeasibility/README.md).
That guide also describes the [isolated Copilot Run](src/WslContainerFeasibility/README.md#isolated-copilot-run)
console harness, private token entry, and billable-work confirmation.

The separate [MXC WSLC Evaluation spike](src/MxcWslcFeasibility/README.md)
compares the packaged MXC policy/SDK layer with the native WSL baseline.
