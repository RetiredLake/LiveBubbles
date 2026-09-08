param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -version '[16.0,17.0)' -latest -property installationPath
$compiler = Join-Path $visualStudio 'MSBuild\Current\Bin\Roslyn\csc.exe'
$outputDirectory = Join-Path $projectRoot 'work\notification-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$executable = Join-Path $outputDirectory 'NotificationTests.exe'
& $compiler /nologo /langversion:7.3 /target:exe "/out:$executable" (Join-Path $PSScriptRoot 'NotificationTests.cs') (Join-Path $projectRoot 'src\LiveBubbles.Notifications\NotificationPolicy.cs') (Join-Path $projectRoot 'src\LiveBubbles.Notifications\WebSocketWire.cs')
if ($LASTEXITCODE -ne 0) { throw 'Notification test compilation failed.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Notification tests failed.' }
