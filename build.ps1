$ErrorActionPreference = "Stop"

$required = @(
  "Assembly-CSharp.dll",
  "UnityEngine.dll",
  "UnityEngine.CoreModule.dll",
  "UnityEngine.IMGUIModule.dll"
)

foreach ($dll in $required) {
  if (-not (Test-Path (Join-Path "lib" $dll))) {
    throw "Missing lib/$dll"
  }
}

dotnet build .\src\AdaptivePulseRCS\AdaptivePulseRCS.csproj -c Release

Write-Host ""
Write-Host "Built:"
Write-Host "GameData\AdaptivePulseRCS\Plugins\AdaptivePulseRCS.dll"
