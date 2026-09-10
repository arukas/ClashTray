# Development and licensing policy

## ClashTray

New ClashTray source code is intended to be independently authored and distributed under the MIT License.

## GPL projects used as references

GPL-licensed projects may be studied for general functionality, interoperability requirements, public protocol behavior, and user-experience concepts. Their source code must not be copied, mechanically translated, or incorporated into ClashTray unless the resulting licensing implications are explicitly reviewed first.

Do not translate, port, rewrite, or mechanically convert ClashBar GPL source files into C#. Do not preserve a one-to-one mapping of source file names, class names, method names, method ordering, comments, private helper structure, or control flow. Implement the required behavior independently with Mihomo's public API, Windows platform APIs, observable behavior, and project-specific requirements.

## Mihomo

Mihomo is treated as a separate executable. Integration uses its documented External Controller API or normal process-management interfaces. Do not import Mihomo source packages into ClashTray, link Mihomo as a native library, or compile Mihomo code directly into ClashTray.
