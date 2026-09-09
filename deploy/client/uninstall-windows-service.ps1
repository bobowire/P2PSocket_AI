# M1-30 客户端 Windows 服务卸载脚本（与 install-windows-service.ps1 配对；状态/配置不随卸载删除）。
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行'
}

$serviceName = 'P2PClient'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    Write-Host "服务 $serviceName 不存在"
    return
}

sc.exe stop $serviceName | Out-Null
Start-Sleep -Seconds 2
sc.exe delete $serviceName
if ($LASTEXITCODE -ne 0) { throw 'sc.exe delete 失败' }
Write-Host "服务 $serviceName 已卸载（%ProgramData%\P2PClient 状态目录保留，如需彻底清除请手动删除）"
