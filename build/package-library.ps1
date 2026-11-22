<#
.SYNOPSIS
  Gera o pacote da biblioteca de familias do GB Familias: o banco SQLite e so as familias unicas (sem duplicatas),
  com um instalador que copia tudo para C:\ProgramData\GestaoBIM\Biblioteca e aponta o plugin para la.
.NOTES
  Usa 'gbfam export-library'. O .zip passa de 1 GB, entao e criado pelo .NET (suporta zip64); o .exe
  (build/SetupStub.cs, o .zip colado no fim) e opcional (-Exe).
#>
param(
    [string]$Version = '0.1.0',
    [string]$GbfamExe = (Join-Path (Split-Path $PSScriptRoot) 'tools\gbfam\target\release\gbfam.exe'),
    [string]$Db = (Join-Path (Split-Path $PSScriptRoot) 'banco_familias\gbfam.sqlite'),
    [string]$FamiliesRoot = (Join-Path (Split-Path $PSScriptRoot) 'familias'),
    # Gera tambem o instalador .exe (do mesmo tamanho do .zip). Padrao: so o .zip.
    [switch]$Exe
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path $PSScriptRoot
foreach ($p in $GbfamExe, $Db, $FamiliesRoot) { if (-not (Test-Path $p)) { throw "Nao encontrado: $p" } }

$name = "GBFamilias-Biblioteca-$Version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

& $GbfamExe --db $Db export-library --root $FamiliesRoot --out $stage
if ($LASTEXITCODE -ne 0) { throw 'gbfam export-library falhou.' }

foreach ($file in 'Install.ps1', 'Uninstall.ps1', 'Instalar.bat', 'Desinstalar.bat') {
    Copy-Item (Join-Path $root "installer\Biblioteca\$file") $stage
}
$readme = @"
GB Familias - Biblioteca $Version
Banco e arquivos .rfa da biblioteca de familias do GB Familias (Gestao BIM)

INSTALAR
  1. Instale antes o plugin GB Familias (GBFamilias-<versao>.zip, Instalar.bat).
  2. De dois cliques em Instalar.bat. Ele copia a biblioteca para C:\ProgramData\GestaoBIM\Biblioteca
     (use -Destino para outra pasta) e configura o plugin para usa-la.
  3. Abra o Revit: painel "Familias", aba "Gestao BIM".

BIBLIOTECA NA REDE
  Para varias pessoas usarem a mesma copia, coloque as pastas gbfam.sqlite e familias num servidor e aponte
  os dois caminhos em Familias > Configuracoes. O banco e aberto somente para leitura.

OBSERVACAO
  As familias vem de varias fontes (arquivos do proprio escritorio e de fabricantes). Confira os direitos de
  redistribuicao antes de compartilhar este pacote fora da sua organizacao.
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host 'Compactando (pode levar alguns minutos)...'
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n")
Write-Host ("Pacote: $zip ({0:N2} GB)" -f ((Get-Item $zip).Length / 1GB))
Write-Host "SHA256: $hash"

if ($Exe) {
    New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GBFamilias-Biblioteca-Setup-$Version.exe") -Slug 'gb-familias-biblioteca'
}
