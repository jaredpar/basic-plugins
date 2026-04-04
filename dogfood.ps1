$pipelineRoot = $PSScriptRoot
$dogfoodPath = Join-Path $pipelineRoot "artifacts\dogfood"
$pluginsPath = Join-Path $dogfoodPath "plugins"
$packagePath = Join-Path $dogfoodPath "package"
$workingPath = Join-Path $dogfoodPath "working"

if (Test-Path $dogfoodPath) { Remove-Item $dogfoodPath -Recurse -Force }
New-Item -ItemType Directory -Path $dogfoodPath -Force | Out-Null
New-Item -ItemType Directory -Path $workingPath -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsPath -Force | Out-Null
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null

dotnet pack (Join-Path $pipelineRoot "Pipeline.slnx") -c Release --nologo -o $packagePath
New-Item -ItemType Directory -Path $pluginsPath -Force | Out-Null
Copy-Item -Path (Join-Path $pipelineRoot "plugins\*") -Destination $pluginsPath -Recurse -Force

# Override the nuget.org source with the local package output for dogfooding
Get-ChildItem -Path $pluginsPath -Filter "plugin.json" -Recurse | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $escapedPackagePath = $packagePath.Replace("\", "\\")
    $content = $content.Replace("https://api.nuget.org/v3/index.json", $escapedPackagePath)
    Set-Content $_.FullName $content
}

$pluginNames = Get-ChildItem -Path $pluginsPath -Directory | Select-Object -ExpandProperty Name

Push-Location $workingPath
try {
    foreach ($pluginName in $pluginNames) {
        Write-Host "Installing plugin: $pluginName"
        & copilot --plugin-dir $workingPath plugin install (Join-Path $pluginsPath $pluginName)
    }
    & copilot --plugin-dir $workingPath @args
}
finally {
    foreach ($pluginName in $pluginNames) {
        & copilot --plugin-dir $workingPath plugin uninstall $pluginName
    }
    Pop-Location
}
