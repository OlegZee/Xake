@powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ((New-Object System.Net.WebClient).DownloadFile('https://github.com/OlegZee/Xake/releases/download/v0.8.3/Xake.Core.dll', '.\packages\Xake.Core.dll'))"
echo done!