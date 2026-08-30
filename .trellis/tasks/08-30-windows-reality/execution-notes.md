# Execution notes (local environment, not for the repo)

- Target: Win11 IoT LTSC VM `192.168.100.2` (WinRM 5985), user `neko`, password via
  `$NEKO_PASS` env var. Driven through `evil-winrm-py` inside `tmux` session `winrm`
  (windows: 0.0 main / s2 sampler / s3 transfer).
- VM facts: 32 logical cores, 4 GB RAM, admin session, OS build 10.0.26100.
- Power plan switched Balanced → High Performance
  (`powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`) before measurement;
  restore with `powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e` if desired.
- Remote work dir: `C:\Users\Neko\wf-bench` (exe + JSONL + soak-sample.csv +
  BenchmarkDotNet.Artifacts). Local publish staging: `/tmp/wf-bench-publish`.
- evil-winrm-py gotchas hit this session: no non-interactive exec mode (tmux PTY
  required); `send-keys` payloads must be single-quoted to survive local shell
  expansion; pane wraps at 80 cols so JSONL lines must be `download`ed, not scraped.
- BDN on the VM requires `--inProcess` (no .NET SDK on the guest; process-isolated
  toolchain prints "requires dotnet SDK" and executes 0 benchmarks).
- BDN multi-family filter that worked on both OSes:
  `-f '*CapturePump*' '*Dispatcher*' '*TcpRelay*' '*UdpSession*' '*NdisBuffer*'`.
- Linux BDN must be launched from the repo root (running from another cwd makes the
  CsProjGenerator fail with "Unable to find WinForward.Benchmarks").
- Linux VM companion: 16 logical cores, Ryzen 9 9955HX host, Ubuntu 24.04, .NET SDK
  10.0.302, runtime 10.0.10.
