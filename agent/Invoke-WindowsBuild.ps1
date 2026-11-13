param(
  [Parameter(Mandatory = $true)][string]$ProjectPath,
  [string]$ResultPath
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
Set-Location -LiteralPath $ProjectPath
& .\BuildAndCopy.bat 2>&1
$code = $LASTEXITCODE
function Get-Sha256([string]$Path) {
  $stream = [System.IO.File]::OpenRead($Path)
  try {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $algorithm.Dispose() }
  } finally { $stream.Dispose() }
}
$targets = @(
  'src\bin\Server-Debug\net9.0\GameEntry.dll',
  'src\bin\Client-Debug\net9.0\GameEntry.dll',
  'AppBundle\managed\GameEntry.dll',
  'ui\AppBundle\managed\GameEntry.dll'
)
$artifacts = @($targets | ForEach-Object {
  $path = Join-Path $ProjectPath $_
  $exists = Test-Path -LiteralPath $path -PathType Leaf
  [ordered]@{ path = $_; exists = $exists; sha256 = $(if ($exists) { Get-Sha256 $path } else { $null }) }
})
$result = [ordered]@{ success = ($code -eq 0); exitCode = $code; project = $ProjectPath; artifacts = $artifacts; finishedAt = [DateTime]::Now.ToString('o') }
if ($ResultPath) { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
$result | ConvertTo-Json -Depth 6 -Compress
exit $code
