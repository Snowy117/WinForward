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
using wfp_test::read_header;
using wfp_test::read_rule;
using wfp_test::write_header;
using wfp_test::write_rule;
using winforward::wfp::blob_view;
using winforward::wfp::network_prefix;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_blob_header;

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

rule make_any_rule()
{
    rule entry{};
    entry.process_index = winforward::wfp::rule_pool_index_any;
    entry.network_index = winforward::wfp::rule_pool_index_any;
    entry.remote_port_start = winforward::wfp::rule_port_any_begin;
    entry.remote_port_end = winforward::wfp::rule_port_any_end;
    entry.action = rule_action::permit;
    return entry;
}

std::vector<std::uint8_t> make_simple_blob()
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.process_index = builder.add_process(R"(c:\curl.exe)");
    builder.add_rule(entry);
    return builder.build();
}

std::vector<std::uint8_t> make_network_blob()
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.process_index = builder.add_process("abc");
    entry.network_index = builder.add_network(4, 24, make_ipv4(10, 0, 0, 0));
    builder.add_rule(entry);
    return builder.build();
}

} // namespace

TEST(rule_blob, opens_a_valid_blob)
{
    const std::string process_path = R"(c:\windows\system32\curl.exe)";
    wfp_test::rule_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    const std::uint32_t process_index = builder.add_process(process_path);
    const std::uint32_t network_index = builder.add_network(4, 24, make_ipv4(203, 0, 113, 0));

    rule entry = make_any_rule();
    entry.process_index = process_index;
    entry.network_index = network_index;
    entry.remote_port_start = 80;
    entry.remote_port_end = 443;
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.action = rule_action::redirect;
    builder.add_rule(entry);

    const std::vector<std::uint8_t> blob = builder.build();
    blob_view view{};
    ASSERT_TRUE(open_blob(blob, view));

    EXPECT_EQ(view.data, blob.data());
    EXPECT_EQ(view.size, blob.size());
    EXPECT_EQ(view.header->magic, winforward::wfp::rule_blob_magic);
    EXPECT_EQ(view.fallback_action, rule_action::block);
    ASSERT_EQ(view.rule_count, 1U);
    EXPECT_EQ(view.rules[0].action, rule_action::redirect);
    EXPECT_EQ(view.rules[0].process_index, process_index);
    EXPECT_EQ(view.rules[0].network_index, network_index);
    EXPECT_EQ(view.process_pool_size, process_path.size() + 1);
    EXPECT_EQ(view.network_pool_count, 1U);
    ASSERT_NE(view.process_pool, nullptr);
    EXPECT_STREQ(view.process_pool + process_index, process_path.c_str());
    EXPECT_EQ(view.network_pool[0].prefix_length, 24U);
    EXPECT_EQ(view.network_pool[0].family, winforward::wfp::rule_family_ipv4);
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
    EXPECT_EQ(view.network_pool_count, 0U);
    EXPECT_EQ(view.fallback_action, rule_action::permit);
}

TEST(rule_blob, exposes_the_fallback_action)
{
    wfp_test::rule_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    blob_view view{};
    ASSERT_TRUE(open_blob(builder.build(), view));
    EXPECT_EQ(view.fallback_action, rule_action::block);
}

TEST(rule_blob, rejects_a_null_buffer)
{
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(nullptr, 40, view));
}

TEST(rule_blob, rejects_a_buffer_shorter_than_the_header)
{
    alignas(4) std::array<std::uint8_t, 8> tiny{};
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(tiny.data(), 8, view));
}

TEST(rule_blob, rejects_a_short_buffer_without_reading_past_its_end)
{
    // 短缓冲的 size 检查只防越界读：任何 40 字节头都无法在 size < 40 时同时满足
    // header_size == 40 与段偏移约束，逻辑层看不到它的必要性。把 8 字节放到页尾、
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
    guard[4] = 1; // version = 1
    guard[5] = 0;
    guard[6] = 40; // header_size = 40
    guard[7] = 0;

    blob_view guarded_view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(guard, 8, guarded_view));
    ::munmap(mapping, page_bytes * 2);
}

