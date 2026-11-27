param(
    [string]$CompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('iotlink-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $stage | Out-Null
Push-Location $PSScriptRoot
try {
    if (!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw "Inno Setup compiler missing at $CompilerPath. Supply -CompilerPath to select it."
    }
    foreach ($project in 'IOTLink/IOTLinkService.csproj', 'IOTLinkAgent/IOTLinkAgent.csproj') {
        dotnet publish $project -c Release -r win-x64 --self-contained true -o "$stage/app"
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
    foreach ($addon in 'Commands', 'ProcessMonitor', 'WindowsMonitor') {
        dotnet build "Addons/$addon/Addon_$addon.csproj" -c Release -o "$stage/addons/$addon"
        if ($LASTEXITCODE -ne 0) { throw "Building $addon failed." }
    }
    & $CompilerPath "/DStageDir=$stage" 'IOTLink_Installer.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $stage -Recurse -Force
}
