$version = "local"
$publishDir = Join-Path $env:TEMP "canyeaye-publish-$([guid]::NewGuid().ToString('N'))"
$distDir = Join-Path (Get-Location) "artifacts"
$zipPath = Join-Path $distDir "canyeaye-win-x64-$version.zip"

dotnet publish .\src\CanYeAye.App\CanYeAye.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  "-p:Version=$version" `
  --output $publishDir

if ($LASTEXITCODE -ne 0) { throw "Publish failed; package was not created." }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host "Package created: $zipPath"