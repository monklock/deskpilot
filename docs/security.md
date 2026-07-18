# Security Design

Only registered command handlers can execute a command. Unresolved or ambiguous input must not cause an operation. Local runtime data is stored per user and excluded from source control.

The repository must not contain credentials, user databases, models, audio recordings, logs, generated runtime binaries, or personal absolute paths.

Voice audio remains in bounded process memory, is cleared when pooled buffers are returned, and is never persisted, transmitted, or logged. Rejected wake attempts and ambient text are not logged. Provider failures are mapped to typed safe messages before they reach WPF.

Release model metadata pins HTTPS sources, SHA-256 values, maximum sizes, provider formats, and licenses. Output file names are restricted to safe basenames and downloads stop when their byte limit is exceeded. Vosk archives reject traversal paths, links, excessive entries, and excessive expanded size. Whisper files must have a valid ggml header. Installation is immutable and model activation is transactional with last-known-good recovery.

Seed and remote model manifests use ECDSA P-256/SHA-256 signatures. Only the public key is included in the application; the PKCS#8 private signing key must exist only in the protected release environment. An unsigned or incomplete publish bundle fails release verification and startup seeding. Redirects are restricted to configured HTTPS origins and no background or automatic optional-model download is allowed.

Release verification scans tracked paths and file extensions to prevent models, runtime data, publish output, databases, recordings, and credentials from entering the public repository.
