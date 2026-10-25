#pragma once

// forwarded UDP 分片跟随表：首片命中 redirect 后记录 datagram 的 (入口, 族, 协议, 源/目的地址,
// IPv4 ID / IPv6 fragment ID)，后续非首片没有端口，按这个键查表决定是否 block。
//
// 边界（与内核侧保持一致）：
// - 固定 256 项轮转；写满后覆盖最旧项，不做 LRU。
// - 2 秒有效期（100ns 单位）；过期项不匹配，也不主动清理。
// - 键包含入口 adapter；不同入口不互相跟随。
// - IP ID 只有 16 位（IPv4），同一入口/地址/协议下跨流碰撞会误 block 另一条流的分片，这是协议
//   本身的限制；首片后到或分片丢失时也不会有跟随项（放行）。
#include <cstdint>

namespace winforward::wfp
{

constexpr std::uint32_t forwarded_fragment_capacity = 256;
constexpr std::uint64_t forwarded_fragment_lifetime_100ns = 20'000'000ULL; // 2 s

struct forwarded_fragment_key
{
    std::uint8_t family;
    std::uint8_t protocol;
    std::uint32_t ingress_adapter_id;
    std::uint32_t identification;
    std::uint8_t source_address[16];
    std::uint8_t destination_address[16];
};

struct forwarded_fragment_slot
{
    forwarded_fragment_key key;
    std::uint64_t observed_tick;
};

[[nodiscard]] inline bool forwarded_fragment_address_equal(const std::uint8_t* left,
                                                           const std::uint8_t* right,
                                                           std::uint32_t address_size) noexcept
{
    for (std::uint32_t index = 0; index < address_size; ++index)
    {
        if (left[index] != right[index])
        {
            return false;
        }
    }
    return true;
}

[[nodiscard]] inline bool forwarded_fragment_key_equal(const forwarded_fragment_key& left,
                                                       const forwarded_fragment_key& right) noexcept
{
    if (left.family != right.family || left.protocol != right.protocol ||
        left.ingress_adapter_id != right.ingress_adapter_id || left.identification != right.identification)
    {
        return false;
    }
    const std::uint32_t address_size = left.family == 6 ? 16U : 4U;
    return forwarded_fragment_address_equal(left.source_address, right.source_address, address_size) &&
           forwarded_fragment_address_equal(left.destination_address, right.destination_address, address_size);
}

inline void forwarded_fragment_remember(forwarded_fragment_slot* slots,
                                        std::uint32_t& cursor,
                                        const forwarded_fragment_key& key,
                                        std::uint64_t now) noexcept
{
    forwarded_fragment_slot& slot = slots[cursor % forwarded_fragment_capacity];
    ++cursor;
    slot.key = key;
    slot.observed_tick = now;
}

[[nodiscard]] inline bool forwarded_fragment_matches(const forwarded_fragment_slot* slots,
                                                     const forwarded_fragment_key& key,
                                                     std::uint64_t now) noexcept
{
    for (std::uint32_t index = 0; index < forwarded_fragment_capacity; ++index)
    {
        const forwarded_fragment_slot& slot = slots[index];
        if (now - slot.observed_tick > forwarded_fragment_lifetime_100ns)
        {
            continue;
        }
        if (forwarded_fragment_key_equal(slot.key, key))
        {
            return true;
        }
    }
    return false;
}

} // namespace winforward::wfp
