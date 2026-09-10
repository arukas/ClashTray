# Startup fix — 2026-09-11

This patch repairs the Windows startup path when the saved `StartWithWindows` preference is already enabled but the per-user Run registration is missing or stale. An explicit Settings save now reconciles the `ClashTray` Run value, creates the missing Run key, and uses the installed executable path only when it is a fully qualified existing `.exe`.

The registration update is transactional with the JSON settings save. If saving settings fails, the prior Run value and value kind are restored only while the value still matches the command written by ClashTray. A command changed by another application is preserved. Windows `StartupApproved` state is read for status display and never overwritten; a disabled entry directs the user to Task Manager's Startup apps page.

Custom-path runtimes used by tests and UI smoke runs use in-memory startup and isolated service backends, so they do not access the user's Run key or installed service. Automatic core startup is awaited and only runs from Missing, Stopped, or Failed states; a confirmed Running core is left alone.

The latest user instruction requested immediate handoff without additional local build or test execution. Baseline verification remains the previously recorded complete Debug x64 build, 68 core tests, and 3 integration tests; this patch's new build and regression tests were not run in this handoff.
