[CmdletBinding()]
param(
    [Uri]$WakeModelUri,
    [string]$WakeModelSha256,
    [Uri]$WhisperModelUri,
    [string]$WhisperModelSha256,
    [string]$OutputDirectory,
    [string]$SigningKeyPath,
    [switch]$VerifyConfigurationOnly,
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseConfigPath = Join-Path $repoRoot 'assets\voice-models\release-assets.json'
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)

    [IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Convert-HexToBytes {
    param([string]$Hex)

    Assert-Condition ($Hex.Length % 2 -eq 0 -and $Hex -cmatch '^[0-9A-Fa-f]+$') 'The hexadecimal input is invalid.'
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($Hex.Substring($index * 2, 2), 16)
    }
    return $bytes
}

function Assert-HttpsUri {
    param([Uri]$Uri, [string]$Name)

    Assert-Condition ($null -ne $Uri -and $Uri.IsAbsoluteUri) "$Name must be an absolute URI."
    Assert-Condition ($Uri.Scheme -ceq 'https') "$Name must use HTTPS."
    Assert-Condition ([string]::IsNullOrEmpty($Uri.UserInfo)) "$Name must not contain credentials."
}

function Assert-Sha256 {
    param([string]$Hash, [string]$Name)

    Assert-Condition ($Hash -cmatch '^[0-9a-fA-F]{64}$') "$Name must be a 64-character SHA-256 value."
}

function Assert-SafeFileName {
    param([string]$Value, [string]$Name)

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Value)) "$Name is required."
    Assert-Condition (-not [IO.Path]::IsPathRooted($Value)) "$Name must not be rooted."
    Assert-Condition ([IO.Path]::GetFileName($Value) -ceq $Value) "$Name must be a file name without directories."
    Assert-Condition ($Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -lt 0) "$Name contains invalid characters."
}

