#pragma once

#include <cstdint>

#include "redirect_context.h"
#include "rule_blob.h"

// 连接事实与规则求值 —— 纯函数、无分配、无内核依赖。
//
// match 假定 view 来自成功的 try_open_blob：解码阶段已经把所有段与池偏移验证到
// 界内，求值阶段不再判界。规则数组的顺序就是优先级，首个命中即返回。
//
// 进程路径两侧都按 ASCII 大小写不敏感比较。规则串在编译期已规整为小写，连接
// 事实按接口约定也已小写化；这里仍然做一次折叠，使实现在两种输入下都正确。

namespace winforward::wfp
{

struct connection_view
{
    const char* process_path; // 已小写化
    std::uint32_t process_path_length;
    std::uint8_t remote_address[16]; // IPv4 只用前 4 字节
    std::uint16_t remote_port;
    std::uint8_t protocol;
    address_family family;
};

struct match_result
{
    rule_action action;
    std::uint32_t rule_index; // 未命中时为 rule_pool_index_any
    bool matched;
};

static_assert(static_cast<std::uint8_t>(address_family::ipv4) == rule_family_ipv4,
              "connection_view 与 rule 的地址族编码必须一致");
static_assert(static_cast<std::uint8_t>(address_family::ipv6) == rule_family_ipv6,
              "connection_view 与 rule 的地址族编码必须一致");
static_assert(static_cast<std::uint8_t>(transport_protocol::tcp) == rule_protocol_tcp,
              "connection_view 与 rule 的协议编码必须一致");

namespace detail
{

[[nodiscard]] constexpr std::uint8_t ascii_lower(std::uint8_t value) noexcept
{
    const auto upper_a = static_cast<std::uint8_t>('A');
    const auto upper_z = static_cast<std::uint8_t>('Z');
    if (value < upper_a || value > upper_z)
    {
        return value;
    }
    return static_cast<std::uint8_t>(value + static_cast<std::uint8_t>('a' - 'A'));
}

[[nodiscard]] inline std::uint32_t bounded_length(const char* text, std::uint32_t limit) noexcept
{
    std::uint32_t length = 0;
    while (length < limit && text[length] != '\0')
    {
        ++length;
    }
    return length;
}

[[nodiscard]] inline bool
process_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    if (selected.process_index == rule_pool_index_any)
    {
        return true;
    }

    const char* selector = view.process_pool + selected.process_index;
    const std::uint32_t selector_length = bounded_length(selector, view.process_pool_size - selected.process_index);
    if (selector_length != connection.process_path_length)
    {
        return false;
    }
    if (selector_length == 0)
    {
        return true;
    }
    if (connection.process_path == nullptr)
    {
        return false;
    }

    for (std::uint32_t index = 0; index < selector_length; ++index)
    {
        const auto selector_byte = static_cast<std::uint8_t>(selector[index]);
        const auto connection_byte = static_cast<std::uint8_t>(connection.process_path[index]);
        if (ascii_lower(selector_byte) != ascii_lower(connection_byte))
        {
            return false;
        }
    }
    return true;
}

[[nodiscard]] inline bool network_matches(const network_prefix& prefix, const connection_view& connection) noexcept
{
    if (prefix.family != static_cast<std::uint8_t>(connection.family))
    {
        return false;
    }

    const std::uint32_t full_bytes = prefix.prefix_length / 8U;
    const std::uint32_t remaining_bits = prefix.prefix_length % 8U;
    for (std::uint32_t index = 0; index < full_bytes; ++index)
    {
        if (connection.remote_address[index] != prefix.network[index])
        {
            return false;
        }
    }
    if (remaining_bits == 0)
    {
        return true;
    }

    const auto mask = static_cast<std::uint8_t>(0xFFU << (8U - remaining_bits));
    return ((connection.remote_address[full_bytes] ^ prefix.network[full_bytes]) & mask) == 0;
}

[[nodiscard]] inline bool
rule_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    if (selected.family != rule_family_any && selected.family != static_cast<std::uint8_t>(connection.family))
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

    if (selected.network_index != rule_pool_index_any)
    {
        const network_prefix& prefix = view.network_pool[selected.network_index / sizeof(network_prefix)];
        if (!network_matches(prefix, connection))
        {
            return false;
        }
    }

    return process_matches(view, selected, connection);
}

} // namespace detail

// 按数组顺序返回首个命中的动作与序号。未命中时 matched 为 false，action 取
// view.fallback_action，rule_index 为 rule_pool_index_any；调用方一律以 matched 为准，
// 不要把 action 当成命中的证据。
[[nodiscard]] inline match_result match(const blob_view& view, const connection_view& connection) noexcept
{
    for (std::uint32_t index = 0; index < view.rule_count; ++index)
    {
        const rule& selected = view.rules[index];
        if (!detail::rule_matches(view, selected, connection))
        {
            continue;
        }
        return match_result{ .action = selected.action, .rule_index = index, .matched = true };
    }
    return match_result{ .action = view.fallback_action, .rule_index = rule_pool_index_any, .matched = false };
}

} // namespace winforward::wfp
