# Voice Pipeline

The future pipeline is:

```text
Microphone → wake-word provider → альфа → audio capture → voice activity detection → speech-to-text provider → intent resolver → command dispatcher → registered module
```

The wake-word provider uses a limited grammar. Speech-to-text begins only after activation. Default voice activity settings are 250 ms minimum speech, 900 ms silence timeout, and 10 seconds maximum command duration.

No audio, speech model, native runtime, or provider implementation is included in Task 0.1.
