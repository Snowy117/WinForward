#include <gtest/gtest.h>
#include <sys/mman.h>
#include <unistd.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <string>
#include <vector>

#include "rule_blob.h"
#include "rule_blob_builder.h"

namespace
{

using wfp_test::open_blob;
using winforward::wfp::blob_view;
using winforward::wfp::process_selector_kind;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_blob_header;

constexpr process_selector_kind file_name = process_selector_kind::file_name;
constexpr process_selector_kind path = process_selector_kind::path;

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

std::vector<std::uint8_t> make_base_blob()
{
    wfp_test::rule_blob_builder builder;
    rule entry = wfp_test::make_any_rule();
    entry.process_first = builder.add_selector("app.exe", file_name);
    entry.process_count = 1;
    builder.add_rule(entry);
    return builder.build();
}

} // namespace

TEST(rule_blob, opens_a_v2_blob)
{
    const std::string path_text = R"(\device\harddiskvolume3\windows\system32\curl.exe)";
    wfp_test::rule_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    const std::uint32_t first_selector = builder.add_selector(path_text, path);
    const std::uint32_t second_selector = builder.add_selector("curl.exe", file_name);
    const std::uint32_t network = builder.add_network(winforward::wfp::rule_family_ipv4, 24, make_ipv4(203, 0, 113, 0));
    const std::uint32_t first_port = builder.add_port(80, 80);
    const std::uint32_t second_port = builder.add_port(443, 443);

    rule entry = wfp_test::make_any_rule();
    entry.process_first = first_selector;
    entry.process_count = 2;
    entry.network_first = network;
    entry.network_count = 1;
    entry.port_first = first_port;
    entry.port_count = 2;
    entry.protocol_mask = winforward::wfp::rule_protocol_mask_all;
    entry.family_mask = winforward::wfp::rule_family_mask_ipv4;
    entry.action = rule_action::redirect;
    builder.add_rule(entry);

    const std::vector<std::uint8_t> blob = builder.build();
    blob_view view{};
    ASSERT_TRUE(open_blob(blob, view));

    EXPECT_EQ(view.header->magic, winforward::wfp::rule_blob_magic);
    EXPECT_EQ(view.header->version, 2U);
    EXPECT_EQ(view.header->header_size, sizeof(rule_blob_header));
    EXPECT_EQ(view.fallback_action, rule_action::block);
    ASSERT_EQ(view.rule_count, 1U);
    EXPECT_EQ(view.rules[0].action, rule_action::redirect);
    EXPECT_EQ(view.rules[0].protocol_mask, winforward::wfp::rule_protocol_mask_all);
    EXPECT_EQ(view.rules[0].family_mask, winforward::wfp::rule_family_mask_ipv4);
    ASSERT_EQ(view.selector_pool_count, 2U);
    EXPECT_EQ(view.selector_pool[second_selector].kind, file_name);
    EXPECT_EQ(view.selector_pool[first_selector].kind, path);
    EXPECT_EQ(view.process_pool_size, path_text.size() + 1 + std::string("curl.exe").size() + 1);
    EXPECT_STREQ(view.process_pool + view.selector_pool[first_selector].string_offset, path_text.c_str());
    ASSERT_EQ(view.network_pool_count, 1U);
    ASSERT_EQ(view.port_pool_count, 2U);
    EXPECT_EQ(view.port_pool[0].start, 80U);
    EXPECT_EQ(view.port_pool[second_port].end, 443U);
}

TEST(rule_blob, opens_an_empty_rule_table)
{
    const wfp_test::rule_blob_builder builder;
    const std::vector<std::uint8_t> blob = builder.build();
    EXPECT_EQ(blob.size(), sizeof(rule_blob_header));

    blob_view view{};
    ASSERT_TRUE(open_blob(blob, view));
    EXPECT_EQ(view.rule_count, 0U);
    EXPECT_EQ(view.process_pool_size, 0U);
    EXPECT_EQ(view.selector_pool_count, 0U);
    EXPECT_EQ(view.network_pool_count, 0U);
    EXPECT_EQ(view.port_pool_count, 0U);
    EXPECT_EQ(view.fallback_action, rule_action::permit);
}

