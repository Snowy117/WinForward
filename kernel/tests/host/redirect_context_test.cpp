#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>

#include "redirect_context.h"
#include "v4_mapped.h"

namespace wfp = winforward::wfp;

namespace
{

constexpr wfp::original_destination make_ipv4_destination() noexcept
{
    wfp::original_destination destination{};
    destination.family = wfp::address_family::ipv4;
    destination.protocol = wfp::transport_protocol::tcp;
    destination.port = 443;
    destination.address[0] = 203;
    destination.address[1] = 0;
    destination.address[2] = 113;
    destination.address[3] = 7;
    return destination;
}

} // namespace

// 编译期往返是这组 constexpr 设计的验收点：内核侧构造、用户态侧解析都必须能在常量求值里完成，
// 这样两侧的编码逻辑在编译期就相互校验过一遍，而不是等到上机。
static_assert(wfp::is_redirect_context_valid(wfp::make_redirect_context(make_ipv4_destination(), 4242, 3)));

TEST(redirect_context, round_trips_an_ipv4_destination)
{
    const auto context = wfp::make_redirect_context(make_ipv4_destination(), 4242, 3);

    wfp::original_destination recovered{};
    ASSERT_TRUE(wfp::try_read_original_destination(context, recovered));

    EXPECT_EQ(recovered.family, wfp::address_family::ipv4);
    EXPECT_EQ(recovered.protocol, wfp::transport_protocol::tcp);
    EXPECT_EQ(recovered.port, 443);
    EXPECT_EQ(recovered.address[0], 203);
    EXPECT_EQ(recovered.address[3], 7);
    EXPECT_EQ(context.process_id, 4242U);
    EXPECT_EQ(context.rule_index, 3U);
}

TEST(redirect_context, round_trips_an_ipv6_destination)
{
    wfp::original_destination destination{};
    destination.family = wfp::address_family::ipv6;
    destination.protocol = wfp::transport_protocol::tcp;
    destination.port = 8443;
    destination.address[0] = 0x20;
    destination.address[1] = 0x01;
    destination.address[15] = 0x99;

    wfp::original_destination recovered{};
    ASSERT_TRUE(wfp::try_read_original_destination(wfp::make_redirect_context(destination, 1, 0), recovered));

    EXPECT_EQ(recovered.family, wfp::address_family::ipv6);
    EXPECT_EQ(recovered.port, 8443);
    EXPECT_EQ(recovered.address[0], 0x20);
    EXPECT_EQ(recovered.address[15], 0x99);
}

TEST(redirect_context, rejects_a_wrong_magic)
{
    auto context = wfp::make_redirect_context(make_ipv4_destination(), 1, 0);
    context.magic = 0;

    wfp::original_destination recovered{};
    EXPECT_FALSE(wfp::try_read_original_destination(context, recovered));
}

TEST(redirect_context, rejects_a_foreign_version)
{
    auto context = wfp::make_redirect_context(make_ipv4_destination(), 1, 0);
    context.version = static_cast<std::uint16_t>(wfp::redirect_context_version + 1);

    wfp::original_destination recovered{};
    EXPECT_FALSE(wfp::try_read_original_destination(context, recovered));
}

TEST(redirect_context, rejects_an_unknown_address_family)
{
    auto context = wfp::make_redirect_context(make_ipv4_destination(), 1, 0);
    context.family = static_cast<wfp::address_family>(0);

    wfp::original_destination recovered{};
    EXPECT_FALSE(wfp::try_read_original_destination(context, recovered));
}

TEST(redirect_context, round_trips_a_udp_destination)
{
    wfp::original_destination destination = make_ipv4_destination();
    destination.protocol = wfp::transport_protocol::udp;

    wfp::original_destination recovered{};
    ASSERT_TRUE(wfp::try_read_original_destination(wfp::make_redirect_context(destination, 7, 2), recovered));

    EXPECT_EQ(recovered.protocol, wfp::transport_protocol::udp);
    EXPECT_EQ(recovered.port, 443);
    EXPECT_EQ(recovered.address[0], 203);
}

TEST(redirect_context, rejects_an_unknown_transport_protocol)
{
    auto context = wfp::make_redirect_context(make_ipv4_destination(), 1, 0);
    context.protocol = static_cast<wfp::transport_protocol>(18);

    wfp::original_destination recovered{};
    EXPECT_FALSE(wfp::try_read_original_destination(context, recovered));
}

// 归一后的 v4-mapped 目的地写进 context 是 family 4 + 四个 IPv4 字节、尾部为零；context 的
// 尺寸与版本不随归一改变（用户态按 36 字节 / version 1 读取）。
TEST(redirect_context, carries_a_normalized_v4_mapped_destination)
{
    const std::array<std::uint8_t, 16> mapped = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 192, 0, 2, 10 };
    std::uint8_t family = 0;
    std::array<std::uint8_t, 16> normalized{};
    ASSERT_TRUE(wfp::try_normalize_v4_mapped(
        static_cast<std::uint8_t>(wfp::address_family::ipv6), mapped.data(), family, normalized.data()));

    wfp::original_destination destination{};
    destination.family = static_cast<wfp::address_family>(family);
    destination.protocol = wfp::transport_protocol::tcp;
    destination.port = 40012;
    for (std::size_t index = 0; index < normalized.size(); ++index)
    {
        destination.address[index] = normalized[index];
    }

    const auto context = wfp::make_redirect_context(destination, 4242, 3);
    wfp::original_destination recovered{};
    ASSERT_TRUE(wfp::try_read_original_destination(context, recovered));

    EXPECT_EQ(recovered.family, wfp::address_family::ipv4);
    EXPECT_EQ(recovered.port, 40012);
    EXPECT_EQ(recovered.address[0], 192);
    EXPECT_EQ(recovered.address[1], 0);
    EXPECT_EQ(recovered.address[2], 2);
    EXPECT_EQ(recovered.address[3], 10);
    for (std::size_t index = 4; index < wfp::address_storage_size; ++index)
    {
        EXPECT_EQ(recovered.address[index], 0) << "index=" << index;
    }
    EXPECT_EQ(context.family, wfp::address_family::ipv4);
    EXPECT_EQ(sizeof(wfp::redirect_context), 36U);
    EXPECT_EQ(wfp::redirect_context_version, 1);
}
