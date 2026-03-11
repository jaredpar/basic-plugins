$pipelineRoot = $PSScriptRoot
Push-Location $pipelineRoot
try {
  $pluginPath = Join-Path $pipelineRoot "artifacts\plugin"
  $dogfoodPath = Join-Path $pipelineRoot "artifacts\dogfood"
  if (Test-Path $pluginPath) {
      Remove-Item -Path "$pluginPath\*" -Recurse -Force
  }

  New-Item -ItemType Directory -Path $pluginPath -Force | Out-Null
  New-Item -ItemType Directory -Path $dogfoodPath -Force | Out-Null

  dotnet publish .\src\Pipeline.Mcp\Pipeline.Mcp.csproj -c Debug -o (Join-Path $pluginPath "mcp") --nologo
  Copy-Item -Path (Join-Path $pipelineRoot "plugin\mcp\*") -Destination $pluginPath -Recurse -Force

  # Use an isolated directory so the copilot session doesn't try to use the tools in the
  # source directory
  Set-Location $dogfoodPath
  copilot --plugin-dir $pluginPath
  copilot @args
}
finally {
    Pop-Location
}