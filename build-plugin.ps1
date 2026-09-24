<# Build an experimental package; does not install the plugin or modify any media. #>
param([ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = "0.1.5.0")
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
$taskMeta = [ordered]@{
    category = "General"
    changelog = "Experimental preview: stop oversized encodes early, clarify progress and keep the original when minimum savings cannot be met. Automatic compression stays off by default."
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
