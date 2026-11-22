<#
.SYNOPSIS
  Instala a biblioteca de familias do GB Familias (banco + arquivos .rfa) e aponta o plugin para ela.
.PARAMETER Destino
  Onde copiar. Padrao: C:\ProgramData\GestaoBIM\Biblioteca (qualquer usuario do PC usa a mesma copia).
#>
param(
    [string]$Destino = (Join-Path $env:ProgramData 'GestaoBIM\Biblioteca')
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$db = Join-Path $here 'gbfam.sqlite'
$familias = Join-Path $here 'familias'
if (-not (Test-Path $db) -or -not (Test-Path $familias)) { throw 'Este pacote esta incompleto: faltam gbfam.sqlite e/ou a pasta familias.' }

$needBytes = (Get-ChildItem $familias -Recurse -File | Measure-Object Length -Sum).Sum + (Get-Item $db).Length
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($Destino).TrimEnd('\', ':'))
if ($drive.Free -lt $needBytes * 1.05) {
    throw ("Pouco espaco em {0}: precisa de {1:N1} GB livres." -f $drive.Name, ($needBytes * 1.05 / 1GB))
}

New-Item -ItemType Directory -Force $Destino | Out-Null
Write-Host ("Copiando a biblioteca para $Destino ({0:N1} GB)..." -f ($needBytes / 1GB))
# robocopy reaproveita o que ja existe (instalar de novo e rapido) e nao apaga o que o usuario acrescentou.
robocopy $familias (Join-Path $Destino 'familias') /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "A copia das familias falhou (robocopy codigo $LASTEXITCODE)." }
Copy-Item $db (Join-Path $Destino 'gbfam.sqlite') -Force
Get-ChildItem $Destino -Recurse -File | Unblock-File

# Aponta o plugin para esta copia, preservando as outras configuracoes do usuario.
$settingsDir = Join-Path $env:APPDATA 'GestaoBIM\Familias'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$settingsFile = Join-Path $settingsDir 'settings.json'
$settings = [ordered]@{ library_db = ''; library_root = ''; gbfam_exe = ''; semantic_enabled = $true }
if (Test-Path $settingsFile) {
    try {
        $existing = Get-Content $settingsFile -Raw | ConvertFrom-Json
        foreach ($p in $existing.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    } catch { Write-Warning 'A configuracao anterior estava ilegivel; foi substituida (a antiga ficou em settings.json.antigo).'; Copy-Item $settingsFile "$settingsFile.antigo" -Force }
}
$settings['library_db'] = Join-Path $Destino 'gbfam.sqlite'
$settings['library_root'] = Join-Path $Destino 'familias'
[IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Biblioteca instalada em $Destino"
Write-Host 'Abra o Revit e use o painel Familias, na aba Gestao BIM.'
