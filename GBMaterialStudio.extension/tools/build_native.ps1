param(
    [string]$Source = (Join-Path $PSScriptRoot 'GBCursorHook.cs'),
    [string]$Output = (Join-Path $PSScriptRoot '..\lib\GBCursorHook.dll')
)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe nao encontrado em $csc" }

& $csc /nologo /target:library /optimize+ /platform:anycpu "/out:$([System.IO.Path]::GetFullPath($Output))" $Source
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar (codigo $LASTEXITCODE)" }
Get-Item $Output | Select-Object Name, Length
