# Security Design

Only registered command handlers can execute a command. Unresolved or ambiguous input must not cause an operation. Local runtime data is stored per user and excluded from source control.

The repository must not contain credentials, user databases, models, audio recordings, logs, generated runtime binaries, or personal absolute paths.
