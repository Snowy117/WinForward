#pragma once

#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <initializer_list>
#include <string_view>
#include <utility>
#include <vector>

#include "matcher.h"
#include "rule_blob_builder.h"

// matcher 测试的共享夹具：构造连接事实、单规则 blob 与选择器数组。

namespace wfp_matcher_test
{

using winforward::wfp::address_family;
using winforward::wfp::connection_view;
using winforward::wfp::match_result;
using winforward::wfp::process_selector_kind;
using winforward::wfp::rule;
using winforward::wfp::rule_action;

inline constexpr std::uint8_t tcp_protocol = 6;
inline constexpr std::uint8_t udp_protocol = 17;
inline constexpr process_selector_kind file_name = process_selector_kind::file_name;
inline constexpr process_selector_kind path = process_selector_kind::path;

[[nodiscard]] inline std::array<std::uint8_t, 16> make_address(std::initializer_list<std::uint8_t> bytes)
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

[[nodiscard]] inline std::array<std::uint8_t, 16>
make_ipv4(std::uint8_t first, std::uint8_t second, std::uint8_t third, std::uint8_t fourth)
{
    return make_address({ first, second, third, fourth });
}

[[nodiscard]] inline connection_view make_connection(const char* process_path,
                                                     const std::array<std::uint8_t, 16>& address,
                                                     std::uint16_t remote_port,
                                                     std::uint8_t protocol,
                                                     address_family family)
{
    connection_view connection{};
    connection.process_path = process_path;
    connection.process_path_length = static_cast<std::uint32_t>(std::strlen(process_path));
    for (std::size_t index = 0; index < address.size(); ++index)
    {
        connection.remote_address[index] = address[index];
    }
    connection.remote_port = remote_port;
    connection.protocol = protocol;
    connection.family = family;
    return connection;
}

[[nodiscard]] inline connection_view make_connection_without_path(const std::array<std::uint8_t, 16>& address,
                                                                  std::uint16_t remote_port,
                                                                  std::uint8_t protocol,
                                                                  address_family family)
{
    connection_view connection = make_connection("", address, remote_port, protocol, family);
    connection.process_path = nullptr;
    return connection;
}

[[nodiscard]] inline match_result match_connection(const std::vector<std::uint8_t>& blob,
                                                   const connection_view& connection)
{
    winforward::wfp::blob_view view{};
    EXPECT_TRUE(wfp_test::open_blob(blob, view));
    return winforward::wfp::match(view, connection);
}

[[nodiscard]] inline std::vector<std::uint8_t>
build_blob(const std::function<void(wfp_test::rule_blob_builder&, rule&)>& configure)
{
    wfp_test::rule_blob_builder builder;
    rule entry = wfp_test::make_any_rule();
    configure(builder, entry);
    builder.add_rule(entry);
    return builder.build();
}

struct selector_spec
{
    std::string_view text;
    process_selector_kind kind;
    bool is_directory;

    constexpr selector_spec(std::string_view selector_text,
                            process_selector_kind selector_kind,
                            bool directory = false) noexcept
        : text(selector_text)
        , kind(selector_kind)
        , is_directory(directory)
    {
    }
};

inline void
set_processes(wfp_test::rule_blob_builder& builder, rule& entry, std::initializer_list<selector_spec> selectors)
{
    std::uint32_t first = 0;
    std::uint32_t count = 0;
    for (const selector_spec& spec : selectors)
    {
        const std::uint32_t index = builder.add_selector(spec.text, spec.kind, spec.is_directory);
        if (count == 0)
        {
            first = index;
        }
        ++count;
    }
    entry.process_first = first;
    entry.process_count = count;
}

struct network_spec
{
    std::uint8_t family;
    std::uint8_t prefix_length;
    std::array<std::uint8_t, 16> address;

    constexpr network_spec(std::uint8_t network_family,
                           std::uint8_t length,
                           const std::array<std::uint8_t, 16>& network_address) noexcept
        : family(network_family)
        , prefix_length(length)
        , address(network_address)
    {
    }
};

inline void
set_networks(wfp_test::rule_blob_builder& builder, rule& entry, std::initializer_list<network_spec> networks)
{
    std::uint32_t first = 0;
    std::uint32_t count = 0;
    for (const network_spec& spec : networks)
    {
        const std::uint32_t index = builder.add_network(spec.family, spec.prefix_length, spec.address);
        if (count == 0)
        {
            first = index;
        }
        ++count;
    }
    entry.network_first = first;
    entry.network_count = count;
}

inline void set_ports(wfp_test::rule_blob_builder& builder,
                      rule& entry,
                      std::initializer_list<std::pair<std::uint16_t, std::uint16_t>> ranges)
{
    std::uint32_t first = 0;
    std::uint32_t count = 0;
    for (const auto& [start, end] : ranges)
    {
        const std::uint32_t index = builder.add_port(start, end);
        if (count == 0)
        {
            first = index;
        }
        ++count;
    }
    entry.port_first = first;
    entry.port_count = count;
}

} // namespace wfp_matcher_test
