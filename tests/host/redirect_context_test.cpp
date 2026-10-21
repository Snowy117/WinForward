#include <gtest/gtest.h>

#include "redirect_context.h"

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
