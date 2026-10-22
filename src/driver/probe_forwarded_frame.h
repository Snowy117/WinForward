#pragma once

// forwarded MAC 帧的解析结果、改写与注入。只允许在 probe_driver.h（WDK 头）之后包含：
// 这里用到 NET_BUFFER_LIST / NTSTATUS。
//
// 解析与注入共用同一份 offset：classify 用 parse 得到事实，注入时按事实里的 network/transport
// 偏移改写并重算校验和，不重新猜 VLAN / IPv4 option / IPv6 扩展头的长度。

#include <cstdint>

namespace winforward::wfp::probe
{

enum class forwarded_frame_kind : std::uint8_t
{
    ip = 0,
    non_ip = 1,
    malformed = 2,
};

struct forwarded_frame_facts
{
    std::uint32_t data_length; // 整个 NBL 的帧长（含以太网头与 VLAN）
    std::uint32_t network_offset; // IP 头起点
    std::uint32_t transport_offset; // TCP 头起点
    std::uint32_t ip_header_length; // IPv4 头长；IPv6 为 40
    std::uint32_t ip_payload_length; // IPv4 total_length - IHL；IPv6 payload_length - 扩展头
    std::uint32_t tcp_length; // TCP 头 + 负载（校验和覆盖范围）
    std::uint16_t source_port;
    std::uint16_t destination_port;
    std::uint8_t protocol;
    std::uint8_t family; // 4 / 6
    std::uint8_t tcp_flags;
    std::uint8_t destination_mac[6];
    std::uint8_t source_mac[6];
    std::uint8_t source_address[16];
    std::uint8_t destination_address[16];
};

// 解析失败返回 malformed，非 IP（ARP/LLDP）返回 non_ip；两者都不参与规则。
[[nodiscard]] forwarded_frame_kind probe_forwarded_parse_frame(const NET_BUFFER* buffer,
                                                               forwarded_frame_facts& facts) noexcept;
// 规则匹配只看 NBL 的首个 NET_BUFFER；MAC 层一个 NBL 可以带多个 NET_BUFFER（每帧一个）。
[[nodiscard]] forwarded_frame_kind probe_forwarded_parse_frame(const NET_BUFFER_LIST* nbl,
                                                               forwarded_frame_facts& facts) noexcept;

// 一次改写的全部动作；指针为 nullptr 表示不改。地址均为 16 字节存储（IPv4 只用前 4 字节）。
struct forwarded_frame_rewrite
{
    const std::uint8_t* source_address;
    const std::uint8_t* destination_address;
    std::uint16_t source_port;
    std::uint16_t destination_port;
    bool rewrite_source_port;
    bool rewrite_destination_port;
    const std::uint8_t* source_mac;
    const std::uint8_t* destination_mac;
};

// 复制整帧 → 改写 → 重算 IP/TCP 校验和 → FwpsInjectMacReceiveAsync0（入栈）
// 或 FwpsInjectMacSendAsync0（出栈）。返回调用（排队）状态；失败由调用方 block 原帧并计数。
[[nodiscard]] NTSTATUS probe_forwarded_inject_frame(const NET_BUFFER* source,
                                                    const forwarded_frame_facts& facts,
                                                    bool outbound,
                                                    std::uint32_t interface_index,
                                                    std::uint32_t ndis_port,
                                                    const forwarded_frame_rewrite& rewrite) noexcept;

} // namespace winforward::wfp::probe
