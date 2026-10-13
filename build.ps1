param([switch]$Portable)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & '.\native\hook-helper\build.cmd'
    if ($LASTEXITCODE -ne 0) { throw '输入组件构建失败。' }
    if ($Portable) {
        dotnet publish '.\src\ApexPing.csproj' -c Release -r win-x64 --self-contained true -o '.\release'
    }
    else {
        dotnet publish '.\src\ApexPing.csproj' -c Release -r win-x64 --self-contained false -o '.\release'
    }
    if ($LASTEXITCODE -ne 0) { throw '桌面应用构建失败。' }
    Write-Output '完成：双击 release\ApexPing.exe 启动。'
}
finally { Pop-Location }
