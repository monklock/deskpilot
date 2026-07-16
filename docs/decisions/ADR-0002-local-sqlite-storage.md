# ADR-0002: Local SQLite storage

## Decision

Store the SQLite database in the current user's LocalApplicationData directory and create contexts through `IDbContextFactory`.

## Consequences

Desktop UI does not hold a long-lived DbContext. Migrations are applied through an explicit initializer.
