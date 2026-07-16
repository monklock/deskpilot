# Requirements

DeskPilot targets Windows 10/11 x64 and uses C# with .NET 10, WPF, MVVM, SQLite, Entity Framework Core, Generic Host, dependency injection, and structured logging.

The wake phrase is `альфа`. A future wake-word provider listens only for this limited grammar. A future speech-to-text provider processes a command only after activation and voice activity detection.

Task 0.1 excludes audio capture, speech recognition implementations, Windows audio control, application launching, command groups, shutdown, network APIs, and installers.
