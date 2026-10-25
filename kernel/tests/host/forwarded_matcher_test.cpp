#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <string>
#include <vector>

#include "forwarded_blob_builder.h"
#include "forwarded_matcher.h"

namespace
{

using winforward::wfp::address_family;
using winforward::wfp::forwarded_blob_rule;
using winforward::wfp::forwarded_blob_view;
using winforward::wfp::forwarded_connection_view;
using winforward::wfp::forwarded_match_result;
using winforward::wfp::network_prefix;
using winforward::wfp::rule_action;
using winforward::wfp::rule_pool_index_any;

constexpr std::uint8_t tcp_protocol = 6;
constexpr std::uint8_t udp_protocol = 17;

std::array<std::uint8_t, 16> make_address(std::initializer_list<std::uint8_t> bytes)
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

std::array<std::uint8_t, 16> make_ipv4(std::uint8_t first, std::uint8_t second, std::uint8_t third, std::uint8_t fourth)
{
    return make_address({ first, second, third, fourth });
}

forwarded_connection_view make_connection(std::uint32_t adapter,
                                          const std::array<std::uint8_t, 16>& client_address,
                                          const std::array<std::uint8_t, 16>& remote_address,
                                          std::uint16_t remote_port,
                                          std::uint8_t protocol,
                                          address_family family)
{
    forwarded_connection_view connection{};
    connection.ingress_adapter_id = adapter;
    for (std::size_t index = 0; index < 16; ++index)
    {
        connection.client_address[index] = client_address[index];
        connection.remote_address[index] = remote_address[index];
    }
    connection.remote_port = remote_port;
    connection.protocol = protocol;
    connection.family = family;
    return connection;
}

forwarded_blob_rule make_rule(std::uint32_t adapter, std::uint32_t client_index, std::uint32_t remote_index)
{
    forwarded_blob_rule entry{};
    entry.ingress_adapter_id = adapter;
    entry.client_network_index = client_index;
    entry.remote_network_index = remote_index;
    entry.remote_port_start = winforward::wfp::rule_port_any_begin;
    entry.remote_port_end = winforward::wfp::rule_port_any_end;
    entry.upstream_id = winforward::wfp::forwarded_upstream_none;
    entry.action = rule_action::permit;
    return entry;
}

forwarded_match_result match_blob(const std::vector<std::uint8_t>& blob, const forwarded_connection_view& connection)
{
    forwarded_blob_view view{};
    EXPECT_TRUE(wfp_test::open_forwarded_blob(blob, view));
    return winforward::wfp::match_forwarded(view, connection);
}

} // namespace

TEST(forwarded_matcher, matches_a_redirect_rule)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(
        7, builder.add_network(4, 24, make_ipv4(10, 0, 0, 0)), builder.add_network(4, 24, make_ipv4(203, 0, 113, 0)));
    entry.remote_port_start = 443;
    entry.remote_port_end = 443;
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.action = rule_action::redirect;
    entry.upstream_id = 4;
    builder.add_rule(entry);

    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(7, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 113, 5), 443, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::redirect);
    EXPECT_EQ(result.rule_index, 0U);
    EXPECT_EQ(result.upstream_id, 4U);
}

TEST(forwarded_matcher, matches_an_any_adapter_rule_on_every_adapter)
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_rule(make_rule(winforward::wfp::forwarded_adapter_any, rule_pool_index_any, rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result first = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result second = match_blob(
        blob,
        make_connection(999, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(first.matched);
    EXPECT_TRUE(second.matched);
}

TEST(forwarded_matcher, skips_a_rule_with_a_different_adapter)
{
    wfp_test::forwarded_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    builder.add_rule(make_rule(7, rule_pool_index_any, rule_pool_index_any));

    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(8, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
}

TEST(forwarded_matcher, matches_the_client_network)
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_rule(
        make_rule(rule_pool_index_any, builder.add_network(4, 24, make_ipv4(10, 0, 0, 0)), rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result inside = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 113, 5), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result outside = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 1, 9), make_ipv4(203, 0, 113, 5), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(inside.matched);
    EXPECT_FALSE(outside.matched);
}

TEST(forwarded_matcher, matches_the_remote_network)
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_rule(
        make_rule(rule_pool_index_any, rule_pool_index_any, builder.add_network(4, 24, make_ipv4(203, 0, 113, 0))));
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result inside =
        match_blob(blob,
                   make_connection(
                       1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 113, 200), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result outside = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 114, 1), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(inside.matched);
    EXPECT_FALSE(outside.matched);
}

TEST(forwarded_matcher, matches_both_networks_together)
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_rule(make_rule(rule_pool_index_any,
                               builder.add_network(4, 24, make_ipv4(10, 0, 0, 0)),
                               builder.add_network(4, 24, make_ipv4(203, 0, 113, 0))));
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result both = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 113, 5), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result client_only = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 114, 5), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(both.matched);
    EXPECT_FALSE(client_only.matched);
}

TEST(forwarded_matcher, matches_the_port_range_inclusively)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    entry.remote_port_start = 100;
    entry.remote_port_end = 200;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    const auto matches = [&blob](std::uint16_t port)
    {
        return match_blob(
                   blob,
                   make_connection(
                       1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), port, tcp_protocol, address_family::ipv4))
            .matched;
    };
    EXPECT_TRUE(matches(100));
    EXPECT_TRUE(matches(150));
    EXPECT_TRUE(matches(200));
    EXPECT_FALSE(matches(99));
    EXPECT_FALSE(matches(201));
}

