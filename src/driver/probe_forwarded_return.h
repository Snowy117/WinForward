#pragma once

#include <cstdint>

#include "probe_driver.h"
#include "probe_forwarded_frame.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{

struct forwarded_return_decision
{
    bool handled; // true 表示回程分支已经消费了这次 classify
    bool block; // handled 为 true 时原 NBL 的最终动作
};

// 回程 TCP SNAT：listener 进程发出的 TCP 帧，源端口等于 listener_port。MAC 层一个 NBL 可以带多个
// NET_BUFFER（每帧一个），必须逐帧查询/改写/注入；原 NBL 整体 block。
[[nodiscard]] forwarded_return_decision handle_forwarded_tcp_return(forwarded_state& state,
                                                                    const forwarded_frame_facts& facts,
                                                                    void* layer_data,
                                                                    std::uint32_t ndis_port,
                                                                    probe_forwarded_stats& delta) noexcept;

// 回程 UDP SNAT：listener 用同一个 socket 回包时，源端口等于 listener_port。命中就改写源地址/端口
// 并注入；未命中的 listener 源端口流量按普通本机流量 permit（不参与 forwarded 规则匹配）。
[[nodiscard]] forwarded_return_decision handle_forwarded_udp_return(forwarded_state& state,
                                                                    const forwarded_frame_facts& facts,
                                                                    void* layer_data,
                                                                    std::uint32_t ndis_port,
                                                                    probe_forwarded_stats& delta) noexcept;

} // namespace winforward::wfp::probe
