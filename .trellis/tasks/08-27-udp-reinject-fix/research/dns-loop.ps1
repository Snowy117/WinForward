param(
    [int]$Count = 20,
    [string]$DnsServer = '223.5.5.5',
    [int]$TimeoutMs = 2000
)

$results = @()
for ($i = 1; $i -le $Count; $i++) {
    $tid = Get-Random -Minimum 0 -Maximum 65535
    $tidHi = [math]::Floor($tid / 256)
    $tidLo = $tid % 256

    # DNS query: example.com A IN
    $q = [byte[]]($tidHi, $tidLo, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,
                   7, 0x65, 0x78, 0x61, 0x6D, 0x70, 0x6C, 0x65, 3, 0x63, 0x6F, 0x6D, 0,
                   0, 1, 0, 1)

    $client = New-Object System.Net.Sockets.UdpClient
    $client.Client.ReceiveTimeout = $TimeoutMs
    try {
        $client.Connect($DnsServer, 53)
        $null = $client.Send($q, $q.Length)
        $remoteEp = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $resp = $client.Receive([ref]$remoteEp)
            $sw.Stop()
            $ok = ($resp.Length -gt 12) -and ($resp[0] -eq $tidHi) -and ($resp[1] -eq $tidLo)
            $results += [pscustomobject]@{ Attempt = $i; Success = $ok; Ms = $sw.ElapsedMilliseconds; Bytes = $resp.Length }
        } catch {
            $sw.Stop()
            $results += [pscustomobject]@{ Attempt = $i; Success = $false; Ms = $sw.ElapsedMilliseconds; Bytes = 0 }
        }
    } finally {
        $client.Close()
    }
    Start-Sleep -Milliseconds 300
}

$ok = ($results | Where-Object { $_.Success }).Count
Write-Output ("RESULT: {0}/{1} succeeded" -f $ok, $Count)
$results | Format-Table -AutoSize | Out-String -Width 120
