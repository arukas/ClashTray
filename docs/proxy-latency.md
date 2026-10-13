# Proxy latency and current-node display

The group test button measures the entire group through Mihomo's `/group/{name}/delay` endpoint. It refreshes proxy history and `now` from `/proxies`, applies the confirmed batch results, and preserves expanded groups. Repeated requests for the same group are disabled until completion; core lifecycle operations and batch testing are serialized.

Node and nested-group latency comes from the last history record. An empty or malformed history displays `未测速`; a recorded zero displays `超时`; positive values display milliseconds. A failed or cancelled batch request does not invent latency values.

Selector, URLTest, and Fallback members show the confirmed current node using the selection highlight, checkmark, bold name, and `当前` label. These indicators update when the core changes `now`.

This resolves [issue #7](https://github.com/arukas/ClashTray/issues/7). No additional per-member test button is introduced.

## Validation

- Complete solution builds with no warnings or errors; 71 unit/integration test cases pass.
- Tests cover latest-history selection, empty/malformed/zero histories, nested groups, escaped group names, whole-group results, refreshed automatic selection, cancellation, failures and retries.
- Isolated WinUI validation confirms URLTest and Fallback current markers follow `now`, and distinguishes milliseconds, timeout and untested states.
- An isolated official Mihomo v1.19.30 process successfully returned both member results for Selector, URLTest and Fallback groups against a local HTTP HEAD endpoint. Latest history and `now` were checked. TUN and proxy listeners were disabled and system proxy settings were untouched.
- Real subscription nodes and remote network availability were not part of this isolated check.

API behavior was checked against upstream [group routes](https://github.com/MetaCubeX/mihomo/blob/v1.19.30/hub/route/groups.go) and [proxy history behavior](https://github.com/MetaCubeX/mihomo/blob/v1.19.30/adapter/adapter.go). Implementation is independent C# code.
