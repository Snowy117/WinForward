#include <gtest/gtest.h>

#include <array>
#include <cstdint>

#include "forwarded_fragment_table.h"

namespace
{

using winforward::wfp::forwarded_fragment_capacity;
using winforward::wfp::forwarded_fragment_key;
using winforward::wfp::forwarded_fragment_lifetime_100ns;
using winforward::wfp::forwarded_fragment_matches;
using winforward::wfp::forwarded_fragment_remember;
using winforward::wfp::forwarded_fragment_slot;

forwarded_fragment_key make_key(std::uint32_t identification, std::uint32_t ingress = 5)
{
    forwarded_fragment_key key{};
    key.family = 4;
    key.protocol = 17;
    key.ingress_adapter_id = ingress;
    key.identification = identification;
    key.source_address[0] = 192;
    key.source_address[1] = 168;
    key.source_address[2] = 100;
    key.source_address[3] = 4;
    key.destination_address[0] = 192;
    key.destination_address[1] = 168;
    key.destination_address[2] = 100;
    key.destination_address[3] = 99;
    return key;
}

TEST(forwarded_fragment_table, matches_same_key_within_lifetime)
{
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    std::uint32_t cursor = 0;
    const forwarded_fragment_key key = make_key(0xB6A1);
    forwarded_fragment_remember(slots.data(), cursor, key, 1000);
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), key, 1000));
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), key, 1000 + forwarded_fragment_lifetime_100ns));
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), key, 1001 + forwarded_fragment_lifetime_100ns));
}

TEST(forwarded_fragment_table, ingress_is_part_of_the_key)
{
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    std::uint32_t cursor = 0;
    forwarded_fragment_remember(slots.data(), cursor, make_key(0xB6A1, 5), 1000);
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), make_key(0xB6A1, 5), 1000));
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), make_key(0xB6A1, 6), 1000));
}

TEST(forwarded_fragment_table, key_fields_distinguish_flows)
{
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    std::uint32_t cursor = 0;
    forwarded_fragment_remember(slots.data(), cursor, make_key(0xB6A1), 1000);

    forwarded_fragment_key other = make_key(0xB6A2);
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), other, 1000));
    other = make_key(0xB6A1);
    other.protocol = 6;
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), other, 1000));
    other = make_key(0xB6A1);
    other.family = 6;
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), other, 1000));
    other = make_key(0xB6A1);
    other.source_address[3] = 5;
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), other, 1000));
    other = make_key(0xB6A1);
    other.destination_address[3] = 98;
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), other, 1000));
}

TEST(forwarded_fragment_table, wraps_after_capacity_and_evicts_oldest)
{
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    std::uint32_t cursor = 0;
    for (std::uint32_t index = 0; index < forwarded_fragment_capacity; ++index)
    {
        forwarded_fragment_remember(slots.data(), cursor, make_key(index), 1000);
    }
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), make_key(0), 1000));
    forwarded_fragment_remember(slots.data(), cursor, make_key(forwarded_fragment_capacity), 1000);
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), make_key(0), 1000));
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), make_key(1), 1000));
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), make_key(forwarded_fragment_capacity), 1000));
}

TEST(forwarded_fragment_table, empty_table_has_no_match_for_non_first_foreign_fragments)
{
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), make_key(0xB6A1), 1000));
    EXPECT_FALSE(forwarded_fragment_matches(slots.data(), make_key(0xB6A1), 0));
}

TEST(forwarded_fragment_table, ip_id_collision_is_inherent_and_documented)
{
    // IPv4 ID 只有 16 位；同一入口/地址/协议下，两条流的同一 ID 会互相跟随。这里把它钉成
    // 已知行为，避免以后误以为表能区分端口（非首片本来就没有端口）。
    std::array<forwarded_fragment_slot, forwarded_fragment_capacity> slots{};
    std::uint32_t cursor = 0;
    forwarded_fragment_remember(slots.data(), cursor, make_key(0x1234), 1000);
    EXPECT_TRUE(forwarded_fragment_matches(slots.data(), make_key(0x1234), 1000));
}

} // namespace
