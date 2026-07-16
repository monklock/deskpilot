# Module Development

Modules implement `IDeskPilotModule`, expose immutable metadata, register dependencies through `IServiceCollection`, and initialize with typed `IModuleContext`. Module initialization must not receive or retain `IServiceProvider`.

Each executable command has one `ICommandHandler`. Modules do not invoke other modules directly; command groups use `ICommandDispatcher`.
