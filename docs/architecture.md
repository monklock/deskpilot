# Architecture

DeskPilot is an extensible modular monolith. `DeskPilot.Core` contains domain contracts and has no dependency on WPF, SQLite, Windows APIs, or speech providers. `DeskPilot.Application` dispatches only registered commands. `DeskPilot.Infrastructure` implements local paths and SQLite persistence. `DeskPilot.Desktop` is the WPF composition root.

Modules register statically through dependency injection and do not call each other directly. Voice, desktop UI, and future control surfaces use `ICommandDispatcher`.

SQLite is stored at `%LOCALAPPDATA%\DeskPilot\data\deskpilot.db`; Task 0.1 creates only application and voice settings tables.
