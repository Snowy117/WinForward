#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#include "rule_blob.h"

// 测试专用的规则 blob v2 构造器，不属于产品头。
// 进程串按加入顺序连续排列并去重；selector / 网段 / 端口记录按调用顺序追加，
// 各段起点 4 字节对齐，构造结果与 rule_blob.h 描述的 v2 布局一致。add_* 返回的是
// 记录下标（进程串返回池内字节偏移），负向夹具可以直接改 build() 的产物。

namespace wfp_test
{

using winforward::wfp::network_prefix;
using winforward::wfp::port_range;
using winforward::wfp::process_selector;
using winforward::wfp::process_selector_kind;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_blob_header;

inline constexpr std::size_t blob_alignment = 4;

[[nodiscard]] inline std::size_t align_up(std::size_t value, std::size_t alignment) noexcept
{
    return (value + alignment - 1U) & ~(alignment - 1U);
}

[[nodiscard]] inline rule_blob_header read_header(const std::vector<std::uint8_t>& blob) noexcept
{
    rule_blob_header header{};
    std::memcpy(&header, blob.data(), sizeof(header));
    return header;
}

inline void write_header(std::vector<std::uint8_t>& blob, const rule_blob_header& header) noexcept
{
    std::memcpy(blob.data(), &header, sizeof(header));
}

[[nodiscard]] inline rule read_rule(const std::vector<std::uint8_t>& blob, std::size_t index) noexcept
{
    const rule_blob_header header = read_header(blob);
    rule entry{};
    std::memcpy(&entry, blob.data() + header.rules_offset + index * sizeof(entry), sizeof(entry));
    return entry;
}

inline void write_rule(std::vector<std::uint8_t>& blob, std::size_t index, const rule& entry) noexcept
{
    const rule_blob_header header = read_header(blob);
    std::memcpy(blob.data() + header.rules_offset + index * sizeof(entry), &entry, sizeof(entry));
}

[[nodiscard]] inline process_selector read_selector(const std::vector<std::uint8_t>& blob, std::size_t index) noexcept
{
    const rule_blob_header header = read_header(blob);
    process_selector selector{};
    std::memcpy(&selector, blob.data() + header.selector_pool_offset + index * sizeof(selector), sizeof(selector));
    return selector;
}

inline void
write_selector(std::vector<std::uint8_t>& blob, std::size_t index, const process_selector& selector) noexcept
{
    const rule_blob_header header = read_header(blob);
    std::memcpy(blob.data() + header.selector_pool_offset + index * sizeof(selector), &selector, sizeof(selector));
}

[[nodiscard]] inline network_prefix read_network(const std::vector<std::uint8_t>& blob, std::size_t index) noexcept
{
    const rule_blob_header header = read_header(blob);
    network_prefix prefix{};
    std::memcpy(&prefix, blob.data() + header.network_pool_offset + index * sizeof(prefix), sizeof(prefix));
    return prefix;
}

inline void write_network(std::vector<std::uint8_t>& blob, std::size_t index, const network_prefix& prefix) noexcept
{
    const rule_blob_header header = read_header(blob);
    std::memcpy(blob.data() + header.network_pool_offset + index * sizeof(prefix), &prefix, sizeof(prefix));
}

[[nodiscard]] inline port_range read_port(const std::vector<std::uint8_t>& blob, std::size_t index) noexcept
{
    const rule_blob_header header = read_header(blob);
    port_range range{};
    std::memcpy(&range, blob.data() + header.port_pool_offset + index * sizeof(range), sizeof(range));
    return range;
}

inline void write_port(std::vector<std::uint8_t>& blob, std::size_t index, const port_range& range) noexcept
{
    const rule_blob_header header = read_header(blob);
    std::memcpy(blob.data() + header.port_pool_offset + index * sizeof(range), &range, sizeof(range));
}

[[nodiscard]] inline bool open_blob(const std::vector<std::uint8_t>& blob, winforward::wfp::blob_view& view) noexcept
{
    return winforward::wfp::try_open_blob(blob.data(), static_cast<std::uint32_t>(blob.size()), view);
}

// 所有区间 / 掩码都取“任意”的规则。
[[nodiscard]] inline rule make_any_rule() noexcept
{
    rule entry{};
    entry.action = rule_action::permit;
    return entry;
}

class rule_blob_builder
{
  public:
    void set_fallback_action(rule_action action)
    {
        fallback = action;
    }

    std::uint32_t add_process_string(std::string_view text)
    {
        std::uint32_t offset = 0;
        for (const process_string& entry : process_strings)
        {
            if (std::string_view(entry.text) == text)
            {
                return entry.offset;
            }
            offset += static_cast<std::uint32_t>(entry.text.size() + 1);
        }
        process_strings.push_back(process_string{ .text = std::string(text), .offset = offset });
        return offset;
    }

