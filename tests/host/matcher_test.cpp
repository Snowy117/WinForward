#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <string>
#include <vector>

#include "matcher.h"
#include "rule_blob_builder.h"

namespace
{

using winforward::wfp::address_family;
using winforward::wfp::blob_view;
using winforward::wfp::connection_view;
using winforward::wfp::match_result;
using winforward::wfp::network_prefix;
using winforward::wfp::rule;
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

connection_view make_connection(const std::string& process_path,
                                const std::array<std::uint8_t, 16>& address,
                                std::uint16_t remote_port,
                                std::uint8_t protocol,
                                address_family family)
{
    connection_view connection{};
    connection.process_path = process_path.c_str();
    connection.process_path_length = static_cast<std::uint32_t>(process_path.size());
    for (std::size_t index = 0; index < address.size(); ++index)
    {
        connection.remote_address[index] = address[index];
    }
    connection.remote_port = remote_port;
    connection.protocol = protocol;
    connection.family = family;
    return connection;
}

rule make_rule(std::uint32_t process_index, std::uint32_t network_index)
{
    rule entry{};
    entry.process_index = process_index;
    entry.network_index = network_index;
    entry.remote_port_start = winforward::wfp::rule_port_any_begin;
    entry.remote_port_end = winforward::wfp::rule_port_any_end;
    entry.action = rule_action::permit;
    return entry;
}

match_result match_connection(const std::vector<std::uint8_t>& blob, const connection_view& connection)
{
    blob_view view{};
    EXPECT_TRUE(wfp_test::open_blob(blob, view));
    return winforward::wfp::match(view, connection);
}

} // namespace

TEST(matcher, matches_a_redirect_rule)
{
    const std::string process_path = R"(c:\windows\system32\curl.exe)";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(builder.add_process(process_path), builder.add_network(4, 24, make_ipv4(203, 0, 113, 0)));
    entry.remote_port_start = 443;
    entry.remote_port_end = 443;
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.action = rule_action::redirect;
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(),
        make_connection(process_path, make_ipv4(203, 0, 113, 7), 443, tcp_protocol, address_family::ipv4));

    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::redirect);
    EXPECT_EQ(result.rule_index, 0U);
}

TEST(matcher, matches_a_block_rule)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, rule_pool_index_any);
    entry.action = rule_action::block;
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.rule_index, 0U);
}

TEST(matcher, returns_the_first_matching_rule)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule first = make_rule(rule_pool_index_any, rule_pool_index_any);
    first.remote_port_start = 443;
    first.remote_port_end = 443;
    first.action = rule_action::redirect;
    rule second = make_rule(rule_pool_index_any, rule_pool_index_any);
    second.action = rule_action::block;
    builder.add_rule(first);
    builder.add_rule(second);

    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::redirect);
    EXPECT_EQ(result.rule_index, 0U);
}

TEST(matcher, skips_a_non_matching_rule_and_matches_the_next)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule first = make_rule(rule_pool_index_any, rule_pool_index_any);
    first.remote_port_start = 80;
    first.remote_port_end = 80;
    rule second = make_rule(rule_pool_index_any, rule_pool_index_any);
    second.action = rule_action::block;
    builder.add_rule(first);
    builder.add_rule(second);

    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.rule_index, 1U);
}

TEST(matcher, falls_back_when_no_rule_matches)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    rule entry = make_rule(rule_pool_index_any, rule_pool_index_any);
    entry.remote_port_start = 80;
    entry.remote_port_end = 80;
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 443, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::block);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
}

TEST(matcher, matches_an_empty_rule_table)
{
    const wfp_test::rule_blob_builder builder;
    const std::string process_path = "app.exe";
    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
    EXPECT_EQ(result.action, rule_action::permit);
    EXPECT_EQ(result.rule_index, rule_pool_index_any);
}

TEST(matcher, matches_the_process_path_case_insensitively)
{
    const std::string selector = R"(c:\windows\system32\curl.exe)";
    const std::string connection_path = R"(C:\Windows\System32\CURL.EXE)";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(builder.add_process(selector), rule_pool_index_any);
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(),
        make_connection(connection_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
    EXPECT_EQ(result.rule_index, 0U);
}

TEST(matcher, rejects_a_different_process_path)
{
    const std::string selector = "app.exe";
    const std::string connection_path = "apq.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(builder.add_process(selector), rule_pool_index_any);
    builder.add_rule(entry);

    const match_result result = match_connection(
        builder.build(),
        make_connection(connection_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4));
    EXPECT_FALSE(result.matched);
}

TEST(matcher, matches_any_process_when_there_is_no_selector)
{
    wfp_test::rule_blob_builder builder;
    builder.add_rule(make_rule(rule_pool_index_any, rule_pool_index_any));

    connection_view connection{};
    connection.process_path = nullptr;
    connection.process_path_length = 0;
    connection.remote_port = 80;
    connection.protocol = tcp_protocol;
    connection.family = address_family::ipv4;
    connection.remote_address[0] = 1;

    const match_result result = match_connection(builder.build(), connection);
    EXPECT_TRUE(result.matched);
}

TEST(matcher, rejects_a_missing_process_path)
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(builder.add_process("app.exe"), rule_pool_index_any);
    builder.add_rule(entry);

    connection_view connection{};
    connection.process_path = nullptr;
    connection.process_path_length = 0;
    connection.remote_port = 80;
    connection.protocol = tcp_protocol;
    connection.family = address_family::ipv4;

    const match_result result = match_connection(builder.build(), connection);
    EXPECT_FALSE(result.matched);
}

