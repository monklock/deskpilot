# Changelog

## Unreleased

- Established the DeskPilot solution foundation and extension contracts.
- Added the finite module-owned Russian audio command catalog with handler validation.
- Added deterministic normalization, strict `0..100` digit/cardinal parsing, exact matching, and bounded fuzzy fallback.
- Added voice commands for saved speakers/headphones, fixed `+10/-10` volume changes, explicit volume, mute, unmute, and mute toggle.
- Connected local speech recognition to trusted command dispatch with resolving/executing states and safe success/failure feedback.
- Added stable error codes and safe WPF command outcomes without exposing arguments, endpoint IDs, native paths, audio, or raw handler messages.
- Added unit and integration coverage for resolver boundaries, handler behavior, pipeline recovery, dependency injection, and WPF projection.
