# ADR-0003: WPF Generic Host lifecycle

## Decision

Use Generic Host as the desktop composition root while WPF owns the process window lifecycle.

## Consequences

The host starts before the main window is shown and stops with a bounded timeout when the application exits. The tray icon hides the window on close and exposes explicit open and exit actions.