TEST(forwarded_matcher, matches_protocol_and_family)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    entry.family = winforward::wfp::rule_family_ipv4;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result tcp = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result udp = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, udp_protocol, address_family::ipv4));
    const forwarded_match_result ipv6 = match_blob(
        blob,
        make_connection(
            1, make_address({ 0x20, 0x01 }), make_address({ 0x20, 0x01 }), 80, tcp_protocol, address_family::ipv6));
    EXPECT_TRUE(tcp.matched);
    EXPECT_FALSE(udp.matched);
    EXPECT_FALSE(ipv6.matched);
}

TEST(forwarded_matcher, matches_an_ipv6_network)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(
        rule_pool_index_any, rule_pool_index_any, builder.add_network(6, 32, make_address({ 0x20, 0x01, 0x0d, 0xB8 })));
    entry.family = winforward::wfp::rule_family_ipv6;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result result =
        match_blob(blob,
                   make_connection(1,
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8 }),
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 0, 0, 0, 1 }),
                                   443,
                                   tcp_protocol,
                                   address_family::ipv6));
    EXPECT_TRUE(result.matched);
}

TEST(forwarded_matcher, requires_the_network_family_to_match_the_connection)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(
        rule_pool_index_any, rule_pool_index_any, builder.add_network(6, 32, make_address({ 0x20, 0x01, 0x0d, 0xB8 })));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    // 规则族是 any；网段族仍必须与连接事实一致，否则字节比较没有意义。
    const forwarded_match_result ipv6 = match_blob(blob,
                                                   make_connection(1,
                                                                   make_address({ 0x20, 0x01 }),
                                                                   make_address({ 0x20, 0x01, 0x0d, 0xB8 }),
                                                                   443,
                                                                   tcp_protocol,
                                                                   address_family::ipv6));
    const forwarded_match_result ipv4 = match_blob(
        blob,
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(203, 0, 113, 5), 443, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(ipv6.matched);
    EXPECT_FALSE(ipv4.matched);
}

TEST(forwarded_matcher, matches_a_non_byte_aligned_prefix)
{
    wfp_test::forwarded_blob_builder builder;
    const std::uint32_t client_index = builder.add_network(4, 25, make_ipv4(192, 0, 2, 0));
    builder.add_rule(make_rule(winforward::wfp::forwarded_adapter_any, client_index, rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    // /25 的最后一个字节只比较最高位：192.0.2.127 在内，192.0.2.128 在外。
    const forwarded_match_result inside = match_blob(
        blob,
        make_connection(
            1, make_ipv4(192, 0, 2, 127), make_ipv4(203, 0, 113, 5), 80, tcp_protocol, address_family::ipv4));
    const forwarded_match_result outside = match_blob(
        blob,
        make_connection(
            1, make_ipv4(192, 0, 2, 128), make_ipv4(203, 0, 113, 5), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(inside.matched);
    EXPECT_FALSE(outside.matched);
}

TEST(forwarded_matcher, matches_an_ipv6_client_network)
{
    wfp_test::forwarded_blob_builder builder;
    const std::uint32_t client_index = builder.add_network(6, 48, make_address({ 0x20, 0x01, 0x0d, 0xB8, 0x00, 0x01 }));
    builder.add_rule(make_rule(winforward::wfp::forwarded_adapter_any, client_index, rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    const forwarded_match_result inside =
        match_blob(blob,
                   make_connection(1,
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8, 0x00, 0x01 }),
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8, 0x00, 0x02 }),
                                   443,
                                   tcp_protocol,
                                   address_family::ipv6));
    const forwarded_match_result outside =
        match_blob(blob,
                   make_connection(1,
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8, 0x00, 0x02 }),
                                   make_address({ 0x20, 0x01, 0x0d, 0xB8, 0x00, 0x02 }),
                                   443,
                                   tcp_protocol,
                                   address_family::ipv6));
    EXPECT_TRUE(inside.matched);
    EXPECT_FALSE(outside.matched);
}

TEST(forwarded_matcher, returns_the_first_matching_rule)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule first = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    first.action = rule_action::redirect;
    first.upstream_id = 9;
    builder.add_rule(first);
    forwarded_blob_rule second = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    second.action = rule_action::block;
    builder.add_rule(second);

    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::redirect);
    EXPECT_EQ(result.rule_index, 0U);
    EXPECT_EQ(result.upstream_id, 9U);
}

TEST(forwarded_matcher, falls_back_when_no_rule_matches)
{
    wfp_test::forwarded_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    forwarded_blob_rule entry = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    entry.remote_port_start = 80;
    entry.remote_port_end = 80;
    builder.add_rule(entry);

    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
    EXPECT_EQ(result.upstream_id, winforward::wfp::forwarded_upstream_none);
}

TEST(forwarded_matcher, matches_an_empty_rule_table)
{
    const wfp_test::forwarded_blob_builder builder;
    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::permit);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
    EXPECT_EQ(result.upstream_id, winforward::wfp::forwarded_upstream_none);
}

TEST(forwarded_matcher, reports_no_upstream_for_a_matched_block_rule)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_rule(rule_pool_index_any, rule_pool_index_any, rule_pool_index_any);
    entry.action = rule_action::block;
    builder.add_rule(entry);

    const forwarded_match_result result = match_blob(
        builder.build(),
        make_connection(1, make_ipv4(10, 0, 0, 9), make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.upstream_id, winforward::wfp::forwarded_upstream_none);
}
