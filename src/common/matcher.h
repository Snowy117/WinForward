#pragma once

#include <cstdint>

#include "redirect_context.h"
#include "rule_blob.h"

// 连接事实与规则求值 —— 纯函数、无分配、无内核依赖。
//
// match 假定 view 来自成功的 try_open_blob：解码阶段已经把所有段与池偏移验证到
// 界内，求值阶段不再判界。规则数组的顺序就是优先级，首个命中即返回。
//
// 同一字段的选择器区间内 OR、字段之间 AND；count == 0 表示该字段不限制。进程
// 选择器分文件名与路径两种：文件名比较连接路径的最后一段，路径按“完整路径相等
// 或前缀 + \”匹配；两者都只折叠 ASCII 大小写，非 ASCII 字节原样比较。连接路径
// 缺失或为空时，任何进程选择器都不命中。

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
static_assert(static_cast<std::uint8_t>(transport_protocol::udp) == rule_protocol_udp,
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

// 只折叠 ASCII；长度相等是前提，非 ASCII 字节按原值比较。
[[nodiscard]] inline bool ascii_equal(const char* first, const char* second, std::uint32_t length) noexcept
{
    for (std::uint32_t index = 0; index < length; ++index)
    {
        const auto first_byte = static_cast<std::uint8_t>(first[index]);
        const auto second_byte = static_cast<std::uint8_t>(second[index]);
        if (ascii_lower(first_byte) != ascii_lower(second_byte))
        {
            return false;
        }
    }
    return true;
}

[[nodiscard]] inline bool process_selector_matches(const char* selector,
                                                   std::uint32_t selector_length,
                                                   const process_selector& record,
                                                   const connection_view& connection) noexcept
{
    if (record.kind == process_selector_kind::file_name)
    {
        std::uint32_t name_begin = 0;
        for (std::uint32_t index = 0; index < connection.process_path_length; ++index)
        {
            if (connection.process_path[index] == '\\')
            {
                name_begin = index + 1;
            }
        }
        const std::uint32_t name_length = connection.process_path_length - name_begin;
        return name_length == selector_length &&
               ascii_equal(selector, connection.process_path + name_begin, selector_length);
    }

    if (selector_length == connection.process_path_length)
    {
        return ascii_equal(selector, connection.process_path, selector_length);
    }
    // 目录语义：连接路径以 `选择器 + \` 开头。选择器在解码期已保证不以 \ 结尾。
    if (connection.process_path_length <= selector_length || connection.process_path[selector_length] != '\\')
    {
        return false;
    }
    return ascii_equal(selector, connection.process_path, selector_length);
}

[[nodiscard]] inline bool
process_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    if (selected.process_count == 0)
    {
        return true;
    }
    if (connection.process_path == nullptr || connection.process_path_length == 0)
    {
        return false;
    }

    for (std::uint32_t index = 0; index < selected.process_count; ++index)
    {
        const process_selector& record = view.selector_pool[selected.process_first + index];
        const char* selector = view.process_pool + record.string_offset;
        const std::uint32_t selector_length = bounded_length(selector, view.process_pool_size - record.string_offset);
        if (process_selector_matches(selector, selector_length, record, connection))
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] constexpr std::uint8_t protocol_bit(std::uint8_t protocol) noexcept
{
    if (protocol == rule_protocol_tcp)
    {
        return rule_protocol_mask_tcp;
    }
    if (protocol == rule_protocol_udp)
    {
        return rule_protocol_mask_udp;
    }
    return 0;
}

// 掩码 0 表示不限制，但规则只表达 TCP / UDP；其他协议在 classify 侧单独计数，不命中任何规则。
[[nodiscard]] inline bool protocol_matches(const rule& selected, const connection_view& connection) noexcept
{
    const std::uint8_t bit = protocol_bit(connection.protocol);
    if (bit == 0)
    {
        return false;
    }
    return selected.protocol_mask == rule_protocol_mask_any || (selected.protocol_mask & bit) != 0;
}

[[nodiscard]] constexpr std::uint8_t family_bit(address_family family) noexcept
{
    if (family == address_family::ipv4)
    {
        return rule_family_mask_ipv4;
    }
    if (family == address_family::ipv6)
    {
        return rule_family_mask_ipv6;
    }
    return 0;
}

[[nodiscard]] inline bool family_matches(const rule& selected, const connection_view& connection) noexcept
{
    const std::uint8_t bit = family_bit(connection.family);
    if (bit == 0)
    {
        return false;
    }
    return selected.family_mask == rule_family_mask_any || (selected.family_mask & bit) != 0;
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
any_network_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    for (std::uint32_t index = 0; index < selected.network_count; ++index)
    {
        if (network_matches(view.network_pool[selected.network_first + index], connection))
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] inline bool
any_port_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    for (std::uint32_t index = 0; index < selected.port_count; ++index)
    {
        const port_range& range = view.port_pool[selected.port_first + index];
        if (connection.remote_port >= range.start && connection.remote_port <= range.end)
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] inline bool
rule_matches(const blob_view& view, const rule& selected, const connection_view& connection) noexcept
{
    if (!family_matches(selected, connection) || !protocol_matches(selected, connection))
    {
        return false;
    }
    if (selected.port_count != 0 && !any_port_matches(view, selected, connection))
    {
        return false;
    }
    if (selected.network_count != 0 && !any_network_matches(view, selected, connection))
    {
        return false;
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
