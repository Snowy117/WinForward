#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <vector>

#include "matcher.h"
#include "matcher_test_support.h"
#include "rule_blob_builder.h"

namespace
{

using namespace wfp_matcher_test;
using winforward::wfp::rule;

constexpr const char* tools_path = R"(\device\harddiskvolume3\tools)";

TEST(matcher, matches_a_file_name_selector)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "curl.exe", file_name } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        R"(\device\harddiskvolume3\windows\curl.exe)", address, 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(R"(\device\harddiskvolume3\CURL.EXE)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(R"(\device\harddiskvolume3\curl2.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(R"(\device\harddiskvolume3\notcurl.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, file_name_selector_matches_the_last_segment_only)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "tools.exe", file_name } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        R"(\device\harddiskvolume3\a\b\tools.exe)", address, 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(match_connection(
                     blob,
                     make_connection(
                         R"(\device\harddiskvolume3\a\b\mytools.exe)", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_FALSE(match_connection(
                     blob,
                     make_connection(
                         R"(\device\harddiskvolume3\tools.exe\child)", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
}

TEST(matcher, file_name_selector_does_not_match_a_longer_name)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "dns", file_name } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(
            blob, make_connection(R"(\device\harddiskvolume3\dns)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(R"(\device\harddiskvolume3\dns.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, file_name_selector_matches_a_bare_name)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "app.exe", file_name } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(blob, make_connection("app.exe", address, 80, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_FALSE(match_connection(blob, make_connection("app.exe.bak", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
}

TEST(matcher, path_selector_matches_exact_and_descendants)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { tools_path, path, true } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(blob, make_connection(tools_path, address, 80, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        R"(\device\harddiskvolume3\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(
                R"(\device\harddiskvolume3\tools\sub\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(match_connection(
                     blob,
                     make_connection(
                         R"(\device\harddiskvolume3\tools2\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(
                R"(\device\harddiskvolume3\other\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, path_selector_requires_a_separator_at_the_prefix_boundary)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { R"(\device\harddiskvolume3\tool)", path } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(R"(\device\harddiskvolume3\tool\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(match_connection(
                     blob,
                     make_connection(
                         R"(\device\harddiskvolume3\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
}

TEST(matcher, path_selector_still_matches_exact_file_and_its_prefix)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { R"(\device\harddiskvolume3\tools\app.exe)", path } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        R"(\device\harddiskvolume3\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(match_connection(
                     blob,
                     make_connection(
                         R"(\device\harddiskvolume3\other\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                     .matched);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(
                R"(\device\harddiskvolume3\tools\app.exe\child)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, path_selector_folds_ascii_only)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { R"(\device\harddiskvolume1\users\张三\工具.exe)", path } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(
            blob,
            make_connection(
                R"(\DEVICE\HARDDISKVOLUME1\USERS\张三\工具.EXE)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(
            blob,
            make_connection(
                R"(\DEVICE\HARDDISKVOLUME1\USERS\张三\工具.EXE.BAK)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

TEST(matcher, is_directory_flag_does_not_change_matching)
{
    const auto directory = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { tools_path, path, true } });
        });
    const auto regular = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { tools_path, path, false } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    const auto child =
        make_connection(R"(\device\harddiskvolume3\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4);
    EXPECT_TRUE(match_connection(directory, child).matched);
    EXPECT_TRUE(match_connection(regular, child).matched);
}

TEST(matcher, missing_or_empty_process_path_matches_nothing)
{
    const auto file_blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "app.exe", file_name } });
        });
    const auto path_blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { tools_path, path, true } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_FALSE(
        match_connection(file_blob, make_connection_without_path(address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(path_blob, make_connection_without_path(address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_FALSE(
        match_connection(file_blob, make_connection("", address, 80, tcp_protocol, address_family::ipv4)).matched);
    EXPECT_FALSE(
        match_connection(path_blob, make_connection("", address, 80, tcp_protocol, address_family::ipv4)).matched);
}

TEST(matcher, process_array_is_or)
{
    const auto blob = build_blob(
        [](wfp_test::rule_blob_builder& builder, rule& entry)
        {
            set_processes(builder, entry, { { "alpha.exe", file_name }, { tools_path, path, true } });
        });

    const auto address = make_ipv4(1, 2, 3, 4);
    EXPECT_TRUE(
        match_connection(blob, make_connection(R"(\device\alpha.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
    EXPECT_TRUE(match_connection(
                    blob,
                    make_connection(
                        R"(\device\harddiskvolume3\tools\app.exe)", address, 80, tcp_protocol, address_family::ipv4))
                    .matched);
    EXPECT_FALSE(
        match_connection(blob, make_connection(R"(\device\beta.exe)", address, 80, tcp_protocol, address_family::ipv4))
            .matched);
}

} // namespace
