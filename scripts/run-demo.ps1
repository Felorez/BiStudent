<#
.SYNOPSIS
  Запускает демо BiStudent: сервер API на http://localhost:5080 и клиент преподавателя (WinForms).
.PARAMETER Reset
  Удалить локальную базу сервера перед запуском (демо-данные создадутся заново).
#>
param([switch]$Reset)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'BiStudent.Server'
$client = Join-Path $root 'BiStudent'
$url = 'http://localhost:5080'

if (-not (dotnet --list-sdks | Select-String '^10\.')) {
    Write-Error 'Нужен .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}

if ($Reset) {
    $data = Join-Path $server 'App_Data'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data; Write-Host 'База сброшена.' }
}

Write-Host 'Сборка решения…'
dotnet build (Join-Path $root 'BiStudent.slnx') -c Release -v q | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Запуск сервера на $url…"
$serverProcess = Start-Process dotnet -ArgumentList @('run', '--project', $server, '-c', 'Release', '--no-build', '--urls', $url) `
    -WorkingDirectory $server -WindowStyle Minimized -PassThru

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) {
    Stop-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
    Write-Error 'Сервер не ответил за 60 секунд.'
}

Write-Host 'Сервер готов. Открываю клиент преподавателя (демо: teacher / teacher123).'
$exe = Get-ChildItem (Join-Path $client 'bin\Release') -Recurse -Filter BiStudent.exe | Select-Object -First 1
$clientProcess = Start-Process $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
$clientProcess.WaitForExit()

Write-Host 'Клиент закрыт, останавливаю сервер.'
Stop-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
Get-Process BiStudent.Server -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
