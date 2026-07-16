# ADR-0001: Modular monolith

## Decision

Use a modular monolith with static module registration through dependency injection.

## Consequences

Core remains independent of platform adapters. Dynamic external module loading is deferred while module metadata and boundaries remain stable.
