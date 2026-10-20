param(
    [string]$Root = "C:\wfbench\wfprobe",
    [string]$Remote = "1.2.3.4:443",
    [int]$ListenerPort = 18080,
    [string]$LoopbackBind = "127.0.0.1",
    [string]$LocalBind = "192.168.100.2",
    [int]$Iterations = 200
)

$ErrorActionPreference = "Stop"

$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$accesschk = "C:\SysInternals\accesschk64.exe"
$driverProject = Join-Path $Root "src\driver\wfprobe.vcxproj"
$driverBinary = Join-Path $Root "src\driver\x64\Debug\wfprobe.sys"
$toolProject = Join-Path $Root "tools\wfprobe\wfprobe.csproj"
$toolBinary = Join-Path $Root "tools\wfprobe\bin\Release\net11.0\wfprobe.exe"

try {
    "===== 构建驱动 ====="
    & $msb $driverProject -restore -p:Configuration=Debug -p:Platform=x64 -v:m
    if ($LASTEXITCODE -ne 0) { throw "驱动构建失败" }

    "===== 签名 ====="
    $thumbprint = (Get-ChildItem Cert:\LocalMachine\My |
                   Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
    if (-not $thumbprint) { throw "找不到测试证书" }
    & $signtool sign /fd SHA256 /sha1 $thumbprint /v $driverBinary
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
    $signature = Get-AuthenticodeSignature $driverBinary
    "签名状态：$($signature.Status)"
    if ($signature.Status -ne "Valid") { throw "签名状态不是 Valid" }

    "===== 安装并启动驱动 ====="
    sc.exe stop wfprobe 2>$null | Out-Null
    sc.exe delete wfprobe 2>$null | Out-Null
    Copy-Item $driverBinary "C:\Windows\System32\drivers\wfprobe.sys" -Force
    sc.exe create wfprobe type= kernel start= demand binPath= C:\Windows\System32\drivers\wfprobe.sys | Out-Null
    sc.exe start wfprobe | Out-Null
    Start-Sleep -Seconds 1
    $state = (sc.exe query wfprobe | Select-String "STATE").Line.Trim()
    "服务状态：$state"
    if ($state -notmatch "RUNNING") { throw "驱动未进入 RUNNING" }

    "===== 设备 DACL ====="
    if (-not (Test-Path $accesschk)) { throw "找不到 accesschk64.exe，无法验证设备 DACL" }
    $dacl = (& $accesschk -accepteula -q -o '\Device\WfProbe' | Out-String)
    $dacl
    if ($dacl -notmatch 'SYSTEM') { throw "DACL 断言失败：缺少 NT AUTHORITY\SYSTEM" }
    if ($dacl -notmatch 'Administrators') { throw "DACL 断言失败：缺少 BUILTIN\Administrators" }
    if ($dacl -match 'Everyone') { throw "DACL 断言失败：出现 Everyone" }

    "===== 构建测试程序 ====="
    & dotnet build $toolProject -c Release -v:q
    if ($LASTEXITCODE -ne 0) { throw "测试程序构建失败" }

    "===== 分支 1：ANY → loopback ====="
    & $toolBinary selftest --remote $Remote --bind $LoopbackBind --port $ListenerPort --iterations 2 --expect-address $LoopbackBind --expect-branch loopback
    if ($LASTEXITCODE -ne 0) { throw "ANY→loopback 分支失败" }
    & $toolBinary events --count 2

    "===== 分支 2：指定 local 地址 ====="
    & $toolBinary selftest --remote $Remote --bind $LocalBind --port $ListenerPort --iterations 2 --source $LocalBind --expect-address $LocalBind --expect-branch local
    if ($LASTEXITCODE -ne 0) { throw "指定 local 地址分支失败" }
    & $toolBinary events --count 2

    "===== 所有权：$Iterations 次连接的池用量 ====="
    & $toolBinary selftest --remote $Remote --bind $LoopbackBind --port $ListenerPort --iterations $Iterations --expect-address $LoopbackBind --expect-branch loopback --settle-ms 3000
    if ($LASTEXITCODE -ne 0) { throw "$Iterations 次连接验证失败" }
    & $toolBinary pooltag

    "===== 跨进程验证（listener 与 client 分属两个进程） ====="
    & (Join-Path $Root "tools\vm\cross-process.ps1") -Root $Root -Remote $Remote -ListenerPort $ListenerPort -Bind $LoopbackBind
    if ($LASTEXITCODE -ne 0) { throw "跨进程验证失败" }

    "===== 目标无人监听的失败路径 ====="
    & (Join-Path $Root "tools\vm\edge-cases.ps1") -Root $Root -Remote $Remote -DeadPort ($ListenerPort + 1)
    if ($LASTEXITCODE -ne 0) { throw "无人监听失败路径验证失败" }

    "===== 全部通过 ====="
    $script:result = 0
}
catch {
    "失败：$($_.Exception.Message)"
    $script:result = 1
}
finally {
    sc.exe stop wfprobe 2>$null | Out-Null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $text = sc.exe query wfprobe 2>$null | Out-String
        if ($text -match "STOPPED" -or $text -match "1060") { break }
        Start-Sleep -Milliseconds 250
    }
    sc.exe delete wfprobe 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    Remove-Item "C:\Windows\System32\drivers\wfprobe.sys" -Force -ErrorAction SilentlyContinue
}

exit $script:result
