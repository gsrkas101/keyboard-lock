# Builds KeyboardLock.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Runs the unit tests first; nothing is built if a test fails.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$src = Join-Path $root 'src\KeyboardLock.cs'
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$common = @('/nologo', '/codepage:65001', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll')

& $csc @common /target:exe /main:KeyboardLock.Tests "/out:$out\Tests.exe" $src (Join-Path $root 'src\Tests.cs')
if ($LASTEXITCODE) { throw 'Test build failed' }
& "$out\Tests.exe"
if ($LASTEXITCODE) { throw 'Tests failed' }

$ico = Join-Path $root 'src\app.ico'
if (-not (Test-Path $ico)) {
    & $csc @common /target:exe "/out:$out\IconTool.exe" $src
    if ($LASTEXITCODE) { throw 'Icon tool build failed' }
    & "$out\IconTool.exe" --export-icon $ico
}

& $csc @common /target:winexe /optimize+ "/out:$root\KeyboardLock.exe" "/win32icon:$ico" "/win32manifest:$root\src\app.manifest" $src
if ($LASTEXITCODE) { throw 'Build failed' }
Write-Host "Built $root\KeyboardLock.exe"