TEST(rule_blob, rejects_an_unaligned_buffer)
{
    const std::vector<std::uint8_t> blob = make_simple_blob();
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
    const std::vector<std::uint8_t> blob = make_simple_blob();
    blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_blob(blob.data(), static_cast<std::uint32_t>(blob.size() - 1), view));
}

TEST(rule_blob, rejects_a_buffer_larger_than_the_blob)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    blob.resize(blob.size() + 8, 0xAB);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_wrong_magic)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.magic = 0;
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_foreign_version)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.version = static_cast<std::uint16_t>(winforward::wfp::rule_blob_version + 1);
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_header_size_that_is_not_the_fixed_size)
{
    blob_view view{};
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.header_size = 16;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    header = read_header(blob);
    header.header_size = static_cast<std::uint16_t>(sizeof(rule_blob_header) + 4);
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_an_unknown_fallback_action)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.fallback_action = 3;
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_bad_rule_offsets)
{
    blob_view view{};
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.rules_offset = 41;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    header = read_header(blob);
    header.rules_offset = 16;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    header = read_header(blob);
    header.rules_offset = header.total_size + 4;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_rule_count_that_overruns_the_blob)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.rule_count = 0xFFFFFFFFU;
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_misaligned_pool_offsets)
{
    blob_view view{};
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.process_pool_offset = 41;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_network_blob();
    header = read_header(blob);
    header.network_pool_offset = 61;
    write_header(blob, header);
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_process_pool_that_overruns_the_blob)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.process_pool_size = header.total_size;
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_rules_array_overlapping_the_process_pool)
{
    wfp_test::rule_blob_builder builder;
    builder.add_rule(make_any_rule());
    std::vector<std::uint8_t> blob = builder.build();

    // 规则数组的最后一个字节是 reserved=0，同时也充当进程池的尾部 NUL；没有规则引用
    // 进程池。因此只有"规则数组与进程池重叠"这一条能拒绝这个夹具。
    rule_blob_header header = read_header(blob);
    header.process_pool_offset = header.rules_offset;
    header.process_pool_size = static_cast<std::uint32_t>(sizeof(rule));
    write_header(blob, header);

    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_rules_array_overlapping_the_network_pool)
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.family = winforward::wfp::rule_family_ipv4;
    builder.add_rule(entry);
    std::vector<std::uint8_t> blob = builder.build();

    // 网段记录从 rule.protocol 的位置开始：protocol=0、family=4、action=permit=0、
    // reserved=0 恰好是合法记录头，record 的 network 字节落在规则数组之外并补零。
    // 因此只有"规则数组与网段池重叠"这一条能拒绝这个夹具。
    rule_blob_header header = read_header(blob);
    header.total_size = static_cast<std::uint32_t>(blob.size() + sizeof(network_prefix));
    header.network_pool_offset = header.rules_offset + static_cast<std::uint32_t>(offsetof(rule, protocol));
    header.network_pool_size = static_cast<std::uint32_t>(sizeof(network_prefix));
    blob.resize(header.total_size, 0);
    write_header(blob, header);

    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_process_pool_overlapping_the_network_pool)
{
    const wfp_test::rule_blob_builder builder;
    std::vector<std::uint8_t> blob = builder.build();
    blob.resize(static_cast<std::size_t>(sizeof(rule_blob_header) + sizeof(network_prefix)), 0);

    // 池首 4 字节同时满足两种读法：进程池的尾部 NUL（最后一字节为 0）与合法网段
    // 记录头（prefix=0、family=4、reserved=0）。没有规则，所以只有"进程池与网段池
    // 重叠"这一条能拒绝这个夹具。
    rule_blob_header header = read_header(blob);
    header.total_size = static_cast<std::uint32_t>(blob.size());
    header.process_pool_offset = header.rules_offset;
    header.process_pool_size = 4;
    header.network_pool_offset = header.rules_offset;
    header.network_pool_size = static_cast<std::uint32_t>(sizeof(network_prefix));
    write_header(blob, header);

    const std::size_t pool_offset = header.process_pool_offset;
    blob[pool_offset] = 0;
    blob[pool_offset + 1] = winforward::wfp::rule_family_ipv4;
    blob[pool_offset + 2] = 0;
    blob[pool_offset + 3] = 0;

    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_process_pool_without_a_terminator)
{
    std::vector<std::uint8_t> blob = make_network_blob();
    const rule_blob_header header = read_header(blob);
    blob[header.process_pool_offset + header.process_pool_size - 1] = 'x';
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_an_out_of_range_process_index)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    const rule_blob_header header = read_header(blob);
    rule entry = read_rule(blob, 0);
    entry.process_index = header.process_pool_size;
    write_rule(blob, 0, entry);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_process_index_inside_a_string)
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.process_index = builder.add_process("alpha") + 2;
    builder.add_rule(entry);

    const std::vector<std::uint8_t> blob = builder.build();
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_network_pool_size_that_is_not_a_multiple_of_twenty)
{
    std::vector<std::uint8_t> blob = make_network_blob();
    rule_blob_header header = read_header(blob);
    header.network_pool_size -= 1;
    write_header(blob, header);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_an_out_of_range_network_index)
{
    std::vector<std::uint8_t> blob = make_network_blob();
    const rule_blob_header header = read_header(blob);
    rule entry = read_rule(blob, 0);
    entry.network_index = header.network_pool_size;
    write_rule(blob, 0, entry);
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_a_misaligned_network_index)
{
    wfp_test::rule_blob_builder builder;
    builder.add_network(4, 24, make_ipv4(10, 0, 0, 0));
    builder.add_network(4, 24, make_ipv4(10, 0, 1, 0));
    builder.add_network(4, 24, make_ipv4(10, 0, 2, 0));
    rule entry = make_any_rule();
    entry.network_index = 24;
    builder.add_rule(entry);

    const std::vector<std::uint8_t> blob = builder.build();
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_unknown_rule_fields)
{
    blob_view view{};

    std::vector<std::uint8_t> blob = make_simple_blob();
    rule entry = read_rule(blob, 0);
    entry.action = static_cast<rule_action>(3);
    write_rule(blob, 0, entry);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    entry = read_rule(blob, 0);
    entry.reserved = 1;
    write_rule(blob, 0, entry);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    entry = read_rule(blob, 0);
    entry.protocol = 17;
    write_rule(blob, 0, entry);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    entry = read_rule(blob, 0);
    entry.family = 5;
    write_rule(blob, 0, entry);
    EXPECT_FALSE(open_blob(blob, view));

    blob = make_simple_blob();
    entry = read_rule(blob, 0);
    entry.remote_port_start = 2000;
    entry.remote_port_end = 1000;
    write_rule(blob, 0, entry);
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, rejects_invalid_network_entries)
{
    blob_view view{};

    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.network_index = builder.add_network(5, 24, make_ipv4(10, 0, 0, 0));
    builder.add_rule(entry);
    EXPECT_FALSE(open_blob(builder.build(), view));

    builder = wfp_test::rule_blob_builder{};
    entry = make_any_rule();
    entry.network_index = builder.add_network(4, 33, make_ipv4(10, 0, 0, 0));
    builder.add_rule(entry);
    EXPECT_FALSE(open_blob(builder.build(), view));

    builder = wfp_test::rule_blob_builder{};
    entry = make_any_rule();
    entry.network_index = builder.add_network(6, 129, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    builder.add_rule(entry);
    EXPECT_FALSE(open_blob(builder.build(), view));

    network_prefix prefix{};
    prefix.prefix_length = 24;
    prefix.family = winforward::wfp::rule_family_ipv4;
    prefix.reserved[0] = 1;
    builder = wfp_test::rule_blob_builder{};
    entry = make_any_rule();
    entry.network_index = builder.add_network(prefix);
    builder.add_rule(entry);
    EXPECT_FALSE(open_blob(builder.build(), view));
}

TEST(rule_blob, rejects_a_family_mismatch_between_rule_and_network)
{
    wfp_test::rule_blob_builder builder;
    rule entry = make_any_rule();
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.network_index = builder.add_network(6, 64, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    builder.add_rule(entry);

    const std::vector<std::uint8_t> blob = builder.build();
    blob_view view{};
    EXPECT_FALSE(open_blob(blob, view));
}

TEST(rule_blob, leaves_the_view_untouched_on_failure)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    rule_blob_header header = read_header(blob);
    header.magic = 0;
    write_header(blob, header);

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
