<# Build an experimental package; does not install the plugin or modify any media. #>
param([ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = "0.1.11.0")
$ErrorActionPreference = "Stop"
$taskRoot = $PSScriptRoot
$taskProject = Join-Path $taskRoot "Jellyfin.Plugin.PreTranscode/Jellyfin.Plugin.PreTranscode.csproj"
dotnet build $taskProject -c Release -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$taskArtifacts = Join-Path $taskRoot "artifacts"
$taskStage = Join-Path $taskArtifacts ("stage-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot "Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.Compressor.dll") -Destination $taskStage
Copy-Item -LiteralPath (Join-Path $taskRoot "LICENSE"), (Join-Path $taskRoot "UPSTREAM.md") -Destination $taskStage
$taskTools = Join-Path $taskStage "tools"
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskCache = Join-Path $taskArtifacts "tool-cache"
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
function Get-PinnedTool($name, $url, $hash) {
    $path = Join-Path $taskCache $name
    if (-not (Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) {
        Remove-Item -LiteralPath $path -Force
        throw "SHA-256 mismatch for $name"
    }
    return $path
}
$taskMkv = Get-PinnedTool "MKVToolNix_GUI-102.0-x86_64.AppImage" `
    "https://mkvtoolnix.download/appimage/MKVToolNix_GUI-102.0-x86_64.AppImage" `
    "C66345B30D6D5FD640EA982AB5E202A99B9F541A20E42AA19EDD90F3DDD5DC9B"
Copy-Item -LiteralPath $taskMkv -Destination (Join-Path $taskTools "mkvtoolnix.AppImage")
$taskHdrArchive = Get-PinnedTool "hdr10plus_tool-1.7.2-x86_64-unknown-linux-musl.tar.gz" `
    "https://github.com/quietvoid/hdr10plus_tool/releases/download/1.7.2/hdr10plus_tool-1.7.2-x86_64-unknown-linux-musl.tar.gz" `
    "06385F37A639D61BA21D4BE3150C863846933BC3B58110E094D8FC8F1C2249F2"
tar -xzf $taskHdrArchive -C $taskTools ./hdr10plus_tool
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $taskTools "hdr10plus_tool") -Algorithm SHA256).Hash -ne
    "7845916B549C36E5D7FE9DBB3D24C124466D7C71EC3442E207551B677949D0BE") { throw "Invalid hdr10plus_tool binary" }
Copy-Item -LiteralPath (Join-Path $taskRoot "THIRD_PARTY_NOTICES.md") -Destination $taskStage
$taskLicenses = Join-Path $taskStage "licenses"
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot "licenses/hdr10plus_tool-MIT.txt"), (Join-Path $taskRoot "licenses/MKVToolNix-GPL-2.0.txt") -Destination $taskLicenses
$taskMeta = [ordered]@{
    category = "General"
    changelog = "Experimental manual HDR10+ preservation for MKV on Linux x86-64, disabled by default. Automatic compression stays off by default."
    description = "Compress movies while retaining originals temporarily and preserving Jellyfin item identity."
    guid = "274af2b7-724c-41e9-82e7-56c3e80139c1"
    name = "Jellyfin Compressor"
    overview = "Movie compression with recoverable originals."
    owner = "slx612"
    targetAbi = "10.11.6.0"
    timestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    version = $Version
    status = "Active"
    autoUpdate = $false
}
$taskMeta | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskStage "meta.json") -Encoding utf8
$taskZip = Join-Path $taskArtifacts "jellyfin-compressor-$Version.zip"
Compress-Archive -Path (Join-Path $taskStage "*") -DestinationPath $taskZip -Force
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$taskHash  jellyfin-compressor-$Version.zip" | Set-Content -LiteralPath "$taskZip.sha256" -Encoding ascii
Write-Host "Package: $taskZip"
Write-Host "SHA256: $taskHash"