    std::uint32_t add_selector(std::string_view text, process_selector_kind kind, bool is_directory = false)
    {
        process_selector selector{};
        selector.string_offset = add_process_string(text);
        selector.kind = kind;
        selector.is_directory = is_directory ? 1U : 0U;
        selectors.push_back(selector);
        return static_cast<std::uint32_t>(selectors.size() - 1);
    }

    std::uint32_t add_network(std::uint8_t family,
                              std::uint8_t prefix_length,
                              const std::array<std::uint8_t, winforward::wfp::network_address_size>& address)
    {
        network_prefix prefix{};
        prefix.prefix_length = prefix_length;
        prefix.family = family;
        for (std::size_t index = 0; index < address.size(); ++index)
        {
            prefix.network[index] = address[index];
        }
        return add_network(prefix);
    }

    std::uint32_t add_network(const network_prefix& prefix)
    {
        network_prefixes.push_back(prefix);
        return static_cast<std::uint32_t>(network_prefixes.size() - 1);
    }

    std::uint32_t add_port(std::uint16_t start, std::uint16_t end)
    {
        ports.push_back(port_range{ .start = start, .end = end });
        return static_cast<std::uint32_t>(ports.size() - 1);
    }

    void add_rule(const rule& entry)
    {
        rules.push_back(entry);
    }

    [[nodiscard]] std::vector<std::uint8_t> build() const
    {
        const std::size_t rules_offset = align_up(sizeof(rule_blob_header), blob_alignment);
        const std::size_t process_offset = align_up(rules_offset + rules.size() * sizeof(rule), blob_alignment);
        std::size_t process_size = 0;
        for (const process_string& entry : process_strings)
        {
            process_size += entry.text.size() + 1;
        }
        const std::size_t selector_offset = align_up(process_offset + process_size, blob_alignment);
        const std::size_t network_offset =
            align_up(selector_offset + selectors.size() * sizeof(process_selector), blob_alignment);
        const std::size_t port_offset =
            align_up(network_offset + network_prefixes.size() * sizeof(network_prefix), blob_alignment);
        const std::size_t total_size = port_offset + ports.size() * sizeof(port_range);

        std::vector<std::uint8_t> blob(total_size, 0);
        rule_blob_header header{};
        header.magic = winforward::wfp::rule_blob_magic;
        header.version = winforward::wfp::rule_blob_version;
        header.header_size = static_cast<std::uint16_t>(sizeof(rule_blob_header));
        header.total_size = static_cast<std::uint32_t>(total_size);
        header.rule_count = static_cast<std::uint32_t>(rules.size());
        header.fallback_action = static_cast<std::uint32_t>(fallback);
        header.rules_offset = static_cast<std::uint32_t>(rules_offset);
        header.process_pool_offset = static_cast<std::uint32_t>(process_offset);
        header.process_pool_size = static_cast<std::uint32_t>(process_size);
        header.selector_pool_offset = static_cast<std::uint32_t>(selector_offset);
        header.selector_pool_size = static_cast<std::uint32_t>(selectors.size() * sizeof(process_selector));
        header.network_pool_offset = static_cast<std::uint32_t>(network_offset);
        header.network_pool_size = static_cast<std::uint32_t>(network_prefixes.size() * sizeof(network_prefix));
        header.port_pool_offset = static_cast<std::uint32_t>(port_offset);
        header.port_pool_size = static_cast<std::uint32_t>(ports.size() * sizeof(port_range));
        write_header(blob, header);

        std::size_t offset = rules_offset;
        for (const rule& entry : rules)
        {
            std::memcpy(blob.data() + offset, &entry, sizeof(entry));
            offset += sizeof(entry);
        }
        offset = process_offset;
        for (const process_string& entry : process_strings)
        {
            std::memcpy(blob.data() + offset, entry.text.data(), entry.text.size());
            offset += entry.text.size() + 1;
        }
        offset = selector_offset;
        for (const process_selector& entry : selectors)
        {
            std::memcpy(blob.data() + offset, &entry, sizeof(entry));
            offset += sizeof(entry);
        }
        offset = network_offset;
        for (const network_prefix& entry : network_prefixes)
        {
            std::memcpy(blob.data() + offset, &entry, sizeof(entry));
            offset += sizeof(entry);
        }
        offset = port_offset;
        for (const port_range& entry : ports)
        {
            std::memcpy(blob.data() + offset, &entry, sizeof(entry));
            offset += sizeof(entry);
        }
        return blob;
    }

  private:
    struct process_string
    {
        std::string text;
        std::uint32_t offset;
    };

    rule_action fallback = rule_action::permit;
    std::vector<process_string> process_strings;
    std::vector<process_selector> selectors;
    std::vector<network_prefix> network_prefixes;
    std::vector<port_range> ports;
    std::vector<rule> rules;
};

} // namespace wfp_test
