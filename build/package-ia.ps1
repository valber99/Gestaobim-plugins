<#
.SYNOPSIS
  Compila o GB Cotas e o GB Render para o Revit 2025, 2026 e 2027 e monta a pasta dos plugins de IA com o gateway local
  (CLIProxyAPI ORIGINAL, sem modificacao, de uma versao fixa conferida por SHA-256 do zip E do executavel).
.DESCRIPTION
  So monta a pasta dist\GBIa-<versao> (o instalador unico, package-gestaobim.ps1, junta tudo num exe so). Leva:
    <ano>\GBCotas\GBCotas.dll, <ano>\GBRender\GBRender.dll, GBCotas.addin, GBRender.addin,
    tools\cliproxyapi\cli-proxy-api.exe e o LICENSE (MIT) dele.
  O hash do executavel tem de ser o mesmo de ProxyPin.ExeSha256 (src/GBAi.Shared/GatewayHost.cs): o plugin recusa outro.
#>
param(
    [string]$Configuration = 'Release',
    [int[]]$Years = @(2025, 2026, 2027),
    [string]$ProxyVersion = '8.1.1',
    [string]$ProxyZipSha256 = 'f345c06313e487e474baf03fda1e3ec8a76c26bf2e3f57f3c7dcbd60857c1b2c',
    [string]$ProxyExeSha256 = 'aa4faf13862209c94b1556236ddb193fe3608614e0d19c5a7e6b6f07db07efff'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

# O pino do script e o do codigo tem de ser o mesmo (senao o plugin recusaria o exe que o instalador levou).
$pin = Get-Content (Join-Path $root 'src\GBAi.Shared\GatewayHost.cs') -Raw
foreach ($expected in "Version = `"$ProxyVersion`"", "ExeSha256 = `"$ProxyExeSha256`"", "ZipSha256 = `"$ProxyZipSha256`"") {
    if (-not $pin.Contains($expected)) { throw "ProxyPin em GatewayHost.cs nao confere com o script ($expected)." }
}

$versions = foreach ($p in 'GBCotas', 'GBRender') {
    [xml]$xml = Get-Content (Join-Path $root "src\$p\$p.csproj")
    [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if (@($versions | Sort-Object -Unique).Count -ne 1) { throw "GB Cotas e GB Render precisam da mesma versao: $($versions -join ', ')" }
$version = $versions[0]

# 1) O CLIProxyAPI original: baixa uma vez, confere o zip, extrai so o executavel e a licenca, confere o executavel.
$cache = Join-Path $PSScriptRoot 'cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$zipName = "CLIProxyAPI_${ProxyVersion}_windows_amd64.zip"
$proxyZip = Join-Path $cache $zipName
if (-not (Test-Path $proxyZip)) {
    $url = "https://github.com/router-for-me/CLIProxyAPI/releases/download/v$ProxyVersion/$zipName"
    Write-Host "Baixando o CLIProxyAPI $ProxyVersion..."
    Invoke-WebRequest -Uri $url -OutFile $proxyZip -UseBasicParsing
}
$actual = (Get-FileHash $proxyZip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $ProxyZipSha256.ToLower()) {
    Remove-Item $proxyZip -Force
    throw "Checksum do CLIProxyAPI nao confere (esperado $ProxyZipSha256, obtido $actual)."
}

$name = "GBIa-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$proxyStage = Join-Path $stage 'tools\cliproxyapi'
New-Item -ItemType Directory -Force $proxyStage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($proxyZip)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.Name -in 'cli-proxy-api.exe', 'LICENSE') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $proxyStage $entry.Name), $true)
        }
    }
} finally { $archive.Dispose() }
foreach ($f in 'cli-proxy-api.exe', 'LICENSE') {
    if (-not (Test-Path (Join-Path $proxyStage $f))) { throw "$f nao esta no zip do CLIProxyAPI." }
}
$exeHash = (Get-FileHash (Join-Path $proxyStage 'cli-proxy-api.exe') -Algorithm SHA256).Hash.ToLower()
if ($exeHash -ne $ProxyExeSha256.ToLower()) { throw "SHA-256 do cli-proxy-api.exe nao confere (esperado $ProxyExeSha256, obtido $exeHash)." }
Copy-Item (Join-Path $proxyStage 'LICENSE') (Join-Path $proxyStage 'LICENSE-CLIProxyAPI.txt')
Remove-Item (Join-Path $proxyStage 'LICENSE')
Write-Host "Gateway de IA: CLIProxyAPI $ProxyVersion (SHA-256 conferido)"

# 2) Os add-ins, uma DLL por versao do Revit (o nucleo, o gateway e a parte compartilhada vao compilados dentro).
foreach ($p in 'GBCotas', 'GBRender') {
    $project = Join-Path $root "src\$p\$p.csproj"
    foreach ($year in $Years) {
        Write-Host "Compilando o $p para o Revit $year..."
        dotnet build $project -c $Configuration "-p:RevitVersion=$year" --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o $p para o Revit $year" }
        $payload = Join-Path $stage "$year\$p"
        New-Item -ItemType Directory -Force $payload | Out-Null
        Copy-Item (Join-Path $root "src\$p\bin\$Configuration\$year\$p.dll") $payload
    }
    Copy-Item (Join-Path $root "installer\$p.addin") $stage
}
return $stage
