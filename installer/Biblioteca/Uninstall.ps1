<#
.SYNOPSIS
  Remove a biblioteca de familias instalada pelo pacote da biblioteca. Favoritos e colecoes do usuario nao sao apagados.
#>
param(
    [string]$Destino = (Join-Path $env:ProgramData 'GestaoBIM\Biblioteca')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Destino)) { Write-Host "Nada a remover em $Destino."; return }

$answer = Read-Host "Remover a biblioteca em $Destino ? (s/N)"
if ($answer -notmatch '^[sSyY]') { Write-Host 'Cancelado.'; return }
try {
    Remove-Item $Destino -Recurse -Force
    Write-Host 'Biblioteca removida.'
} catch {
    Write-Warning 'Nao consegui remover tudo (arquivos em uso?). Feche o Revit e tente de novo.'
}
Write-Host 'Seus favoritos e colecoes continuam em %APPDATA%\GestaoBIM\Familias.'
