param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$package = Join-Path $Destination 'innosetup-7.1.1-x64.exe'
$compiler = Join-Path $Destination 'compiler'
$null = New-Item -ItemType Directory -Force -Path $Destination
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $package
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0') {
    throw 'Inno Setup SHA-256 mismatch.'
}
$arguments = '/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS="" /DIR="{0}"' -f $compiler
$process = Start-Process -FilePath $package -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
$iscc = Join-Path $compiler 'ISCC.exe'
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup could not be installed (exit $($process.ExitCode))."
}
$iscc
