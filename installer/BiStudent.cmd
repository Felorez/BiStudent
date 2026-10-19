@echo off
chcp 65001 >nul
title BiStudent
cd /d "%~dp0"

tasklist /fi "imagename eq BiStudent.Server.exe" | find /i "BiStudent.Server.exe" >nul
if errorlevel 1 (
    rem На пустой базе создаются демо-данные: вход teacher / teacher123.
    set Demo__Enabled=true
    start "BiStudent — сервер (не закрывайте это окно)" /min /d "%~dp0Server" "%~dp0Server\BiStudent.Server.exe" --urls http://0.0.0.0:5080
)

echo Запускаю BiStudent, подождите...
for /l %%i in (1,1,60) do (
    curl.exe -s -f -m 2 http://localhost:5080/health >nul 2>&1 && goto ready
    timeout /t 1 /nobreak >nul
)
echo.
echo Сервер не ответил за минуту. Откройте свёрнутое окно «BiStudent — сервер» и пришлите текст ошибки.
pause
exit /b 1

:ready
start "" /d "%~dp0Client" "%~dp0Client\BiStudent.exe"
