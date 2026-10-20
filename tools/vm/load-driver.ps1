param(
    [string]$Root = "C:\wfbench\wfprobe"
)

# 只做「构建 → 签名 → 安装 → 启动」，给调试与证据采集用；完整门禁见 build-and-test.ps1。

$ErrorActionPreference = "Stop"
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$driverProject = Join-Path $Root "src\driver\wfprobe.vcxproj"
$driverBinary = Join-Path $Root "src\driver\x64\Debug\wfprobe.sys"

& $msb $driverProject -restore -p:Configuration=Debug -p:Platform=x64 -v:m 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) { throw "驱动构建失败" }

$thumbprint = (Get-ChildItem Cert:\LocalMachine\My |
               Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
if (-not $thumbprint) { throw "找不到测试证书" }
& $signtool sign /fd SHA256 /sha1 $thumbprint /v $driverBinary | Out-Null
if ($LASTEXITCODE -ne 0) { throw "签名失败" }
"签名状态：$((Get-AuthenticodeSignature $driverBinary).Status)"

for ($installAttempt = 0; $installAttempt -lt 3; $installAttempt++) {
    sc.exe stop wfprobe 2>$null | Out-Null
    sc.exe delete wfprobe 2>$null | Out-Null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $text = sc.exe query wfprobe 2>$null | Out-String
        if ($text -match "STOPPED" -or $text -match "1060") { break }
        Start-Sleep -Milliseconds 250
    }
    Copy-Item $driverBinary "C:\Windows\System32\drivers\wfprobe.sys" -Force
    if (-not (Test-Path "C:\Windows\System32\drivers\wfprobe.sys")) { throw "驱动文件复制失败" }
    sc.exe create wfprobe type= kernel start= demand binPath= C:\Windows\System32\drivers\wfprobe.sys 2>&1 | Out-Null
    $startOutput = sc.exe start wfprobe 2>&1 | Out-String
    Start-Sleep -Seconds 1
    $query = sc.exe query wfprobe 2>&1 | Out-String
    if ($query -match "RUNNING") {
        "DRIVER-STATUS=RUNNING"
        break
    }
    "安装尝试 $($installAttempt + 1) 失败：$($startOutput.Trim()) / $($query.Trim())"
    sc.exe delete wfprobe 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
}

if ($query -notmatch "RUNNING") { throw "驱动没有进入 RUNNING：$($query.Trim())" }
"DRIVER-STATUS=RUNNING"

