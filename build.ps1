param([switch]$Portable)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & '.\native\hook-helper\build.cmd'
    if ($LASTEXITCODE -ne 0) { throw '输入组件构建失败。' }
    $output = Join-Path $PSScriptRoot 'release\ApexPing-win-x64'
    $selfContained = if ($Portable) { 'true' } else { 'false' }
    dotnet publish '.\src\ApexPing.csproj' -c Release -r win-x64 --self-contained $selfContained -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$selfContained -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=zh-Hans
    if ($LASTEXITCODE -ne 0) { throw '桌面应用构建失败。' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md')
    Write-Output "完成：双击 $output\ApexPing.exe 启动。"
}
finally { Pop-Location }
