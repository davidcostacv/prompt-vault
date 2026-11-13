# Compila PromptVault con el compilador que ya trae Windows (.NET Framework 4.8). No descarga nada.
#   .\build.ps1             -> tests + dist\PromptVault.exe
#   .\build.ps1 -TestsOnly  -> solo tests
#   .\build.ps1 -Install    -> además instala en %LOCALAPPDATA%\Programs\PromptVault y la abre
param([switch]$TestsOnly, [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'
$wpf  = Join-Path $fw 'WPF'
$obj  = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null
if (-not (Test-Path $csc)) { throw "No encontré csc.exe en $fw (.NET Framework 4.8 no está instalado)" }

$refs = @(
  "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Xaml.dll",
  "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Web.Extensions.dll",
  "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll"
) | ForEach-Object { if (-not (Test-Path $_)) { throw "Falta referencia: $_" }; "/reference:$_" }
$common = @('/nologo', '/codepage:65001', '/warn:4', '/warnaserror+', '/optimize+', '/platform:anycpu') + $refs

# 1) Tests de la capa de datos
Write-Host '== Tests ==' -ForegroundColor Cyan
$testExe = Join-Path $obj 'Tests.exe'
& $csc @common /target:exe "/out:$testExe" "$root\src\Store.cs" "$root\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Los tests no compilan' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Fallaron tests: no se genera el .exe' }
if ($TestsOnly) { return }

# 2) Icono (.ico generado desde el mismo dibujo que usa la app)
Write-Host '== Icono ==' -ForegroundColor Cyan
$ico = Join-Path $obj 'PromptVault.ico'
if (-not ('PromptVault.IconArt' -as [type])) {
  Add-Type -TypeDefinition (Get-Content -Raw "$root\src\IconArt.cs") -ReferencedAssemblies System.Drawing
}
[PromptVault.IconArt]::WriteIco($ico)

# 3) App
Write-Host '== PromptVault.exe ==' -ForegroundColor Cyan
$exe = Join-Path $dist 'PromptVault.exe'
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$res = Get-ChildItem "$root\src\*.xaml" | ForEach-Object { "/resource:$($_.FullName),PromptVault.$($_.Name)" }
& $csc @common /target:winexe "/out:$exe" "/win32icon:$ico" "/win32manifest:$root\src\app.manifest" @res @src
if ($LASTEXITCODE -ne 0) { throw 'La app no compila' }
Write-Host ("OK -> $exe ({0:N0} KB)" -f ((Get-Item $exe).Length / 1KB)) -ForegroundColor Green

if ($Install) {
  $dest = Join-Path $env:LOCALAPPDATA 'Programs\PromptVault'
  $installed = Join-Path $dest 'PromptVault.exe'
  if (Test-Path $installed) {
    & $installed --exit   # cierre ordenado: guarda antes de salir
    for ($i = 0; $i -lt 50 -and (Get-Process PromptVault -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
  }
  New-Item -ItemType Directory -Force $dest | Out-Null
  Copy-Item $exe $installed -Force
  Start-Process $installed
  Write-Host "Instalada en $installed" -ForegroundColor Green
}
