#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

#include "forwarded_blob.h"

// 测试专用的 forwarded blob 构造器，不属于产品头。
// 网段索引由构造器按"池内字节偏移"返回；网段条目按加入顺序连续排列，规则数组与网段池
// 起点 4 字节对齐，构造结果与 forwarded_blob.h 描述的 v1 布局一致。

namespace wfp_test
{

using winforward::wfp::forwarded_blob_header;
using winforward::wfp::forwarded_blob_rule;
using winforward::wfp::network_prefix;
using winforward::wfp::rule_action;

inline constexpr std::size_t forwarded_blob_alignment = alignof(forwarded_blob_rule);

[[nodiscard]] inline std::size_t forwarded_align_up(std::size_t value, std::size_t alignment) noexcept
{
    return (value + alignment - 1U) & ~(alignment - 1U);
}

[[nodiscard]] inline bool same_forwarded_network_prefix(const network_prefix& left,
                                                        const network_prefix& right) noexcept
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

[[nodiscard]] inline forwarded_blob_header read_forwarded_header(const std::vector<std::uint8_t>& blob) noexcept
{
    forwarded_blob_header header{};
    std::memcpy(&header, blob.data(), sizeof(header));
    return header;
}

inline void write_forwarded_header(std::vector<std::uint8_t>& blob, const forwarded_blob_header& header) noexcept
{
    std::memcpy(blob.data(), &header, sizeof(header));
}

[[nodiscard]] inline forwarded_blob_rule read_forwarded_rule(const std::vector<std::uint8_t>& blob,
                                                             std::size_t index) noexcept
{
    forwarded_blob_rule entry{};
    const std::size_t offset = sizeof(forwarded_blob_header) + index * sizeof(entry);
    std::memcpy(&entry, blob.data() + offset, sizeof(entry));
    return entry;
}

inline void
write_forwarded_rule(std::vector<std::uint8_t>& blob, std::size_t index, const forwarded_blob_rule& entry) noexcept
{
    const std::size_t offset = sizeof(forwarded_blob_header) + index * sizeof(entry);
    std::memcpy(blob.data() + offset, &entry, sizeof(entry));
}

[[nodiscard]] inline bool open_forwarded_blob(const std::vector<std::uint8_t>& blob,
                                              winforward::wfp::forwarded_blob_view& view) noexcept
{
    return winforward::wfp::try_open_forwarded_blob(blob.data(), static_cast<std::uint32_t>(blob.size()), view);
}

class forwarded_blob_builder
{
  public:
    void set_fallback_action(rule_action action)
    {
        fallback = action;
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
            if (same_forwarded_network_prefix(network_prefixes[index], prefix))
            {
                return static_cast<std::uint32_t>(index * winforward::wfp::network_prefix_size);
            }
        }
        network_prefixes.push_back(prefix);
        return static_cast<std::uint32_t>((network_prefixes.size() - 1) * winforward::wfp::network_prefix_size);
    }

    void add_rule(const forwarded_blob_rule& entry)
    {
        rules.push_back(entry);
    }

    [[nodiscard]] std::vector<std::uint8_t> build() const
    {
        const std::size_t rules_offset = forwarded_align_up(sizeof(forwarded_blob_header), forwarded_blob_alignment);
        const std::size_t rules_end = rules_offset + rules.size() * sizeof(forwarded_blob_rule);
        const std::size_t network_offset = forwarded_align_up(rules_end, forwarded_blob_alignment);
        const std::size_t network_size = network_prefixes.size() * sizeof(network_prefix);
        const std::size_t total_size = network_offset + network_size;

        std::vector<std::uint8_t> blob(total_size, 0);

        forwarded_blob_header header{};
        header.magic = winforward::wfp::forwarded_blob_magic;
        header.version = winforward::wfp::forwarded_blob_version;
        header.header_size = static_cast<std::uint16_t>(sizeof(forwarded_blob_header));
        header.total_size = static_cast<std::uint32_t>(total_size);
        header.rule_count = static_cast<std::uint32_t>(rules.size());
        header.fallback_action = static_cast<std::uint32_t>(fallback);
        header.rules_offset = static_cast<std::uint32_t>(rules_offset);
        header.network_pool_offset = static_cast<std::uint32_t>(network_offset);
        header.network_pool_size = static_cast<std::uint32_t>(network_size);
        write_forwarded_header(blob, header);

        std::size_t offset = rules_offset;
        for (const forwarded_blob_rule& entry : rules)
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
        return blob;
    }

  private:
    rule_action fallback = rule_action::permit;
    std::vector<network_prefix> network_prefixes;
    std::vector<forwarded_blob_rule> rules;
};

} // namespace wfp_test
