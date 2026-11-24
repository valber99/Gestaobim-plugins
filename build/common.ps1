# Funcoes compartilhadas pelos scripts de empacotamento (package.ps1 e package-dwg.ps1).

function New-ZipWithChecksum {
    param([string]$Stage, [string]$Zip)
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$Zip.sha256", "$hash  $(Split-Path $Zip -Leaf)`n")
    Write-Host "Pacote: $Zip"
    Write-Host "SHA256: $hash"
}

<#
.SYNOPSIS
  Instalador .exe para pacotes grandes (o IExpress trava com ~0,7 GB): compila build/SetupStub.cs com o csc do .NET
  Framework do Windows e cola o .zip no fim, com o SHA-256 e o tamanho num rodape. O .exe confere o SHA-256, recusa
  instalar com o Revit aberto, extrai e roda o Install.ps1 (com /quiet nao pausa no fim).
#>
function New-LargeSetupExe {
    param([string]$Zip, [string]$Exe, [string]$Slug)   # Slug: nome do log, %TEMP%\<Slug>-setup.log
    if ($Slug -notmatch '^[a-z0-9-]{1,32}$') { throw "Slug invalido: $Slug" }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "csc.exe do .NET Framework nao encontrado: $csc" }
    $stub = Join-Path (Split-Path $Exe) 'setup-stub.exe'
    $ico = Join-Path $PSScriptRoot 'gestaobim.ico'
    $logo = Join-Path $PSScriptRoot 'gestaobim-logo.png'
    $cscArgs = @(
        '/nologo', '/optimize+', '/codepage:65001', '/target:winexe', "/out:$stub",
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll'
    )
    if (Test-Path $ico) { $cscArgs += "/win32icon:$ico" }
    if (Test-Path $logo) { $cscArgs += "/resource:$logo,gestaobim-logo.png" }
    $cscArgs += (Join-Path $PSScriptRoot 'SetupStub.cs')
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador (SetupStub.cs).' }
    if (Test-Path $Exe) { Remove-Item $Exe -Force }
    $zipHash = [byte[]]::new(32)
    $sha = [Security.Cryptography.SHA256]::Create()
    $in = [IO.File]::OpenRead($Zip)
    try { $zipHash = $sha.ComputeHash($in); $zipLength = $in.Length } finally { $in.Dispose(); $sha.Dispose() }
    $out = [IO.File]::Create($Exe)
    try {
        $s = [IO.File]::OpenRead($stub); try { $s.CopyTo($out) } finally { $s.Dispose() }
        $z = [IO.File]::OpenRead($Zip); try { $z.CopyTo($out, 1MB) } finally { $z.Dispose() }
        $slugBytes = [byte[]]::new(32); $ascii = [Text.Encoding]::ASCII.GetBytes($Slug)
        [Array]::Copy($ascii, $slugBytes, $ascii.Length); $out.Write($slugBytes, 0, 32)
        $out.Write($zipHash, 0, 32)
        $len = [BitConverter]::GetBytes([long]$zipLength); $out.Write($len, 0, 8)
        $magic = [Text.Encoding]::ASCII.GetBytes('GBSETUP2'); $out.Write($magic, 0, 8)
    } finally { $out.Dispose() }
    Remove-Item $stub -Force
    $hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$Exe.sha256", "$hash  $(Split-Path $Exe -Leaf)`n")
    Write-Host "Instalador: $Exe"
    Write-Host "SHA256 exe: $hash"
}

# Descompacta um .gz (o .NET traz o GZipStream; o Expand-Archive do Windows PowerShell nao le .gz).
function Expand-GzipFile {
    param([string]$Path, [string]$Destination)
    $in = [IO.File]::OpenRead($Path)
    try {
        $gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
        $out = [IO.File]::Create($Destination)
        try { $gz.CopyTo($out) } finally { $out.Dispose(); $gz.Dispose() }
    } finally { $in.Dispose() }
}
