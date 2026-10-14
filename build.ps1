param(
    [switch]$Portable,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & '.\native\hook-helper\build.cmd'
    if ($LASTEXITCODE -ne 0) { throw '输入组件构建失败。' }
    $output = Join-Path $PSScriptRoot 'release\ApexPing-win-x64'
    $selfContained = if ($Portable) { 'true' } else { 'false' }
    $versionOption = if ($Version) { @("-p:Version=$Version") } else { @() }
    dotnet publish '.\src\ApexPing.csproj' -c Release -r win-x64 --self-contained $selfContained -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$selfContained -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=zh-Hans @versionOption
    if ($LASTEXITCODE -ne 0) { throw '桌面应用构建失败。' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md')
    Write-Output "完成：双击 $output\ApexPing.exe 启动。"
}
finally { Pop-Location }
