#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <vector>

#include "matcher.h"
#include "matcher_test_support.h"
#include "rule_blob_builder.h"
#include "v4_mapped.h"

namespace
{

using namespace wfp_matcher_test;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_pool_index_any;

TEST(matcher, fields_are_anded)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "app.exe", file_name } });
            set_networks(builder, entry, { { 4, 8, make_ipv4(10, 0, 0, 0) } });
            set_ports(builder, entry, { { 80, 80 } });
        });

    EXPECT_TRUE(match_connection(
                    blob, make_connection("app.exe", make_ipv4(10, 1, 2, 3), 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(
        match_connection(blob,
                         make_connection("app.exe", make_ipv4(192, 168, 1, 1), 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(match_connection(
                     blob, make_connection("app.exe", make_ipv4(10, 1, 2, 3), 81, tcp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_FALSE(match_connection(
                     blob, make_connection("other.exe", make_ipv4(10, 1, 2, 3), 80, tcp_protocol, address_family::ipv4))
                     .matched);
}

TEST(matcher, network_array_is_or_across_families)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_networks(builder,
                         entry,
                         { { 4, 8, make_ipv4(10, 0, 0, 0) }, { 6, 32, make_address({ 0x20, 0x01, 0x0d, 0xB8 }) } });
            entry.family_mask = winforward::wfp::rule_family_mask_all;
        });

    EXPECT_TRUE(match_connection(
                    blob, make_connection("app.exe", make_ipv4(10, 1, 2, 3), 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(
                "app.exe", make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 1 }), 80, tcp_protocol, address_family::ipv6))
            .matched);
    EXPECT_FALSE(match_connection(
                     blob, make_connection("app.exe", make_ipv4(8, 8, 8, 8), 80, tcp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(
                "app.exe", make_address({ 0x20, 0x01, 0x0d, 0xB9, 0, 1 }), 80, tcp_protocol, address_family::ipv6))
            .matched);
}

TEST(matcher, port_array_is_or_with_inclusive_endpoints)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_ports(builder, entry, { { 80, 80 }, { 443, 443 }, { 8000, 8100 } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 80, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 443, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 8000, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 8100, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 8050, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_FALSE(
        match_connection(blob, make_connection("app.exe", address, 7999, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_FALSE(
        match_connection(blob, make_connection("app.exe", address, 8101, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_FALSE(
        match_connection(blob, make_connection("app.exe", address, 9000, tcp_protocol, address_family::ipv4)).matched);
}

TEST(matcher, protocol_mask_selects_tcp_and_udp)
{
    const auto tcp_only = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.protocol_mask = winforward::wfp::rule_protocol_mask_tcp;
        });
    const auto udp_only = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.protocol_mask = winforward::wfp::rule_protocol_mask_udp;
        });
    const auto both = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.protocol_mask = winforward::wfp::rule_protocol_mask_all;
        });
    const auto any = build_blob(
        [](wfp_test::rule_blob_builder&, rule&)
        {
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    const auto tcp = make_connection("app.exe", address, 80, tcp_protocol, address_family::ipv4);
    const auto udp = make_connection("app.exe", address, 80, udp_protocol, address_family::ipv4);
    const auto other = make_connection("app.exe", address, 80, 1, address_family::ipv4);

    EXPECT_TRUE(match_connection(tcp_only, tcp).matched);
    EXPECT_FALSE(match_connection(tcp_only, udp).matched);
    EXPECT_TRUE(match_connection(udp_only, udp).matched);
    EXPECT_FALSE(match_connection(udp_only, tcp).matched);
    EXPECT_TRUE(match_connection(both, tcp).matched);
    EXPECT_TRUE(match_connection(both, udp).matched);
    EXPECT_TRUE(match_connection(any, tcp).matched);
    EXPECT_TRUE(match_connection(any, udp).matched);
    EXPECT_FALSE(match_connection(any, other).matched);
    EXPECT_FALSE(match_connection(both, other).matched);
}

TEST(matcher, family_mask_selects_ipv4_and_ipv6)
{
    const auto ipv4_only = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.family_mask = winforward::wfp::rule_family_mask_ipv4;
        });
    const auto ipv6_only = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.family_mask = winforward::wfp::rule_family_mask_ipv6;
        });
    const auto both = build_blob(
        [](wfp_test::rule_blob_builder&, rule& entry)
        {
            entry.family_mask = winforward::wfp::rule_family_mask_all;
        });
    const auto any = build_blob(
        [](wfp_test::rule_blob_builder&, rule&)
        {
        });

    const auto v4 = make_connection("app.exe", make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4);
    const auto v6 =
        make_connection("app.exe", make_address({ 0x20, 0x01, 0x0d, 0xB8 }), 80, tcp_protocol, address_family::ipv6);

    EXPECT_TRUE(match_connection(ipv4_only, v4).matched);
    EXPECT_FALSE(match_connection(ipv4_only, v6).matched);
    EXPECT_TRUE(match_connection(ipv6_only, v6).matched);
    EXPECT_FALSE(match_connection(ipv6_only, v4).matched);
    EXPECT_TRUE(match_connection(both, v4).matched);
    EXPECT_TRUE(match_connection(both, v6).matched);
    EXPECT_TRUE(match_connection(any, v4).matched);
    EXPECT_TRUE(match_connection(any, v6).matched);
}

TEST(matcher, returns_the_first_matching_rule_and_its_index)
{
    wfp_test::rule_blob_builder builder;
    rule first = wfp_test::make_any_rule();
    first.port_first = builder.add_port(80, 80);
    first.port_count = 1;
    first.action = rule_action::redirect;
    rule second = wfp_test::make_any_rule();
    second.action = rule_action::block;
    builder.add_rule(first);
    builder.add_rule(second);
    const std::vector<std::uint8_t> blob = builder.build();

    const match_result on_80 = match_connection(
        blob, make_connection("app.exe", make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(on_80.matched);
    EXPECT_EQ(on_80.action, rule_action::redirect);
    EXPECT_EQ(on_80.rule_index, 0U);

    const match_result on_443 = match_connection(
        blob, make_connection("app.exe", make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(on_443.matched);
    EXPECT_EQ(on_443.action, rule_action::block);
    EXPECT_EQ(on_443.rule_index, 1U);
}

TEST(matcher, falls_back_when_no_rule_matches)
{
    wfp_test::rule_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    rule entry = wfp_test::make_any_rule();
    entry.port_first = builder.add_port(80, 80);
    entry.port_count = 1;
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(), make_connection("app.exe", make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
}

TEST(matcher, matches_an_empty_rule_table)
{
    const wfp_test::rule_blob_builder builder;
    const match_result result = match_connection(
        builder.build(), make_connection("app.exe", make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::permit);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
}

// v4-mapped 双模连接在 classify 里归一成 IPv4 之后才进 matcher：IPv4 网段命中、v4-mapped 的
// IPv6 网段不命中。同一条 IPv6 网段对未归一的原始视图仍然命中，证明差异来自归一而不是 blob。
TEST(matcher, normalized_v4_mapped_view_matches_ipv4_networks_only)
{
    const auto ipv4_cidr = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            entry.family_mask = winforward::wfp::rule_family_mask_ipv4;
            set_networks(builder, entry, { { 4, 8, make_ipv4(127, 0, 0, 0) } });
        });
    const auto ipv6_mapped_cidr = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_networks(builder,
                         entry,
                         { { 6, 104, make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 127, 0, 0, 0 }) } });
        });
    const auto ipv6_native_cidr = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_networks(builder, entry, { { 6, 32, make_address({ 0x20, 0x01, 0x0D, 0xB8 }) } });
        });

    const std::array<std::uint8_t, 16> mapped =
        make_address({ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 127, 0, 0, 1 });
    const auto raw_view = make_connection("app.exe", mapped, 80, tcp_protocol, address_family::ipv6);
    EXPECT_TRUE(match_connection(ipv6_mapped_cidr, raw_view).matched);

    std::uint8_t family = 0;
    std::array<std::uint8_t, 16> normalized{};
    ASSERT_TRUE(winforward::wfp::try_normalize_v4_mapped(6, mapped.data(), family, normalized.data()));
    const auto normalized_view =
        make_connection("app.exe", normalized, 80, tcp_protocol, static_cast<address_family>(family));
    EXPECT_TRUE(match_connection(ipv4_cidr, normalized_view).matched);
    EXPECT_FALSE(match_connection(ipv6_mapped_cidr, normalized_view).matched);

    const auto native_view =
        make_connection("app.exe", make_address({ 0x20, 0x01, 0x0D, 0xB8 }), 80, tcp_protocol, address_family::ipv6);
    EXPECT_TRUE(match_connection(ipv6_native_cidr, native_view).matched);
    EXPECT_FALSE(match_connection(ipv4_cidr, native_view).matched);
}

} // namespace
