#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "differential_fixture.h"
#include "matcher.h"
#include "rule_blob.h"

// C# 侧（src/userland）把规则模型编译成 blob，并把「连接 → 期望决策」的整张表写进夹具；
// 这里用同一份 try_open_blob + match 消费，逐条比对。任何一边的布局或语义漂移都会在这里变红。

namespace
{

using winforward::wfp::blob_view;
using winforward::wfp::connection_view;
using winforward::wfp::match_result;
using winforward::wfp::rule_action;

constexpr std::size_t expected_case_count = 32;
constexpr std::size_t expected_connection_count = 15;

// 覆盖维度按名字钉死：空表、优先级、CIDR /0 与 /32、IPv6 对应项、端口端点、
// 进程串大小写与中文路径、进程 + 网段组合、协议、全任意。
constexpr auto required_case_names = std::to_array<std::string_view>({
    "empty-table-block",
    "empty-table-redirect",
    "priority-first-match-wins",
    "mixed-rules-report-rule-index",
    "cidr-v4-slash0-family-v4",
    "cidr-v4-slash32",
    "cidr-v6-slash0-family-v6",
    "cidr-v6-slash128",
    "port-range-endpoints-excluded",
    "process-rule-uppercase",
    "process-unicode-path",
    "process-and-network-together",
    "protocol-tcp-excludes-udp",
    "all-any-single-rule",
});

[[nodiscard]] std::filesystem::path fixture_directory()
{
    const char* from_environment = std::getenv("WF_DIFF_FIXTURE_DIR");
    if (from_environment != nullptr && *from_environment != '\0')
    {
        return { from_environment };
    }
    return { WF_DIFF_FIXTURE_DIR_DEFAULT };
}

[[nodiscard]] std::string_view action_name(rule_action action) noexcept
{
    switch (action)
    {
    case rule_action::permit:
        return "permit";
    case rule_action::block:
        return "block";
    case rule_action::redirect:
        return "redirect";
    }
    return "unknown";
}

[[nodiscard]] std::string hex_of(std::span<const std::uint8_t> bytes)
{
    static constexpr char digits[] = "0123456789abcdef";

    std::string text;
    text.reserve(bytes.size() * 2U);
    for (const std::uint8_t value : bytes)
    {
        text.push_back(digits[value >> 4U]);
        text.push_back(digits[value & 0x0FU]);
    }
    return text;
}

[[nodiscard]] std::string describe(const wfp_test::fixture_connection& connection)
{
    const std::size_t address_size =
        connection.family == winforward::wfp::rule_family_ipv4 ? 4U : winforward::wfp::network_address_size;

    std::string text = "family=" + std::to_string(connection.family);
    text += " protocol=" + std::to_string(connection.protocol);
    text += " port=" + std::to_string(connection.port);
    text += " address=" + hex_of(std::span<const std::uint8_t>(connection.address.data(), address_size));
    text += " process=";
    text += connection.has_process_path ? connection.process_path : "<null>";
    return text;
}

[[nodiscard]] connection_view make_connection(const wfp_test::fixture_connection& source)
{
    connection_view view{};
    for (std::size_t index = 0; index < winforward::wfp::network_address_size; ++index)
    {
        view.remote_address[index] = source.address[index];
    }
    view.process_path = source.has_process_path ? source.process_path.c_str() : nullptr;
    view.process_path_length = static_cast<std::uint32_t>(source.process_path.size());
    view.remote_port = source.port;
    view.protocol = source.protocol;
    view.family = static_cast<winforward::wfp::address_family>(source.family);
    return view;
}

[[nodiscard]] std::vector<wfp_test::fixture_case> load_cases()
{
    const std::expected<std::vector<wfp_test::fixture_case>, std::string> cases =
        wfp_test::load_manifest(fixture_directory() / "manifest.txt");
    if (!cases.has_value())
    {
        ADD_FAILURE() << cases.error();
        return {};
    }
    return *cases;
}

TEST(Differential, ManifestIsPresent)
{
    const std::filesystem::path manifest = fixture_directory() / "manifest.txt";
    ASSERT_TRUE(std::filesystem::exists(manifest))
        << "夹具清单不存在：" << manifest.string()
        << "\n先生成夹具：cd src/userland && dotnet run --project WinForward.Userland.Tests -- test";
}

TEST(Differential, CoverageFloor)
{
    const std::vector<wfp_test::fixture_case> cases = load_cases();
    ASSERT_FALSE(cases.empty());

    // 精确值而不是下限：删掉用例必须让这条断言变红，420/480 这样的数字不许静默缩水。
    EXPECT_EQ(cases.size(), expected_case_count) << "用例数必须精确等于约定值";
    for (const wfp_test::fixture_case& item : cases)
    {
        SCOPED_TRACE("case=" + item.name);
        EXPECT_EQ(item.connections.size(), expected_connection_count) << "每组连接数必须精确等于约定值";
    }

    // 覆盖维度也钉死在名字上：用例被重命名或删掉时，这里会指出缺的是哪一类。
    for (const std::string_view required : required_case_names)
    {
        const bool present = std::ranges::any_of(cases,
                                                 [required](const wfp_test::fixture_case& item)
                                                 {
                                                     return item.name == required;
                                                 });
        EXPECT_TRUE(present) << "缺少必需用例：" << required;
    }
}

TEST(Differential, BlobsOpen)
{
    for (const wfp_test::fixture_case& item : load_cases())
    {
        SCOPED_TRACE("case=" + item.name);
        const std::expected<wfp_test::blob_buffer, std::string> blob =
            wfp_test::blob_buffer::load(fixture_directory() / item.blob_file);
        if (!blob.has_value())
        {
            ADD_FAILURE() << blob.error();
            continue;
        }

        blob_view view{};
        if (!winforward::wfp::try_open_blob(blob->data(), blob->size(), view))
        {
            ADD_FAILURE() << "try_open_blob 拒绝了 C# 侧产出的 blob：" << item.blob_file;
            continue;
        }

        EXPECT_EQ(view.header->rule_count, item.rule_count) << "清单与实际规则的条数";
        EXPECT_EQ(action_name(view.fallback_action), action_name(item.fallback_action)) << "兜底动作";
        EXPECT_EQ(view.size, blob->size()) << "视图长度";
    }
}

TEST(Differential, DecisionsMatchExpected)
{
    for (const wfp_test::fixture_case& item : load_cases())
    {
        SCOPED_TRACE("case=" + item.name);
        const std::expected<wfp_test::blob_buffer, std::string> blob =
            wfp_test::blob_buffer::load(fixture_directory() / item.blob_file);
        if (!blob.has_value())
        {
            ADD_FAILURE() << blob.error();
            continue;
        }

        blob_view view{};
        if (!winforward::wfp::try_open_blob(blob->data(), blob->size(), view))
        {
            ADD_FAILURE() << "try_open_blob 拒绝了 C# 侧产出的 blob：" << item.blob_file;
            continue;
        }

        for (std::size_t index = 0; index < item.connections.size(); ++index)
        {
            const wfp_test::fixture_connection& connection = item.connections[index];
            SCOPED_TRACE("connection=" + std::to_string(index) + " (" + connection.label + ") " + describe(connection));

            const match_result result = winforward::wfp::match(view, make_connection(connection));
            EXPECT_EQ(result.matched, connection.expected_matched) << "命中与否";
            EXPECT_EQ(action_name(result.action), action_name(connection.expected_action)) << "决策动作";
            EXPECT_EQ(result.rule_index, connection.expected_rule_index) << "命中的规则序号（4294967295 表示未命中）";
        }
    }
}

} // namespace
