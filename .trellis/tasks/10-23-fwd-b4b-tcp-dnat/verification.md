# B4b 验证记录（2026-10-23）

环境：WinLTSC 192.168.100.2（driver ABI v5，测试签名，RUNNING）；Linux 192.168.100.4；
测试目标 echo 40250；fake SOCKS5 40500；VM Internal 临时 on-link 别名 192.168.100.99/24 + TCP 40400
防火墙放行（测试后均已删除）。Linux 宿主没有 root，无法加 10.99.0.0/24 测试路由，故用 on-link
别名代替；两者都走同一条 Internal L2 入向路径。检查 LWF 关闭时两次重启 VM 恢复 WinRM（未 reset），
重启后重装驱动并恢复 disabled/空表。

## 返工（检查阻断：回程间歇丢数据）

检查者复现：外部客户端发 65536B 后立即 half-close，单客户端也会失败；客户端 recv=0 或
63712B 后超时，TCP ACK 停在 ISN+1，Windows 重传增加；pktmon 在 NIC 组件看到 44×1448B 的
SNAT 后数据帧，下一个 376B 段完全没出现。不是并发 race、不是 fake SOCKS 收尾、LWF 关掉同样复现。

根因：MAC 层一个 NBL 可以带多个 NET_BUFFER（每帧一个）。旧实现只取
NET_BUFFER_LIST_FIRST_NB，回程 SNAT 只注入首帧；同一 NBL 的 376B 段被原 NBL 的 block 消费掉，
接收端出现 1824B 空洞（44×1448B=63712B）。修复：

- probe_forwarded_parse_frame / inject 改为接收 NET_BUFFER；classify 的入口 DNAT 与回程 SNAT 都
  逐 NET_BUFFER 查表/改写/注入，原 NBL 整体 block；计数仍按 NBL 记，新增 redirect_multi_nb。
- 注入完成例程检查 NET_BUFFER_LIST_STATUS，非 SUCCESS 计 inject_complete_failed / last_status；
  inject_complete_calls 可核对每个成功注入都有回调。
- stop 在 FwpsInjectionHandleDestroy0 后再等 outstanding_injections 归零，才销毁 NBL 池。
- 外部客户端在数据面结束后连 verify 端口回报 bytes/sha/acked，套件按 client_port 配对断言；
  没有这条回报不再可能假 PASS。
- classify 的查表/建表/ closing 标记抽到 probe_forwarded_path.cpp，classify 降到 500 有效行以内。

## VM 套件

- forwarded-tcp --phase core：客户端 65536B 字节一致，peer=192.168.100.99:40250；
  stats redirected=59 (ingress=52 return=7)、multi_nb=7、complete=102/failed=0、return_miss=0；
  单客户端 10/10、core 13/13（每轮客户端实收 65536B、SHA 一致、acked>=65536）。
- forwarded-tcp --phase upstreams：两条规则分别命中 upstream 0/1，两个测试 SOCKS5 各一次 CONNECT，
  65536B 双向一致（顺序与并发各跑一轮）；--expect-accepts 5 的单上游并发 5/5，
  redirected=306 (ingress=266 return=40)、multi_nb=33、complete=520/failed=0。
- forwarded-tcp --phase fail：config disabled 时客户端超时，listener 无 accept，
  redirect_no_config=3、redirect_blocked=3、blocked=3，恒等式 PASS。
- forwarded-rules --only F1,F2,F3,F6：PASS（F6 已改为“非 redirect 带 upstream 被拒”）。
- rules --iterations 10：PASS。
- selftest --remote 192.168.100.4:40250 --iterations 3：PASS。
- gates：PASS（四道防循环闸）。
- udp-rules --timeout-ms 10000 --ping-host 192.168.100.4 --connected-window-ms 1000：PASS。

## 产品 E2E（forwarded）

WinForward.Controller run，forwarded 规则 adapterName=Internal / client 192.168.100.4/32 /
remote 192.168.100.99/32 / TCP 40250 / target=b4b；SOCKS5=192.168.100.4:40500。

- controller-forwarded-config enabled=1 listener_port=49682（最终源码复跑） listener=192.168.100.2 family=4
- relay accept #1 original=192.168.100.99:40250 source=forwarded family=IPv4 protocol=TCP
  pid=0 rule=0 upstream_id=0 ingress=5 upstream=socks5:192.168.100.4:40500 client=192.168.100.4:<port>
- relay connected #1 ... via=socks5:192.168.100.4:40500
- relay close #1 in=65536 out=65536
- 退出：controller-config-disabled、controller-forwarded-config-disabled、空 blob 回读、
  controller-stop accepted=1 refused=0 bytes_in=65536 bytes_out=65536、controller-exit code=0
- Linux 客户端：bytes=65536 sha 一致，peer 仍是 192.168.100.99:40250。

## 回归

- userland-e2e.ps1：PASS checks=48（host TCP V4/V6、2×1MiB、重启、records/self-PID 闸）。
- userland-socks5-e2e.ps1 -Remote 192.168.100.4:40250 -TargetPort 40250 -ProxyPort 40501：
  PASS checks=38（host SOCKS5 失败轮 + 成功轮）。
- gate.ps1 -Remote 192.168.100.4:40250：GATE-RESULT=PASS（构建 0 warning、签名 Valid、
  PREfast 0 告警、rules/forwarded-rules/udp-rules/selftest/gates/firewall、用户态 220 单测、
  vm-check、userland-e2e 48、卸载无新增 WfPr outstanding、VM 未重启）。
- Linux：tools/check.sh --full 8/8 PASS（格式/有效行/cppcheck/禁止模式/clang-tidy/wf_check）；
  ctest 104/104；dotnet 单测 220/220；tools/userland/verify-gates.sh 23/23 PASS。
- Windows route print 前后一致（无 10.99 / 192.168.100.99 残留）；Linux 未加测试路由。
- 驱动保持 RUNNING，forwarded config=disabled、空 permit 表；回滚文件
  C:\workspace\rollback\wfprobe-abi4.sys / wfprobe-abi5.sys。

## 未覆盖

- 4096 条真实连接的表满压力（容量语义由建表返回值 + redirect_capacity 计数覆盖）。
- 多入口 / 多 listener 端口 / 多 L（B6）。
- UDP redirect（B5）。
- LWF 对注入帧的可见性；关闭 LWF 的复跑也没完成：Disable-NetAdapterBinding 与 sc stop ndisrd
  都会断 WinRM 管理通道（两次重启后已恢复 LWF，当前 Enabled=True / RUNNING）。检查者已在关闭
  LWF 时复现旧丢包，根因在驱动 NET_BUFFER 处理，与 LWF 无关；修复后的数据面验证都在 LWF 开启下完成。
- forwarded 路径的“上游失败”E2E：connector 单测覆盖不回退直连，host socks5 E2E 覆盖失败轮；
  forwarded 失败轮未单独跑。
