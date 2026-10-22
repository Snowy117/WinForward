#pragma once

#include <cstdint>

#include "forwarded_blob.h"
#include "redirect_context.h"
#include "rule_blob.h"

// Forwarded 连接事实与规则求值 —— 纯函数、无分配、无内核依赖。
//
// match_forwarded 假定 view 来自成功的 try_open_forwarded_blob：解码阶段已经把所有段、
// 池偏移与族一致性验证到界内，求值阶段不再判界。规则数组的顺序就是优先级，首个命中即返回。
//
// 与 host matcher 的差异只在匹配维度：forwarded 多了入口适配器与客户端网段，没有进程。
// 网段、端口、协议与地址族的比较语义与 host 保持一致（复用 network_prefix 与常量）。

namespace winforward::wfp
{

struct forwarded_connection_view
{
    std::uint32_t ingress_adapter_id;
    std::uint8_t client_address[16]; // IPv4 只用前 4 字节
    std::uint8_t remote_address[16];
    std::uint16_t remote_port;
    std::uint8_t protocol;
    address_family family;
};

struct forwarded_match_result
{
    rule_action action;
    std::uint32_t rule_index; // 未命中时为 rule_pool_index_any
    std::uint32_t upstream_id; // 未命中或非 redirect 时为 forwarded_upstream_none
    bool matched;
};

static_assert(static_cast<std::uint8_t>(address_family::ipv4) == rule_family_ipv4,
              "forwarded_connection_view 与 rule 的地址族编码必须一致");
static_assert(static_cast<std::uint8_t>(address_family::ipv6) == rule_family_ipv6,
              "forwarded_connection_view 与 rule 的地址族编码必须一致");
static_assert(static_cast<std::uint8_t>(transport_protocol::tcp) == rule_protocol_tcp,
              "forwarded_connection_view 与 rule 的协议编码必须一致");
static_assert(static_cast<std::uint8_t>(transport_protocol::udp) == rule_protocol_udp,
              "forwarded_connection_view 与 rule 的协议编码必须一致");

namespace detail
{

// 比较前先确认地址族一致，再逐位比较前缀；client/remote 两个地址共用这份实现。
[[nodiscard]] inline bool forwarded_network_matches(const network_prefix& prefix,
                                                    const std::uint8_t* address) noexcept
{
    const std::uint32_t full_bytes = prefix.prefix_length / 8U;
    const std::uint32_t remaining_bits = prefix.prefix_length % 8U;
    for (std::uint32_t index = 0; index < full_bytes; ++index)
    {
        if (address[index] != prefix.network[index])
        {
            return false;
        }
    }
    if (remaining_bits == 0)
    {
        return true;
    }

    const auto mask = static_cast<std::uint8_t>(0xFFU << (8U - remaining_bits));
    return ((address[full_bytes] ^ prefix.network[full_bytes]) & mask) == 0;
}

[[nodiscard]] inline bool forwarded_rule_matches(const forwarded_blob_view& view,
                                                 const forwarded_blob_rule& selected,
                                                 const forwarded_connection_view& connection) noexcept
{
    if (selected.ingress_adapter_id != forwarded_adapter_any &&
        selected.ingress_adapter_id != connection.ingress_adapter_id)
    {
        return false;
    }

    const auto connection_family = static_cast<std::uint8_t>(connection.family);
    if (selected.family != rule_family_any && selected.family != connection_family)
    {
        return false;
    }
    if (selected.protocol != rule_protocol_any && selected.protocol != connection.protocol)
    {
        return false;
    }

    const bool any_port =
        selected.remote_port_start == rule_port_any_begin && selected.remote_port_end == rule_port_any_end;
    if (!any_port &&
        (connection.remote_port < selected.remote_port_start || connection.remote_port > selected.remote_port_end))
    {
        return false;
    }

    if (selected.remote_network_index != rule_pool_index_any)
    {
        const network_prefix& prefix = view.network_pool[selected.remote_network_index / sizeof(network_prefix)];
        if (prefix.family != connection_family)
        {
            return false;
        }
        if (!forwarded_network_matches(prefix, connection.remote_address))
        {
            return false;
        }
    }

    if (selected.client_network_index != rule_pool_index_any)
    {
        const network_prefix& prefix = view.network_pool[selected.client_network_index / sizeof(network_prefix)];
        if (prefix.family != connection_family)
        {
            return false;
        }
        if (!forwarded_network_matches(prefix, connection.client_address))
        {
            return false;
        }
    }

    return true;
}

} // namespace detail

// 按数组顺序返回首个命中的动作、序号与上游。未命中时 matched 为 false，action 取
// view.fallback_action，rule_index 为 rule_pool_index_any，upstream_id 为
// forwarded_upstream_none；调用方一律以 matched 为准，不要把 action 当成命中的证据。
[[nodiscard]] inline forwarded_match_result
match_forwarded(const forwarded_blob_view& view, const forwarded_connection_view& connection) noexcept
{
    for (std::uint32_t index = 0; index < view.rule_count; ++index)
    {
        const forwarded_blob_rule& selected = view.rules[index];
        if (!detail::forwarded_rule_matches(view, selected, connection))
        {
            continue;
        }
        return forwarded_match_result{
            .action = selected.action,
            .rule_index = index,
            .upstream_id = selected.upstream_id,
            .matched = true,
        };
    }
    return forwarded_match_result{
        .action = view.fallback_action,
        .rule_index = rule_pool_index_any,
        .upstream_id = forwarded_upstream_none,
        .matched = false,
    };
}

} // namespace winforward::wfp
