param([switch]$FrameworkDependent, [string]$OutputDirectory = 'artifacts/win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    rtk dotnet build Bridge.Tests -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Core test build failed.' }
    rtk dotnet Bridge.Tests/bin/Release/net8.0/Bridge.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    rtk dotnet build Bridge.UiTests -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    rtk dotnet Bridge.UiTests/bin/Release/net8.0-windows/Bridge.UiTests.dll artifacts/ui-preview.png
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
    if ($FrameworkDependent) {
        rtk dotnet publish Bridge.App -c Release -r win-x64 --self-contained false -o $OutputDirectory -m:1
    } else {
        rtk dotnet publish Bridge.App -c Release -r win-x64 --self-contained true -o $OutputDirectory --source https://api.nuget.org/v3/index.json -m:1
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $OutputDirectory 'README.md')
    $bridgeHash = [Security.Cryptography.SHA256]::Create()
    $bridgeStream = [IO.File]::OpenRead((Join-Path (Resolve-Path $OutputDirectory).Path 'WarudoBridge.exe'))
    try {
        $digest = [BitConverter]::ToString($bridgeHash.ComputeHash($bridgeStream)).Replace('-', '').ToLowerInvariant()
        Write-Output "WarudoBridge.exe SHA256: $digest"
    } finally { $bridgeStream.Dispose(); $bridgeHash.Dispose() }
} finally { Pop-Location }
