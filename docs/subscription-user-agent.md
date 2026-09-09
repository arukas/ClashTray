# Subscription User-Agent

Subscription import, manual refresh and scheduled refresh use `ConfigurationStore.ImportSubscriptionAsync` and send `User-Agent: clash.meta/v1.19.30`.

This matches the bundled Mihomo release's default `GlobalUA` (`"clash.meta/" + C.Version`):
https://github.com/MetaCubeX/mihomo/blob/v1.19.30/config/config.go#L469

`packaging/mihomo-release.json` is the shared source for the bundled version and archive checksum. The EXE packaging script reads it; Core embeds it to derive the subscription UA even when the service/core is stopped. When upgrading the bundled core, update this manifest, verify the new release's UA format, and update the pinned-version regression assertions.

This change does not convert other subscription formats or bypass configuration validation. A server returning HTML, an expired subscription response, or non-Mihomo content will still fail validation. Tests use a synthetic HTTP handler and no private subscription URLs.
