# M1-30 客户端 Windows 服务安装脚本（08 §3.1/§4.2；打包安装器属 M4，此处为管理员手工/预发布口径）。
# 用法：管理员 PowerShell 在发布目录（含 p2p-client.exe、Wintun.dll）执行 .\install-windows-service.ps1
# 服务以 LocalSystem 运行（需加载 Wintun 驱动）；状态/日志在 %ProgramData%\P2PClient（03 §5）。
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行（需要 sc.exe 与驱动权限）'
}

$serviceName = 'P2PClient'
$exe = Join-Path $PSScriptRoot 'p2p-client.exe'
if (-not (Test-Path $exe)) { throw "未找到 $exe（请在发布目录内执行）" }

if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "服务 $serviceName 已存在，跳过创建（如需重装先执行 uninstall-windows-service.ps1）"
} else {
    sc.exe create $serviceName binPath= "`"$exe`"" start= auto DisplayName= 'P2P Client'
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe create 失败' }
}

# 崩溃自愈（FR-C-902，01 §4.2）：失败即重启（5s×3 档），24h 无失败重置计数
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
if ($LASTEXITCODE -ne 0) { throw 'sc.exe failure 失败' }

sc.exe start $serviceName
Write-Host "服务 $serviceName 已安装并启动（本地管理页 http://127.0.0.1:7100）"