TEST(matcher, matches_a_zero_length_process_selector)
{
    const std::string empty_path;
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(builder.add_process(""), rule_pool_index_any);
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(match_connection(
                    blob, make_connection(empty_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(match_connection(
                     blob, make_connection("app.exe", make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4))
                     .matched);
}

TEST(matcher, matches_the_closed_port_range_endpoints)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, rule_pool_index_any);
    entry.remote_port_start = 1000;
    entry.remote_port_end = 2000;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(
        match_connection(blob,
                         make_connection(process_path, make_ipv4(1, 2, 3, 4), 1000, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_TRUE(
        match_connection(blob,
                         make_connection(process_path, make_ipv4(1, 2, 3, 4), 2000, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(blob,
                         make_connection(process_path, make_ipv4(1, 2, 3, 4), 999, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(blob,
                         make_connection(process_path, make_ipv4(1, 2, 3, 4), 2001, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, matches_any_port_when_the_full_range_is_used)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    builder.add_rule(make_rule(rule_pool_index_any, rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(match_connection(
                    blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 0, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_TRUE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 0xFFFF, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_TRUE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 12345, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, matches_an_ipv4_slash_zero_prefix)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(4, 0, make_ipv4(0, 0, 0, 0)));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(match_connection(
                    blob, make_connection(process_path, make_ipv4(8, 8, 8, 8), 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(
                process_path, make_address({ 0x20, 0x01, 0x0d, 0xB8 }), 80, tcp_protocol, address_family::ipv6))
            .matched);
}

TEST(matcher, matches_an_ipv6_slash_zero_prefix)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(6, 0, make_address({})));
    builder.add_rule(entry);

    const match_result result =
        match_connection(builder.build(),
                         make_connection(process_path,
                                         make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }),
                                         80,
                                         tcp_protocol,
                                         address_family::ipv6));
    EXPECT_TRUE(result.matched);
}

TEST(matcher, matches_an_exact_ipv4_slash_32_prefix)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(4, 32, make_ipv4(203, 0, 113, 7)));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(203, 0, 113, 7), 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(203, 0, 113, 8), 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, matches_an_exact_ipv6_slash_128_prefix)
{
    const std::string process_path = "app.exe";
    const auto target = make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(6, 128, target));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(
        match_connection(blob, make_connection(process_path, target, 80, tcp_protocol, address_family::ipv6)).matched);
    EXPECT_FALSE(
        match_connection(blob,
                         make_connection(process_path,
                                         make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 }),
                                         80,
                                         tcp_protocol,
                                         address_family::ipv6))
            .matched);
}

TEST(matcher, matches_a_partial_byte_prefix)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(4, 25, make_ipv4(192, 168, 1, 0)));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(192, 168, 1, 1), 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_TRUE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(192, 168, 1, 127), 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(192, 168, 1, 128), 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob, make_connection(process_path, make_ipv4(192, 168, 1, 255), 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, matches_an_ipv6_subnet_prefix)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, builder.add_network(6, 64, make_address({ 0x20, 0x01, 0x0d, 0xB8 })));
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(process_path,
                                    make_address({ 0x20, 0x01, 0x0d, 0xB8, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 1 }),
                                    80,
                                    tcp_protocol,
                                    address_family::ipv6))
                    .matched);
    EXPECT_FALSE(
        match_connection(blob,
                         make_connection(process_path,
                                         make_address({ 0x20, 0x01, 0x0d, 0xB9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }),
                                         80,
                                         tcp_protocol,
                                         address_family::ipv6))
            .matched);
}

TEST(matcher, matches_any_address_when_there_is_no_selector)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    builder.add_rule(make_rule(rule_pool_index_any, rule_pool_index_any));
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_TRUE(match_connection(
                    blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        process_path, make_address({ 0x20, 0x01, 0x0d, 0xB8 }), 80, tcp_protocol, address_family::ipv6))
                    .matched);
}

TEST(matcher, rejects_a_different_transport_protocol)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, rule_pool_index_any);
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_FALSE(match_connection(
                     blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, udp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_TRUE(match_connection(
                    blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4))
                    .matched);
}

TEST(matcher, matches_any_protocol_when_there_is_no_selector)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    builder.add_rule(make_rule(rule_pool_index_any, rule_pool_index_any));

    const match_result result = match_connection(
        builder.build(), make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, udp_protocol, address_family::ipv4));
    EXPECT_TRUE(result.matched);
}

TEST(matcher, rejects_a_different_address_family)
{
    const std::string process_path = "app.exe";
    wfp_test::rule_blob_builder builder;
    rule entry = make_rule(rule_pool_index_any, rule_pool_index_any);
    entry.family = winforward::wfp::rule_family_ipv4;
    builder.add_rule(entry);
    const std::vector<std::uint8_t> blob = builder.build();

    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(
                process_path, make_address({ 0x20, 0x01, 0x0d, 0xB8 }), 80, tcp_protocol, address_family::ipv6))
            .matched);
    EXPECT_TRUE(match_connection(
                    blob, make_connection(process_path, make_ipv4(1, 2, 3, 4), 80, tcp_protocol, address_family::ipv4))
                    .matched);
}
