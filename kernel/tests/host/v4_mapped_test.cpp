#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <initializer_list>

#include "v4_mapped.h"

namespace
{

using winforward::wfp::try_normalize_v4_mapped;

constexpr std::uint8_t ipv4_family = 4;
constexpr std::uint8_t ipv6_family = 6;
constexpr std::uint8_t refusal_family = 0xAB;
constexpr std::uint8_t untouched_byte = 0xCD;

[[nodiscard]] std::array<std::uint8_t, 16> make_address(std::initializer_list<std::uint8_t> bytes)
{
    std::array<std::uint8_t, 16> address{};
    std::size_t index = 0;
    for (const std::uint8_t byte : bytes)
    {
        if (index < address.size())
        {
            address[index] = byte;
        }
        ++index;
    }
    return address;
}

[[nodiscard]] std::array<std::uint8_t, 16>
make_mapped(std::uint8_t first, std::uint8_t second, std::uint8_t third, std::uint8_t fourth)
{
    return make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, first, second, third, fourth });
}

void expect_normalized(const std::array<std::uint8_t, 16>& address,
                       std::uint8_t first,
                       std::uint8_t second,
                       std::uint8_t third,
                       std::uint8_t fourth)
{
    std::uint8_t family = refusal_family;
    std::array<std::uint8_t, 16> output{};
    output.fill(untouched_byte);

    ASSERT_TRUE(try_normalize_v4_mapped(ipv6_family, address.data(), family, output.data()));
    EXPECT_EQ(family, ipv4_family);
    EXPECT_EQ(output[0], first);
    EXPECT_EQ(output[1], second);
    EXPECT_EQ(output[2], third);
    EXPECT_EQ(output[3], fourth);
    for (std::size_t index = 4; index < output.size(); ++index)
    {
        EXPECT_EQ(output[index], 0) << "index=" << index;
    }
}

void expect_refused(std::uint8_t family, const std::array<std::uint8_t, 16>& address)
{
    std::uint8_t out_family = refusal_family;
    std::array<std::uint8_t, 16> output{};
    output.fill(untouched_byte);

    EXPECT_FALSE(try_normalize_v4_mapped(family, address.data(), out_family, output.data()));
    EXPECT_EQ(out_family, refusal_family);
    for (const std::uint8_t byte : output)
    {
        EXPECT_EQ(byte, untouched_byte);
    }
}

} // namespace

// host TCP 与 host UDP 共用同一条 classify，两个不同的 v4-mapped 远端都必须归一。
TEST(v4_mapped, normalizes_a_mapped_remote)
{
    expect_normalized(make_mapped(203, 0, 113, 7), 203, 0, 113, 7);
    expect_normalized(make_mapped(127, 0, 0, 1), 127, 0, 0, 1);
}

TEST(v4_mapped, keeps_a_native_ipv4_remote)
{
    expect_refused(ipv4_family, make_address({ 127, 0, 0, 1 }));
}

TEST(v4_mapped, keeps_a_native_ipv6_remote)
{
    expect_refused(ipv6_family, make_address({ 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }));
}

TEST(v4_mapped, refuses_the_unspecified_and_loopback_addresses)
{
    expect_refused(ipv6_family, make_address({}));
    expect_refused(ipv6_family, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }));
}

// 字节 10..11 必须同时是 0xFF；差一个字节就落在 ::ffff:0:0/96 之外。
TEST(v4_mapped, requires_both_marker_bytes_to_be_ff)
{
    expect_refused(ipv6_family, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFE, 127, 0, 0, 1 }));
    expect_refused(ipv6_family, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0xFF, 127, 0, 0, 1 }));
}

// FF FF 放在字节 11..12（晚一个字节）：前缀全零也会被标记检查拒掉。
TEST(v4_mapped, requires_the_marker_bytes_at_the_right_offset)
{
    expect_refused(ipv6_family, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 127, 0, 1 }));
}

// ::1:ffff:127.0.0.1：前 10 字节里出现非零字节就落在 ::ffff:0:0/96 之外。
TEST(v4_mapped, requires_a_zero_96_bit_prefix)
{
    expect_refused(ipv6_family, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0xFF, 127, 0, 0, 1 }));
}

TEST(v4_mapped, requires_the_ipv6_family)
{
    expect_refused(ipv4_family, make_mapped(127, 0, 0, 1));
}
