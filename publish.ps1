# Rebuild dist\Snatch.exe from this folder's source so folder and exe never diverge.
# Builds into a temp dir first: a failed build leaves the running app and dist untouched.
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'

$dist = Join-Path $PSScriptRoot 'dist'
$exe  = Join-Path $dist 'Snatch.exe'
$tmp  = Join-Path ([IO.Path]::GetTempPath()) 'snatch_publish'

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'Snatch.csproj') -c Release -o $tmp
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE) - dist unchanged" }

# stop only the instance running from dist (not other worktree builds)
Get-Process Snatch -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit(5000) | Out-Null }

New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $exe) { Copy-Item $exe (Join-Path $dist 'Snatch.old.exe') -Force }
Copy-Item (Join-Path $tmp 'Snatch.exe') $exe -Force
Copy-Item (Join-Path $tmp 'Snatch.pdb') (Join-Path $dist 'Snatch.pdb') -Force -ErrorAction SilentlyContinue

Write-Host "dist\Snatch.exe updated: $((Get-Item $exe).LastWriteTime)"
if (-not $NoStart) { Start-Process $exe }
