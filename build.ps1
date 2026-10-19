# Builds GlobalMK.exe into .\dist and runs the self-tests.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet build "$root\src\GlobalMK\GlobalMK.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "App build failed" }

dotnet build "$root\tests\GlobalMK.Tests\GlobalMK.Tests.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Test build failed" }

& "$root\tests\GlobalMK.Tests\bin\Release\net48\GlobalMK.Tests.exe"
if ($LASTEXITCODE -ne 0) { throw "Self-tests failed" }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Copy-Item "$root\src\GlobalMK\bin\Release\net48\GlobalMK.exe" "$root\dist\GlobalMK.exe" -Force
$exe = Get-Item "$root\dist\GlobalMK.exe"
Write-Host ("Built {0} ({1:N0} KB)" -f $exe.FullName, ($exe.Length / 1KB))
