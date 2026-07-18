# Voice model release inputs

This directory tracks release metadata only. Model binaries, archives, generated signatures, and private keys must never be committed.

`scripts/voice-model-assets.ps1` downloads the pinned seed assets only during an explicit release command and injects the verified bundle into `assets/voice-models` under the selected publish output.

Every release bundle must be signed with an external PKCS#8 ECDSA P-256 private key in PEM format. The script signs both the exact seed manifest bytes and the optional-model catalog, then writes only signatures and the public key into the publish bundle. The private key must remain in the protected release environment.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\voice-model-assets.ps1 -OutputDirectory .\artifacts\publish -SigningKeyPath D:\secure\deskpilot-models-p256.pem
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\voice-model-assets.ps1 -OutputDirectory .\artifacts\publish -VerifyOnly
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\voice-model-assets.ps1 -VerifyConfigurationOnly
```

Publish `models.manifest.json`, `models.manifest.sig`, and the optional model payload under the `voice-models-v1` GitHub release. The application contains the matching public key and fails closed if the signed seed or remote catalog is incomplete.