function Resolve-AssetTarget {
    param([string]$AssetRoot, [string]$FileName, [string]$Name)

    Assert-SafeFileName $FileName $Name
    $root = [IO.Path]::GetFullPath($AssetRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath((Join-Path $root $FileName))
    Assert-Condition ($target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) "$Name escapes the asset directory."
    return $target
}

function Assert-FileHash {
    param([string]$Path, [string]$ExpectedHash, [string]$Name)

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "$Name is missing."
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Assert-Condition ($actual -ceq $ExpectedHash.ToUpperInvariant()) "$Name SHA-256 mismatch."
}

function Assert-FileSize {
    param([string]$Path, [long]$MaximumBytes, [string]$Name)

    $length = (Get-Item -LiteralPath $Path).Length
    Assert-Condition ($length -gt 0 -and $length -le $MaximumBytes) "$Name has an invalid or oversized payload."
    return $length
}

function Invoke-BoundedDownload {
    param([Uri]$Uri, [string]$Destination, [long]$MaximumBytes, [string]$Name)

    Assert-Condition ($MaximumBytes -gt 0) "$Name download limit is invalid."
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $handler.UseCookies = $false
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('DeskPilot-Release/1.0')
    try {
        $response = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            $contentLength = $response.Content.Headers.ContentLength
            Assert-Condition ($null -eq $contentLength -or $contentLength -le $MaximumBytes) "$Name exceeds the approved download limit."
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = New-Object byte[] 81920
                [long]$total = 0
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    Assert-Condition ($read -le ($MaximumBytes - $total)) "$Name exceeds the approved download limit."
                    $output.Write($buffer, 0, $read)
                    $total += $read
                }

                Assert-Condition ($total -gt 0) "$Name download is empty."
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}

function Get-VoskArchiveInfo {
    param([string]$Path, [long]$MaximumInstalledBytes)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        Assert-Condition ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 10000) 'The Vosk archive entry count is invalid.'
        $normalized = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $modelFile = @($normalized | Where-Object { $_ -cmatch '(^|/)am/final\.mdl$' })
        Assert-Condition ($modelFile.Count -eq 1) 'The Vosk archive must contain exactly one model root.'
        $root = $modelFile[0].Substring(0, $modelFile[0].Length - 'am/final.mdl'.Length).TrimEnd('/')
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($root)) 'The Vosk model root is invalid.'
        $required = @(
            "$root/am/final.mdl",
            "$root/conf/mfcc.conf",
            "$root/conf/model.conf",
            "$root/graph/Gr.fst",
            "$root/graph/HCLr.fst",
            "$root/graph/phones/word_boundary.int"
        )
        foreach ($entry in $required) {
            Assert-Condition ($normalized -ccontains $entry) "The Vosk archive is missing $entry."
        }

        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            Assert-Condition (-not [IO.Path]::IsPathRooted($entry.FullName)) 'The Vosk archive contains an absolute path.'
            Assert-Condition (-not ($entry.FullName.Replace('\', '/') -cmatch '(^|/)\.\.(/|$)')) 'The Vosk archive contains parent traversal.'
            Assert-Condition ($entry.Length -le ($MaximumInstalledBytes - $expandedBytes)) 'The Vosk archive expands beyond the approved limit.'
            $expandedBytes += $entry.Length
            Assert-Condition ($expandedBytes -le $MaximumInstalledBytes) 'The Vosk archive expands beyond the approved limit.'
        }

        return [pscustomobject]@{
            Root = $root
            ExpandedBytes = $expandedBytes
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-WhisperHeader {
    param([string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $header = New-Object byte[] 4
        Assert-Condition ($stream.Read($header, 0, $header.Length) -eq 4) 'The Whisper model header is truncated.'
        Assert-Condition ([Text.Encoding]::ASCII.GetString($header) -ceq 'lmgg') 'The Whisper model header is invalid.'
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PemDer {
    param([string]$Pem, [string]$Label)

    $body = $Pem.Replace("-----BEGIN $Label-----", '').Replace("-----END $Label-----", '')
    $body = [Text.RegularExpressions.Regex]::Replace($body, '\s', '')
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($body)) "The $Label PEM payload is empty."
    return [Convert]::FromBase64String($body)
}

function Convert-ToPublicKeyPem {
    param([Security.Cryptography.CngKey]$Key)

    $blob = $Key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    Assert-Condition ($blob.Length -eq 72) 'The signing key must be ECDSA P-256.'
    $prefix = Convert-HexToBytes '3059301306072A8648CE3D020106082A8648CE3D03010703420004'
    [byte[]]$point = $blob[8..71]
    [byte[]]$der = $prefix + $point
    $base64 = [Convert]::ToBase64String($der)
    $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
        $base64.Substring($offset, [Math]::Min(64, $base64.Length - $offset))
    }
    return "-----BEGIN PUBLIC KEY-----`n$($lines -join "`n")`n-----END PUBLIC KEY-----`n"
}

function Write-CatalogSignature {
    param([string]$ManifestPath, [string]$PrivateKeyPath, [string]$SignaturePath, [string]$PublicKeyPath)

    Assert-Condition (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf) 'The signing key file is missing.'
    $privateDer = Get-PemDer (Get-Content -Raw -LiteralPath $PrivateKeyPath) 'PRIVATE KEY'
    $key = [Security.Cryptography.CngKey]::Import(
        $privateDer,
        [Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    try {
        Assert-Condition ($key.AlgorithmGroup -eq [Security.Cryptography.CngAlgorithmGroup]::Ecdsa) 'The signing key must be ECDSA.'
        $algorithm = New-Object -TypeName Security.Cryptography.ECDsaCng -ArgumentList $key
        try {
            Assert-Condition ($algorithm.KeySize -eq 256) 'The signing key must use P-256.'
            $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
            $signature = $algorithm.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256)
            [IO.File]::WriteAllBytes($SignaturePath, $signature)
            Write-Utf8NoBom $PublicKeyPath (Convert-ToPublicKeyPem $key)
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $key.Dispose()
        [Array]::Clear($privateDer, 0, $privateDer.Length)
    }
}

function Assert-CatalogSignature {
    param([string]$ManifestPath, [string]$SignaturePath, [string]$PublicKeyPath)

    $publicDer = Get-PemDer (Get-Content -Raw -LiteralPath $PublicKeyPath) 'PUBLIC KEY'
    Assert-Condition ($publicDer.Length -eq 91 -and $publicDer[26] -eq 4) 'The catalog public key must be P-256 SubjectPublicKeyInfo.'
    [byte[]]$publicBlob = [Text.Encoding]::ASCII.GetBytes('ECS1') + [BitConverter]::GetBytes([int]32) + $publicDer[27..90]
    $key = [Security.Cryptography.CngKey]::Import(
        $publicBlob,
        [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try {
        $algorithm = New-Object -TypeName Security.Cryptography.ECDsaCng -ArgumentList $key
        try {
            $valid = $algorithm.VerifyData(
                [IO.File]::ReadAllBytes($ManifestPath),
                [IO.File]::ReadAllBytes($SignaturePath),
                [Security.Cryptography.HashAlgorithmName]::SHA256)
            Assert-Condition $valid 'The catalog signature is invalid.'
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $key.Dispose()
    }
}

function Assert-RepositoryPolicy {
    $tracked = @(& git -C $repoRoot ls-files)
    Assert-Condition ($LASTEXITCODE -eq 0) 'Unable to inspect tracked Git files.'
    $forbidden = @($tracked | Where-Object {
        $_ -cmatch '(^|/)(models|runtime|logs|audio|publish|artifacts)/|\.(db|sqlite|wav|mp3|gguf|bin)$'
    })
    Assert-Condition ($forbidden.Count -eq 0) "Tracked runtime or model assets are forbidden: $($forbidden -join ', ')"
}

function Assert-ReleaseConfiguration {
    param($Configuration)

    Assert-Condition ($Configuration.schemaVersion -eq 1) 'The release asset configuration schema is unsupported.'
    Assert-HttpsUri ([Uri]$Configuration.wake.uri) 'wake.uri'
    Assert-HttpsUri ([Uri]$Configuration.whisperBase.uri) 'whisperBase.uri'
    Assert-HttpsUri ([Uri]$Configuration.whisperSmall.releaseUri) 'whisperSmall.releaseUri'
    Assert-HttpsUri ([Uri]$Configuration.wake.licenseUri) 'wake.licenseUri'
    Assert-HttpsUri ([Uri]$Configuration.whisperBase.licenseUri) 'whisperBase.licenseUri'
    Assert-Sha256 ([string]$Configuration.wake.sha256) 'wake.sha256'
    Assert-Sha256 ([string]$Configuration.whisperBase.sha256) 'whisperBase.sha256'
    Assert-Sha256 ([string]$Configuration.whisperSmall.sha256) 'whisperSmall.sha256'
    Assert-Sha256 ([string]$Configuration.wake.licenseSha256) 'wake.licenseSha256'
    Assert-Sha256 ([string]$Configuration.whisperBase.licenseSha256) 'whisperBase.licenseSha256'
    Assert-SafeFileName ([string]$Configuration.wake.fileName) 'wake.fileName'
    Assert-SafeFileName ([string]$Configuration.whisperBase.fileName) 'whisperBase.fileName'
    Assert-Condition ($Configuration.wake.maximumDownloadBytes -gt 0) 'The Vosk download limit is invalid.'
    Assert-Condition ($Configuration.whisperBase.maximumDownloadBytes -gt 0) 'The Whisper download limit is invalid.'
    Assert-Condition ($Configuration.whisperSmall.downloadBytes -gt 0) 'The optional Whisper size is invalid.'
    Assert-Condition ($Configuration.wake.maximumLicenseBytes -gt 0) 'The Vosk license limit is invalid.'
    Assert-Condition ($Configuration.whisperBase.maximumLicenseBytes -gt 0) 'The Whisper license limit is invalid.'
}

function Assert-ReleaseBundle {
    param(
        [string]$PublishOutput,
        $Configuration,
        [string]$ExpectedWakeHash,
        [string]$ExpectedWhisperHash)

    if ([string]::IsNullOrWhiteSpace($ExpectedWakeHash)) {
        $ExpectedWakeHash = [string]$Configuration.wake.sha256
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedWhisperHash)) {
        $ExpectedWhisperHash = [string]$Configuration.whisperBase.sha256
    }
    Assert-Sha256 $ExpectedWakeHash 'ExpectedWakeHash'
    Assert-Sha256 $ExpectedWhisperHash 'ExpectedWhisperHash'

    $assetRoot = Join-Path ([IO.Path]::GetFullPath($PublishOutput)) 'assets\voice-models'
    Assert-Condition (Test-Path -LiteralPath $assetRoot -PathType Container) 'The release voice model directory is missing.'
    $wakePath = Resolve-AssetTarget $assetRoot ([string]$Configuration.wake.fileName) 'wake.fileName'
    $whisperPath = Resolve-AssetTarget $assetRoot ([string]$Configuration.whisperBase.fileName) 'whisperBase.fileName'
    Assert-FileHash $wakePath $ExpectedWakeHash 'Vosk seed model'
    Assert-FileHash $whisperPath $ExpectedWhisperHash 'Whisper seed model'
    [void](Assert-FileSize $wakePath ([long]$Configuration.wake.maximumDownloadBytes) 'Vosk seed model')
    [void](Assert-FileSize $whisperPath ([long]$Configuration.whisperBase.maximumDownloadBytes) 'Whisper seed model')
    $voskInfo = Get-VoskArchiveInfo $wakePath ([long]$Configuration.wake.maximumInstalledBytes)
    Assert-WhisperHeader $whisperPath

    $seedManifestPath = Join-Path $assetRoot 'seed-manifest.json'
    Assert-Condition (Test-Path -LiteralPath $seedManifestPath -PathType Leaf) 'The seed manifest is missing.'
    $seedManifest = Get-Content -Raw -LiteralPath $seedManifestPath | ConvertFrom-Json
    Assert-Condition ($seedManifest.schemaVersion -eq 1 -and $seedManifest.models.Count -eq 2) 'The seed manifest schema is invalid.'
    $wakeManifest = @($seedManifest.models | Where-Object { $_.provider -ceq 'WakeVosk' })
    $whisperManifest = @($seedManifest.models | Where-Object { $_.provider -ceq 'CommandWhisper' })
    Assert-Condition ($wakeManifest.Count -eq 1 -and $whisperManifest.Count -eq 1) 'The seed manifest provider set is invalid.'
    Assert-Condition ($wakeManifest[0].sha256 -ceq $ExpectedWakeHash.ToUpperInvariant()) 'The Vosk seed manifest hash is invalid.'
    Assert-Condition ($whisperManifest[0].sha256 -ceq $ExpectedWhisperHash.ToUpperInvariant()) 'The Whisper seed manifest hash is invalid.'
    Assert-Condition ($wakeManifest[0].asset -ceq [string]$Configuration.wake.fileName) 'The Vosk seed manifest asset is invalid.'
    Assert-Condition ($whisperManifest[0].asset -ceq [string]$Configuration.whisperBase.fileName) 'The Whisper seed manifest asset is invalid.'
    Assert-Condition ($wakeManifest[0].entryPoint -ceq $voskInfo.Root) 'The Vosk seed manifest entry point is invalid.'
    Assert-Condition ($whisperManifest[0].entryPoint -ceq 'ggml-base.bin') 'The Whisper seed manifest entry point is invalid.'
    $voskLicensePath = Resolve-AssetTarget $assetRoot 'LICENSE-vosk-Apache-2.0.txt' 'Vosk license file'
    $whisperLicensePath = Resolve-AssetTarget $assetRoot 'LICENSE-whisper-MIT.txt' 'Whisper license file'
    Assert-FileHash $voskLicensePath ([string]$Configuration.wake.licenseSha256) 'Vosk license file'
    Assert-FileHash $whisperLicensePath ([string]$Configuration.whisperBase.licenseSha256) 'Whisper license file'
    [void](Assert-FileSize $voskLicensePath ([long]$Configuration.wake.maximumLicenseBytes) 'Vosk license file')
    [void](Assert-FileSize $whisperLicensePath ([long]$Configuration.whisperBase.maximumLicenseBytes) 'Whisper license file')

    $catalogPath = Join-Path $assetRoot 'models.manifest.json'
    $signaturePath = Join-Path $assetRoot 'models.manifest.sig'
    $seedSignaturePath = Join-Path $assetRoot 'seed-manifest.sig'
    $publicKeyPath = Join-Path $assetRoot 'catalog-public-key.pem'
    foreach ($signedPart in @($catalogPath, $signaturePath, $seedSignaturePath, $publicKeyPath)) {
        Assert-Condition (Test-Path -LiteralPath $signedPart -PathType Leaf) 'The signed release model catalog is incomplete.'
    }
    $catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
    Assert-Condition ($catalog.schemaVersion -eq 1 -and $catalog.models.Count -ge 1) 'The signed model catalog schema is invalid.'
    $optionalModel = @($catalog.models | Where-Object { $_.id -ceq 'whisper-small-multi' })
    Assert-Condition ($optionalModel.Count -eq 1) 'The optional Whisper model descriptor is missing.'
    Assert-Condition ($optionalModel[0].sha256 -ceq ([string]$Configuration.whisperSmall.sha256).ToUpperInvariant()) 'The optional Whisper model hash is invalid.'
    Assert-HttpsUri ([Uri]$optionalModel[0].downloadUri) 'optional Whisper downloadUri'
    Assert-CatalogSignature $seedManifestPath $seedSignaturePath $publicKeyPath
    Assert-CatalogSignature $catalogPath $signaturePath $publicKeyPath
}

Assert-Condition (Test-Path -LiteralPath $releaseConfigPath -PathType Leaf) 'The release asset configuration is missing.'
$configuration = Get-Content -Raw -LiteralPath $releaseConfigPath | ConvertFrom-Json
Assert-ReleaseConfiguration $configuration
Assert-RepositoryPolicy

Assert-Condition (-not ($VerifyConfigurationOnly -and $VerifyOnly)) 'Choose only one verification mode.'
if ($VerifyConfigurationOnly) {
    Assert-Condition (-not $PSBoundParameters.ContainsKey('OutputDirectory')) 'OutputDirectory is not used with VerifyConfigurationOnly.'
    Write-Host 'Voice model release configuration and repository policy verification passed.'
    exit 0
}

if ($VerifyOnly) {
    Assert-Condition ($PSBoundParameters.ContainsKey('OutputDirectory')) 'OutputDirectory is required when verifying a release bundle.'
    Assert-ReleaseBundle $OutputDirectory $configuration $WakeModelSha256 $WhisperModelSha256
    Write-Host 'Voice model release bundle verification passed.'
    exit 0
}

Assert-Condition ($PSBoundParameters.ContainsKey('OutputDirectory')) 'OutputDirectory is required when preparing release assets.'
Assert-Condition (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) 'SigningKeyPath is required when preparing release assets.'
if ($null -eq $WakeModelUri) { $WakeModelUri = [Uri]$configuration.wake.uri }
if ([string]::IsNullOrWhiteSpace($WakeModelSha256)) { $WakeModelSha256 = [string]$configuration.wake.sha256 }
if ($null -eq $WhisperModelUri) { $WhisperModelUri = [Uri]$configuration.whisperBase.uri }
if ([string]::IsNullOrWhiteSpace($WhisperModelSha256)) { $WhisperModelSha256 = [string]$configuration.whisperBase.sha256 }
Assert-HttpsUri $WakeModelUri 'WakeModelUri'
Assert-HttpsUri $WhisperModelUri 'WhisperModelUri'
Assert-Sha256 $WakeModelSha256 'WakeModelSha256'
Assert-Sha256 $WhisperModelSha256 'WhisperModelSha256'

$publishOutput = [IO.Path]::GetFullPath($OutputDirectory)
$assetRoot = Join-Path $publishOutput 'assets\voice-models'
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "deskpilot-voice-assets-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $wakeTarget = Resolve-AssetTarget $assetRoot ([string]$configuration.wake.fileName) 'wake.fileName'
    $whisperTarget = Resolve-AssetTarget $assetRoot ([string]$configuration.whisperBase.fileName) 'whisperBase.fileName'
    $voskLicenseTarget = Resolve-AssetTarget $assetRoot 'LICENSE-vosk-Apache-2.0.txt' 'Vosk license file'
    $whisperLicenseTarget = Resolve-AssetTarget $assetRoot 'LICENSE-whisper-MIT.txt' 'Whisper license file'
    $wakeTargetIsCurrent = (Test-Path -LiteralPath $wakeTarget -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $wakeTarget -Algorithm SHA256).Hash -ceq $WakeModelSha256.ToUpperInvariant())
    $whisperTargetIsCurrent = (Test-Path -LiteralPath $whisperTarget -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $whisperTarget -Algorithm SHA256).Hash -ceq $WhisperModelSha256.ToUpperInvariant())
    $voskLicenseIsCurrent = (Test-Path -LiteralPath $voskLicenseTarget -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $voskLicenseTarget -Algorithm SHA256).Hash -ceq ([string]$configuration.wake.licenseSha256).ToUpperInvariant())
    $whisperLicenseIsCurrent = (Test-Path -LiteralPath $whisperLicenseTarget -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $whisperLicenseTarget -Algorithm SHA256).Hash -ceq ([string]$configuration.whisperBase.licenseSha256).ToUpperInvariant())
    $wakeDownload = if ($wakeTargetIsCurrent) { $wakeTarget } else { Join-Path $temporaryRoot 'wake.zip' }
    $whisperDownload = if ($whisperTargetIsCurrent) { $whisperTarget } else { Join-Path $temporaryRoot 'ggml-base.bin' }
    $voskLicenseDownload = if ($voskLicenseIsCurrent) { $voskLicenseTarget } else { Join-Path $temporaryRoot 'LICENSE-vosk.txt' }
    $whisperLicenseDownload = if ($whisperLicenseIsCurrent) { $whisperLicenseTarget } else { Join-Path $temporaryRoot 'LICENSE-whisper.txt' }
    if (-not $wakeTargetIsCurrent) {
        Invoke-BoundedDownload $WakeModelUri $wakeDownload ([long]$configuration.wake.maximumDownloadBytes) 'Vosk seed model'
    }
    if (-not $whisperTargetIsCurrent) {
        Invoke-BoundedDownload $WhisperModelUri $whisperDownload ([long]$configuration.whisperBase.maximumDownloadBytes) 'Whisper seed model'
    }
    if (-not $voskLicenseIsCurrent) {
        Invoke-BoundedDownload ([Uri]$configuration.wake.licenseUri) $voskLicenseDownload ([long]$configuration.wake.maximumLicenseBytes) 'Vosk license file'
    }
    if (-not $whisperLicenseIsCurrent) {
        Invoke-BoundedDownload ([Uri]$configuration.whisperBase.licenseUri) $whisperLicenseDownload ([long]$configuration.whisperBase.maximumLicenseBytes) 'Whisper license file'
    }
    Assert-FileHash $wakeDownload $WakeModelSha256 'Vosk seed model'
    Assert-FileHash $whisperDownload $WhisperModelSha256 'Whisper seed model'
    Assert-FileHash $voskLicenseDownload ([string]$configuration.wake.licenseSha256) 'Vosk license file'
    Assert-FileHash $whisperLicenseDownload ([string]$configuration.whisperBase.licenseSha256) 'Whisper license file'
    $wakeBytes = Assert-FileSize $wakeDownload ([long]$configuration.wake.maximumDownloadBytes) 'Vosk seed model'
    $whisperBytes = Assert-FileSize $whisperDownload ([long]$configuration.whisperBase.maximumDownloadBytes) 'Whisper seed model'
    $voskInfo = Get-VoskArchiveInfo $wakeDownload ([long]$configuration.wake.maximumInstalledBytes)
    Assert-WhisperHeader $whisperDownload

    if (-not $wakeTargetIsCurrent) {
        Copy-Item -LiteralPath $wakeDownload -Destination $wakeTarget -Force
    }
    if (-not $whisperTargetIsCurrent) {
        Copy-Item -LiteralPath $whisperDownload -Destination $whisperTarget -Force
    }
    if (-not $voskLicenseIsCurrent) {
        Copy-Item -LiteralPath $voskLicenseDownload -Destination $voskLicenseTarget -Force
    }
    if (-not $whisperLicenseIsCurrent) {
        Copy-Item -LiteralPath $whisperLicenseDownload -Destination $whisperLicenseTarget -Force
    }

    $seedManifest = [ordered]@{
        schemaVersion = 1
        models = @(
            [ordered]@{
                id = 'wake-ru'
                provider = 'WakeVosk'
                displayName = 'Vosk small Russian 0.22'
                version = '0.22'
                qualityTier = 'Seed'
                downloadUri = $null
                asset = [string]$configuration.wake.fileName
                sha256 = $WakeModelSha256.ToUpperInvariant()
                downloadBytes = $wakeBytes
                installedBytes = $voskInfo.ExpandedBytes
                entryPoint = $voskInfo.Root
                licenseId = 'Apache-2.0'
                archiveFormat = 'Zip'
                minimumProviderVersion = '0.3.38'
                maximumProviderVersion = '0.3.99'
            },
            [ordered]@{
                id = 'whisper-base-multi'
                provider = 'CommandWhisper'
                displayName = 'Whisper base multilingual'
                version = 'openai-base'
                qualityTier = 'Base'
                downloadUri = $null
                asset = [string]$configuration.whisperBase.fileName
                sha256 = $WhisperModelSha256.ToUpperInvariant()
                downloadBytes = $whisperBytes
                installedBytes = $whisperBytes
                entryPoint = 'ggml-base.bin'
                licenseId = 'MIT'
                archiveFormat = 'None'
                minimumProviderVersion = '1.9.1'
                maximumProviderVersion = '1.9.99'
            }
        )
    }
    Write-Utf8NoBom (Join-Path $assetRoot 'seed-manifest.json') ($seedManifest | ConvertTo-Json -Depth 8)
    $catalog = [ordered]@{
        schemaVersion = 1
        models = @(
            [ordered]@{
                id = 'whisper-small-multi'
                provider = 'CommandWhisper'
                displayName = 'Whisper small multilingual'
                version = 'openai-small'
                qualityTier = 'HigherAccuracy'
                downloadUri = [string]$configuration.whisperSmall.releaseUri
                asset = $null
                sha256 = ([string]$configuration.whisperSmall.sha256).ToUpperInvariant()
                downloadBytes = [long]$configuration.whisperSmall.downloadBytes
                installedBytes = [long]$configuration.whisperSmall.installedBytes
                entryPoint = 'ggml-small.bin'
                licenseId = 'MIT'
                archiveFormat = 'None'
                minimumProviderVersion = '1.9.1'
                maximumProviderVersion = '1.9.99'
            }
        )
    }
    $resolvedSigningKeyPath = [IO.Path]::GetFullPath($SigningKeyPath)
    $publicKeyPath = Join-Path $assetRoot 'catalog-public-key.pem'
    $seedManifestPath = Join-Path $assetRoot 'seed-manifest.json'
    Write-CatalogSignature $seedManifestPath $resolvedSigningKeyPath (Join-Path $assetRoot 'seed-manifest.sig') $publicKeyPath
    $catalogPath = Join-Path $assetRoot 'models.manifest.json'
    Write-Utf8NoBom $catalogPath ($catalog | ConvertTo-Json -Depth 8)
    Write-CatalogSignature $catalogPath $resolvedSigningKeyPath (Join-Path $assetRoot 'models.manifest.sig') $publicKeyPath

    Assert-ReleaseBundle $publishOutput $configuration $WakeModelSha256 $WhisperModelSha256
    Write-Host "Verified voice model assets prepared under $assetRoot"
}
finally {
    $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($systemTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTemporaryRoot -match 'deskpilot-voice-assets-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
