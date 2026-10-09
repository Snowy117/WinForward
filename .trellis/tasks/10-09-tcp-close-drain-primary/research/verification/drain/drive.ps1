param(
  [Parameter(Mandatory=$true)][string]$Verb,
  [string]$A1 = "",
  [string]$A2 = "",
  [string]$A3 = ""
)

$ErrorActionPreference = "Stop"
$root = "C:/wf-ac0"
$runDir = "$root/data"
if (-not (Test-Path $runDir)) { New-Item -ItemType Directory -Path $runDir | Out-Null }

function Read-PidNum([string]$file) {
  if (Test-Path $file) {
    $t = (Get-Content $file -First 1)
    if ($t -match "^[0-9]+$") { return [int]$t }
  }
  return 0
}

try {
  switch ($Verb) {
    "fw" {
      $flag = "False"
      if ($A1 -eq "on") { $flag = "True" }
      Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled $flag
      Write-Output ((Get-NetFirewallProfile | Select-Object Name,Enabled | Format-Table -AutoSize | Out-String).Trim())
    }
    "wd" {
      if ($A1 -eq "off") { Disable-ScheduledTask -TaskName wfbench-watchdog | Out-Null } else { Enable-ScheduledTask -TaskName wfbench-watchdog | Out-Null }
      Write-Output ("watchdog=" + (Get-ScheduledTask -TaskName wfbench-watchdog).State)
    }
    "sb" {
      if ($A1 -eq "start") {
        $p = Start-Process -FilePath "$root/singbox/sing-box.exe" -ArgumentList @("run","-c","C:/wf-ac0/singbox/config.json") -WorkingDirectory "$root/singbox" -RedirectStandardOutput "$root/logs/singbox.out" -RedirectStandardError "$root/logs/singbox.err" -PassThru -WindowStyle Hidden
        $p.Id | Set-Content "$runDir/singbox.pid"
        Start-Sleep -Seconds 3
        Write-Output ("singbox pid " + $p.Id)
      } else {
        $sbPid = Read-PidNum "$runDir/singbox.pid"
        if ($sbPid -gt 0) { Stop-Process -Id $sbPid -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Write-Output ("stopped singbox " + $sbPid) } else { Write-Output "no singbox pid" }
        Remove-Item "$runDir/singbox.pid" -ErrorAction SilentlyContinue
      }
    }
    "wf" {
      if ($A1 -eq "start") {
        $label = $A2
        $p = Start-Process -FilePath "$root/wf-fdd/WinForward.exe" -ArgumentList @("run","--config","C:/wf-ac0/wf-fdd/appsettings-debug.json") -WorkingDirectory "$root/wf-fdd" -RedirectStandardOutput "$root/logs/wf-$label.out" -RedirectStandardError "$root/logs/wf-$label.err" -PassThru -WindowStyle Hidden
        $p.Id | Set-Content "$runDir/wf.pid"
        Start-Sleep -Seconds 4
        $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        Write-Output ("wf pid " + $p.Id + " alive=" + [bool]$alive)
      } else {
        $wfPid = Read-PidNum "$runDir/wf.pid"
        if ($wfPid -gt 0) { Stop-Process -Id $wfPid -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3; Write-Output ("stopped wf " + $wfPid) } else { Write-Output "no wf pid" }
        Remove-Item "$runDir/wf.pid" -ErrorAction SilentlyContinue
      }
    }
    "variant" {
      Copy-Item "$root/variants/$A1/WinForward.exe" "$root/wf-fdd/WinForward.exe" -Force
      Write-Output ("wf-fdd exe sha256 " + (Get-FileHash "$root/wf-fdd/WinForward.exe" -Algorithm SHA256).Hash.ToLower())
    }
    "client" {
      $label = $A1
      $planFile = "$root/plans/$A2"
      $outDir = "$root/$A3"
      Write-Output ("client start " + (Get-Date).ToUniversalTime().ToString("o"))
      & "$root/e2e/WinForward.E2E.exe" client --target 192.168.100.4 --plan $planFile --out $outDir --label $label --tcp-port 40020 --udp-port 40020 --dns-port 40053
      Write-Output ("client end " + (Get-Date).ToUniversalTime().ToString("o"))
      Write-Output ("CLIENT_EXIT=" + $LASTEXITCODE)
    }
    "hashes" {
      foreach ($f in @("$root/e2e/WinForward.E2E.exe","$root/e2e/WinForward.E2E.dll","$root/wf-fdd/WinForward.exe","$root/wf-fdd/ndisapi.dll","$root/variants/fin/WinForward.exe","$root/variants/nofin/WinForward.exe")) {
        if (Test-Path $f) { Write-Output ("sha256 " + (Get-FileHash $f -Algorithm SHA256).Hash.ToLower() + " " + $f + " " + (Get-Item $f).Length) }
      }
    }
    "clock" {
      Write-Output ("vm_utc " + (Get-Date).ToUniversalTime().ToString("o"))
    }
    "status" {
      Write-Output "--- firewall ---"
      Write-Output ((Get-NetFirewallProfile | Select-Object Name,Enabled | Format-Table -AutoSize | Out-String).Trim())
      Write-Output "--- watchdog ---"
      Write-Output (Get-ScheduledTask -TaskName wfbench-watchdog).State
      Write-Output "--- pids ---"
      Write-Output ("wf=" + (Read-PidNum "$runDir/wf.pid") + " sb=" + (Read-PidNum "$runDir/singbox.pid"))
      Write-Output "--- procs ---"
      Write-Output ((Get-Process | Where-Object { $_.ProcessName -like "WinForward*" -or $_.ProcessName -like "sing-box*" -or $_.ProcessName -like "orchestrator*" } | Select-Object Id,ProcessName | Format-Table -AutoSize | Out-String).Trim())
      Write-Output "--- utc ---"
      Write-Output (Get-Date).ToUniversalTime().ToString("o")
      Write-Output "--- heartbeat ---"
      Write-Output (Get-Item C:/wfbench/heartbeat.txt).LastWriteTimeUtc.ToString("o")
    }
    default { Write-Output ("unknown verb " + $Verb) }
  }
} catch {
  Write-Output ("ERROR " + $Verb + " " + $A1 + ": " + $_.Exception.Message)
}