TEST(rule_blob, rejects_a_null_buffer)
{
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(nullptr, sizeof(rule_blob_header), view));
}

TEST(rule_blob, rejects_a_buffer_shorter_than_the_header)
{
    alignas(4) std::array<std::uint8_t, 8> tiny{};
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(tiny.data(), 8, view));
}

TEST(rule_blob, rejects_a_short_buffer_without_reading_past_its_end)
{
    // 短缓冲的 size 检查只防越界读：任何 56 字节头都无法在 size < 56 时同时满足
    // header_size == 56 与段偏移约束，逻辑层看不到它的必要性。把 8 字节放到页尾、
    // 下一页设为 PROT_NONE：删除 size 检查后读取 header.total_size 会立即段错误，
    // 变异测试才能杀死这个用例。
    const long page_size = ::sysconf(_SC_PAGESIZE);
    ASSERT_GT(page_size, 8);
    const auto page_bytes = static_cast<std::size_t>(page_size);
    void* mapping = ::mmap(nullptr, page_bytes * 2, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    ASSERT_NE(mapping, MAP_FAILED);
    auto* base = static_cast<std::uint8_t*>(mapping);
    if (::mprotect(base + page_bytes, page_bytes, PROT_NONE) != 0)
    {
        ::munmap(mapping, page_bytes * 2);
        FAIL() << "mprotect failed";
    }

    std::uint8_t* guard = base + page_bytes - 8;
    ASSERT_EQ(reinterpret_cast<std::uintptr_t>(guard) % alignof(rule_blob_header), 0U);
    guard[0] = 0x57; // "WRB1"，小端
    guard[1] = 0x52;
    guard[2] = 0x42;
    guard[3] = 0x31;
    guard[4] = 2; // version = 2
    guard[5] = 0;
    guard[6] = 56; // header_size = 56
    guard[7] = 0;

    blob_view guarded_view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(guard, 8, guarded_view));
    ::munmap(mapping, page_bytes * 2);
}

TEST(rule_blob, rejects_an_unaligned_buffer)
{
    const std::vector<std::uint8_t> blob = make_base_blob();
    std::vector<std::uint8_t> storage(blob.size() + alignof(rule_blob_header));
    const std::size_t misalignment = reinterpret_cast<std::uintptr_t>(storage.data()) % alignof(rule_blob_header);
    const std::size_t offset = misalignment == 0 ? 1 : 0;
    std::uint8_t* unaligned = storage.data() + offset;
    ASSERT_NE(reinterpret_cast<std::uintptr_t>(unaligned) % alignof(rule_blob_header), 0U);

    std::memcpy(unaligned, blob.data(), blob.size());
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(unaligned, static_cast<std::uint32_t>(blob.size()), view));
}

TEST(rule_blob, rejects_a_truncated_blob)
{
    const std::vector<std::uint8_t> blob = make_base_blob();
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(blob.data(), static_cast<std::uint32_t>(blob.size() - 1), view));
}

TEST(rule_blob, leaves_the_view_untouched_on_failure)
{
    std::vector<std::uint8_t> blob = make_base_blob();
    rule_blob_header header = wfp_test::read_header(blob);
    header.magic = 0;
    wfp_test::write_header(blob, header);

    blob_view view{};
    view.data = blob.data();
    view.size = 7;
    view.header = &header;
    view.rule_count = 9;
    EXPECT_FALSE(open_blob(blob, view));
    EXPECT_EQ(view.data, blob.data());
    EXPECT_EQ(view.size, 7U);
    EXPECT_EQ(view.header, &header);
    EXPECT_EQ(view.rule_count, 9U);
}
