# Development and licensing policy

## Agent Vibe execution

Implementation and coding use `gpt-6.1-sol` with reasoning effort `max`, as required by the root [AGENTS.md](../AGENTS.md). This is the current execution requirement even when a historical guide or prompt names an older model. Historical implementation reports retain the model information recorded at the time. Session configuration selects the actual model; repository text does not switch it.

The current design-improvement work is specified in the [2026-10-02 Agent Vibe guide](design-optimization-vibe-2026-10-02.md). The [companion execution prompt](design-optimization-prompt-2026-10-02.md) authorizes implementation when the user sends it as an instruction. Preparing these documents alone is documentation work.

## ClashTray

New ClashTray source code is intended to be independently authored and distributed under the MIT License.

## GPL projects used as references

GPL-licensed projects may be studied for general functionality, interoperability requirements, public protocol behavior, and user-experience concepts. Their source code must not be copied, mechanically translated, or incorporated into ClashTray unless the resulting licensing implications are explicitly reviewed first.

Do not translate, port, rewrite, or mechanically convert ClashBar GPL source files into C#. Do not preserve a one-to-one mapping of source file names, class names, method names, method ordering, comments, private helper structure, or control flow. Implement the required behavior independently with Mihomo's public API, Windows platform APIs, observable behavior, and project-specific requirements.

## Mihomo

Mihomo is treated as a separate executable. Integration uses its documented External Controller API or normal process-management interfaces. Do not import Mihomo source packages into ClashTray, link Mihomo as a native library, or compile Mihomo code directly into ClashTray.
