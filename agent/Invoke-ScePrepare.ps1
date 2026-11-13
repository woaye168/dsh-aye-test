param(
  [Parameter(Mandatory = $true)][string]$ProjectPath,
  [Parameter(Mandatory = $true)][string]$SceCommand,
  [switch]$Offline
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
if (-not (Test-Path -LiteralPath $SceCommand -PathType Leaf)) { throw "SCE command not found: $SceCommand" }
$arguments = @('prepare', $ProjectPath)
if ($Offline) { $arguments += '--offline' }
$lines = @(& $SceCommand @arguments)
$lines | ForEach-Object { Write-Output $_ }
$code = $LASTEXITCODE
$result = $null
foreach ($line in $lines) {
  try {
    $value = $line | ConvertFrom-Json
    if ($value.event -eq 'initialization_result') { $result = $value }
  } catch { }
}
if ($code -ne 0 -or $null -eq $result -or -not $result.success -or -not $result.ready) {
  throw "SCE preparation did not reach success+ready; exit=$code"
}
