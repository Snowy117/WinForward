#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#include "rule_blob.h"

// 测试专用的规则 blob 构造器，不属于产品头。
// 索引由构造器按"池内偏移"返回；进程串按加入顺序连续排列，网段条目按加入顺序
// 连续排列，各段起点 4 字节对齐，构造结果与 rule_blob.h 描述的 v1 布局一致。

namespace wfp_test
{

using winforward::wfp::network_prefix;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_blob_header;

inline constexpr std::size_t blob_alignment = alignof(rule);

[[nodiscard]] inline std::size_t align_up(std::size_t value, std::size_t alignment) noexcept
{
    return (value + alignment - 1U) & ~(alignment - 1U);
}

[[nodiscard]] inline bool same_network_prefix(const network_prefix& left, const network_prefix& right) noexcept
{
    if (left.prefix_length != right.prefix_length || left.family != right.family)
    {
        return false;
    }
    if (left.reserved[0] != right.reserved[0] || left.reserved[1] != right.reserved[1])
    {
        return false;
    }
    for (std::size_t index = 0; index < winforward::wfp::network_address_size; ++index)
    {
        if (left.network[index] != right.network[index])
        {
            return false;
        }
    }
    return true;
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
    rule entry{};
    const std::size_t offset = sizeof(rule_blob_header) + index * sizeof(entry);
    std::memcpy(&entry, blob.data() + offset, sizeof(entry));
    return entry;
}

inline void write_rule(std::vector<std::uint8_t>& blob, std::size_t index, const rule& entry) noexcept
{
    const std::size_t offset = sizeof(rule_blob_header) + index * sizeof(entry);
    std::memcpy(blob.data() + offset, &entry, sizeof(entry));
}

[[nodiscard]] inline bool open_blob(const std::vector<std::uint8_t>& blob, winforward::wfp::blob_view& view) noexcept
{
    return winforward::wfp::try_open_blob(blob.data(), static_cast<std::uint32_t>(blob.size()), view);
}

class rule_blob_builder
{
  public:
    void set_fallback_action(rule_action action)
    {
        fallback = action;
    }

    std::uint32_t add_process(std::string_view path)
    {
        std::uint32_t offset = 0;
        for (const process_string& entry : process_strings)
        {
            if (std::string_view(entry.text) == path)
            {
                return entry.offset;
            }
            offset += static_cast<std::uint32_t>(entry.text.size() + 1);
        }
        process_strings.push_back(process_string{ .text = std::string(path), .offset = offset });
        return offset;
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
        for (std::size_t index = 0; index < network_prefixes.size(); ++index)
        {
            if (same_network_prefix(network_prefixes[index], prefix))
            {
                return static_cast<std::uint32_t>(index * winforward::wfp::network_prefix_size);
            }
        }
        network_prefixes.push_back(prefix);
        return static_cast<std::uint32_t>((network_prefixes.size() - 1) * winforward::wfp::network_prefix_size);
    }

    void add_rule(const rule& entry)
    {
        rules.push_back(entry);
    }

    [[nodiscard]] std::vector<std::uint8_t> build() const
    {
        const std::size_t rules_offset = align_up(sizeof(rule_blob_header), blob_alignment);
        const std::size_t rules_end = rules_offset + rules.size() * sizeof(rule);
        const std::size_t process_offset = align_up(rules_end, blob_alignment);
        std::size_t process_size = 0;
        for (const process_string& entry : process_strings)
        {
            process_size += entry.text.size() + 1;
        }
        const std::size_t network_offset = align_up(process_offset + process_size, blob_alignment);
        const std::size_t network_size = network_prefixes.size() * sizeof(network_prefix);
        const std::size_t total_size = network_offset + network_size;

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
        header.network_pool_offset = static_cast<std::uint32_t>(network_offset);
        header.network_pool_size = static_cast<std::uint32_t>(network_size);
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

        offset = network_offset;
        for (const network_prefix& entry : network_prefixes)
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
    std::vector<network_prefix> network_prefixes;
    std::vector<rule> rules;
};

} // namespace wfp_test
