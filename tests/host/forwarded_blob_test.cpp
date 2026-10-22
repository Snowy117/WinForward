#include <gtest/gtest.h>
#include <sys/mman.h>
#include <unistd.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <vector>

#include "forwarded_blob.h"
#include "forwarded_blob_builder.h"

namespace
{

using wfp_test::open_forwarded_blob;
using wfp_test::read_forwarded_header;
using wfp_test::read_forwarded_rule;
using wfp_test::write_forwarded_header;
using wfp_test::write_forwarded_rule;
using winforward::wfp::forwarded_blob_header;
using winforward::wfp::forwarded_blob_rule;
using winforward::wfp::forwarded_blob_view;
using winforward::wfp::network_prefix;
using winforward::wfp::rule_action;

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

forwarded_blob_rule make_any_rule()
{
    forwarded_blob_rule entry{};
    entry.ingress_adapter_id = winforward::wfp::forwarded_adapter_any;
    entry.client_network_index = winforward::wfp::rule_pool_index_any;
    entry.remote_network_index = winforward::wfp::rule_pool_index_any;
    entry.remote_port_start = winforward::wfp::rule_port_any_begin;
    entry.remote_port_end = winforward::wfp::rule_port_any_end;
    entry.upstream_id = winforward::wfp::forwarded_upstream_none;
    entry.action = rule_action::permit;
    return entry;
}

std::vector<std::uint8_t> make_simple_blob()
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_any_rule();
    entry.ingress_adapter_id = 3;
    entry.client_network_index = builder.add_network(4, 8, make_ipv4(10, 0, 0, 0));
    entry.remote_network_index = builder.add_network(4, 24, make_ipv4(203, 0, 113, 0));
    entry.remote_port_start = 80;
    entry.remote_port_end = 443;
    entry.upstream_id = 5;
    entry.protocol = winforward::wfp::rule_protocol_tcp;
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.action = rule_action::redirect;
    builder.add_rule(entry);
    return builder.build();
}

std::vector<std::uint8_t> make_network_blob()
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_network(4, 24, make_ipv4(10, 0, 0, 0));
    builder.add_network(4, 24, make_ipv4(10, 0, 1, 0));
    builder.add_network(4, 24, make_ipv4(10, 0, 2, 0));
    forwarded_blob_rule entry = make_any_rule();
    entry.remote_network_index = 0;
    builder.add_rule(entry);
    return builder.build();
}

} // namespace

TEST(forwarded_blob, opens_a_valid_blob)
{
    const std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(blob, view));

    EXPECT_EQ(view.data, blob.data());
    EXPECT_EQ(view.size, blob.size());
    EXPECT_EQ(view.header->magic, winforward::wfp::forwarded_blob_magic);
    EXPECT_EQ(view.fallback_action, rule_action::permit);
    ASSERT_EQ(view.rule_count, 1U);
    EXPECT_EQ(view.rules[0].ingress_adapter_id, 3U);
    EXPECT_EQ(view.rules[0].client_network_index, 0U);
    EXPECT_EQ(view.rules[0].remote_network_index, 20U);
    EXPECT_EQ(view.rules[0].remote_port_start, 80U);
    EXPECT_EQ(view.rules[0].remote_port_end, 443U);
    EXPECT_EQ(view.rules[0].upstream_id, 5U);
    EXPECT_EQ(view.rules[0].protocol, winforward::wfp::rule_protocol_tcp);
    EXPECT_EQ(view.rules[0].family, winforward::wfp::rule_family_ipv4);
    EXPECT_EQ(view.rules[0].action, rule_action::redirect);
    EXPECT_EQ(view.rules[0].reserved, 0U);
    ASSERT_EQ(view.network_pool_count, 2U);
    EXPECT_EQ(view.network_pool[0].prefix_length, 8U);
    EXPECT_EQ(view.network_pool[1].prefix_length, 24U);
    EXPECT_EQ(view.network_pool[1].network[0], 203U);
}

TEST(forwarded_blob, opens_an_empty_rule_table)
{
    const wfp_test::forwarded_blob_builder builder;
    const std::vector<std::uint8_t> blob = builder.build();
    EXPECT_EQ(blob.size(), sizeof(forwarded_blob_header));

    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(blob, view));
    EXPECT_EQ(view.rule_count, 0U);
    EXPECT_EQ(view.network_pool_count, 0U);
    EXPECT_EQ(view.fallback_action, rule_action::permit);
    EXPECT_EQ(view.header->rules_offset, 40U);
    EXPECT_EQ(view.header->network_pool_offset, 40U);
}

TEST(forwarded_blob, opens_multiple_rules_in_order)
{
    wfp_test::forwarded_blob_builder builder;
    builder.set_fallback_action(rule_action::block);

    forwarded_blob_rule first = make_any_rule();
    first.ingress_adapter_id = 11;
    first.client_network_index = builder.add_network(4, 16, make_ipv4(192, 168, 0, 0));
    first.action = rule_action::redirect;
    first.upstream_id = 2;
    builder.add_rule(first);

    forwarded_blob_rule second = make_any_rule();
    second.remote_network_index = builder.add_network(6, 32, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    second.family = winforward::wfp::rule_family_ipv6;
    second.action = rule_action::block;
    builder.add_rule(second);

    const std::vector<std::uint8_t> blob = builder.build();
    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(blob, view));
    ASSERT_EQ(view.rule_count, 2U);
    EXPECT_EQ(view.fallback_action, rule_action::block);
    EXPECT_EQ(view.rules[0].action, rule_action::redirect);
    EXPECT_EQ(view.rules[0].upstream_id, 2U);
    EXPECT_EQ(view.rules[1].action, rule_action::block);
    EXPECT_EQ(view.rules[1].family, winforward::wfp::rule_family_ipv6);
    ASSERT_EQ(view.network_pool_count, 2U);
    EXPECT_EQ(view.network_pool[1].family, winforward::wfp::rule_family_ipv6);
}

TEST(forwarded_blob, accepts_an_unrestricted_rule)
{
    wfp_test::forwarded_blob_builder builder;
    builder.add_rule(make_any_rule());
    const std::vector<std::uint8_t> blob = builder.build();

    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(blob, view));
    EXPECT_EQ(view.rules[0].ingress_adapter_id, winforward::wfp::forwarded_adapter_any);
    EXPECT_EQ(view.rules[0].upstream_id, winforward::wfp::forwarded_upstream_none);
}

TEST(forwarded_blob, accepts_zero_adapter_and_upstream)
{
    // 0 是合法的具体适配器编号与上游编号，不能误当成 "任意 / 无" 哨兵拒绝。
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_any_rule();
    entry.ingress_adapter_id = 0;
    entry.action = rule_action::redirect;
    entry.upstream_id = 0;
    builder.add_rule(entry);

    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(builder.build(), view));
    EXPECT_EQ(view.rules[0].ingress_adapter_id, 0U);
    EXPECT_EQ(view.rules[0].upstream_id, 0U);
}

TEST(forwarded_blob, exposes_the_fallback_action)
{
    wfp_test::forwarded_blob_builder builder;
    builder.set_fallback_action(rule_action::block);
    forwarded_blob_view view{};
    ASSERT_TRUE(open_forwarded_blob(builder.build(), view));
    EXPECT_EQ(view.fallback_action, rule_action::block);
}

TEST(forwarded_blob, rejects_a_null_buffer)
{
    forwarded_blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_forwarded_blob(nullptr, 40, view));
}

TEST(forwarded_blob, rejects_a_buffer_shorter_than_the_header)
{
    alignas(4) std::array<std::uint8_t, 8> tiny{};
    forwarded_blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_forwarded_blob(tiny.data(), 8, view));
}

TEST(forwarded_blob, rejects_a_short_buffer_without_reading_past_its_end)
{
    // 短缓冲的 size 检查只防越界读：size < 40 时任何头部都无法同时满足 header_size 与段约束。
    // 把 8 字节放在页尾、下一页设为 PROT_NONE：删掉 size 检查后读取 header.total_size 会立即段错误。
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
    ASSERT_EQ(reinterpret_cast<std::uintptr_t>(guard) % alignof(forwarded_blob_header), 0U);
    guard[0] = 0x57; // "WFB1"，小端
    guard[1] = 0x46;
    guard[2] = 0x42;
    guard[3] = 0x31;
    guard[4] = 1; // version = 1
    guard[5] = 0;
    guard[6] = 40; // header_size = 40
    guard[7] = 0;

    forwarded_blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_forwarded_blob(guard, 8, view));
    ::munmap(mapping, page_bytes * 2);
}

TEST(forwarded_blob, rejects_an_unaligned_buffer)
{
    const std::vector<std::uint8_t> blob = make_simple_blob();
    std::vector<std::uint8_t> storage(blob.size() + alignof(forwarded_blob_header));
    const std::size_t misalignment = reinterpret_cast<std::uintptr_t>(storage.data()) % alignof(forwarded_blob_header);
    const std::size_t offset = misalignment == 0 ? 1 : 0;
    std::uint8_t* unaligned = storage.data() + offset;
    ASSERT_NE(reinterpret_cast<std::uintptr_t>(unaligned) % alignof(forwarded_blob_header), 0U);

    std::memcpy(unaligned, blob.data(), blob.size());
    forwarded_blob_view view{};
    EXPECT_FALSE(
        winforward::wfp::try_open_forwarded_blob(unaligned, static_cast<std::uint32_t>(blob.size()), view));
}

TEST(forwarded_blob, rejects_a_truncated_blob)
{
    const std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_view view{};
    EXPECT_FALSE(winforward::wfp::try_open_forwarded_blob(
        blob.data(), static_cast<std::uint32_t>(blob.size() - 1), view));
}

TEST(forwarded_blob, rejects_a_buffer_larger_than_the_blob)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    blob.resize(blob.size() + 8, 0xAB);
    forwarded_blob_view view{};
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_header_field_violations)
{
    forwarded_blob_view view{};

    std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_header header = read_forwarded_header(blob);
    header.magic = 0;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.version = static_cast<std::uint16_t>(winforward::wfp::forwarded_blob_version + 1);
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.header_size = 16;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.header_size = static_cast<std::uint16_t>(sizeof(forwarded_blob_header) + 4);
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    // 兜底只允许 permit(0) / block(1) 这两个原始 u32 取值；redirect、未知值与高字节非零
    // 都必须整块拒绝 —— 先把 u32 截成 uint8 再比较会让 0x100 之类伪装成 permit。
    const std::uint32_t invalid_fallbacks[] = {
        static_cast<std::uint32_t>(rule_action::redirect),
        3U,
        0x100U,
        0x101U,
        0x10000U,
        0xFFFFFF00U,
        0xFFFFFFFFU,
    };
    for (const std::uint32_t fallback : invalid_fallbacks)
    {
        blob = make_simple_blob();
        header = read_forwarded_header(blob);
        header.fallback_action = fallback;
        write_forwarded_header(blob, header);
        EXPECT_FALSE(open_forwarded_blob(blob, view)) << "fallback_action=" << fallback;
    }

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.reserved = 1;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.reserved2 = 1;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_bad_segment_offsets)
{
    forwarded_blob_view view{};

    std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_header header = read_forwarded_header(blob);
    header.rules_offset = 41;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.rules_offset = 16;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.rules_offset = header.total_size + 4;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.network_pool_offset = 61;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.network_pool_offset = 16;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.network_pool_offset = header.total_size + 4;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.network_pool_size = header.total_size;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    header = read_forwarded_header(blob);
    header.rule_count = 0xFFFFFFFFU;
    write_forwarded_header(blob, header);
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_overlapping_rules_and_network_pool)
{
    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_any_rule();
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.remote_network_index = 0;
    builder.add_rule(entry);
    std::vector<std::uint8_t> blob = builder.build();

    // 网段记录从规则内的 protocol 字节开始：protocol=0、family=4、action=permit=0、
    // reserved=0 恰好是合法记录头，后面的地址字节补零。因此只有"两段重叠"能拒绝这个夹具。
    forwarded_blob_header header = read_forwarded_header(blob);
    header.network_pool_offset = header.rules_offset + 20;
    header.network_pool_size = 20;
    header.total_size = static_cast<std::uint32_t>(blob.size() + 16);
    blob.resize(header.total_size, 0);
    write_forwarded_header(blob, header);

    forwarded_blob_view view{};
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_a_network_pool_size_that_is_not_a_multiple_of_twenty)
{
    std::vector<std::uint8_t> blob = make_network_blob();
    forwarded_blob_header header = read_forwarded_header(blob);
    header.network_pool_size -= 1;
    write_forwarded_header(blob, header);
    forwarded_blob_view view{};
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_invalid_network_entries)
{
    forwarded_blob_view view{};

    wfp_test::forwarded_blob_builder builder;
    builder.add_network(5, 24, make_ipv4(10, 0, 0, 0));
    builder.add_rule(make_any_rule());
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));

    builder = wfp_test::forwarded_blob_builder{};
    builder.add_network(4, 33, make_ipv4(10, 0, 0, 0));
    builder.add_rule(make_any_rule());
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));

    builder = wfp_test::forwarded_blob_builder{};
    builder.add_network(6, 129, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    builder.add_rule(make_any_rule());
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));

    // 未被任何规则引用的坏记录同样要拒绝：网段池是整体校验的。
    network_prefix prefix{};
    prefix.prefix_length = 24;
    prefix.family = winforward::wfp::rule_family_ipv4;
    prefix.reserved[0] = 1;
    builder = wfp_test::forwarded_blob_builder{};
    builder.add_network(prefix);
    builder.add_rule(make_any_rule());
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));
}

TEST(forwarded_blob, rejects_unknown_rule_fields)
{
    forwarded_blob_view view{};

    std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_rule entry = read_forwarded_rule(blob, 0);
    entry.action = static_cast<rule_action>(3);
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.reserved = 1;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.protocol = 18;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.family = 5;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.remote_port_start = 2000;
    entry.remote_port_end = 1000;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_invalid_upstream_combinations)
{
    forwarded_blob_view view{};

    // redirect 必须带具体上游。
    std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_rule entry = read_forwarded_rule(blob, 0);
    entry.upstream_id = winforward::wfp::forwarded_upstream_none;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    // 非 redirect 不得带上游。
    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.action = rule_action::permit;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_simple_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.action = rule_action::block;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_out_of_range_or_misaligned_network_indexes)
{
    forwarded_blob_view view{};

    std::vector<std::uint8_t> blob = make_network_blob();
    forwarded_blob_header header = read_forwarded_header(blob);
    forwarded_blob_rule entry = read_forwarded_rule(blob, 0);
    entry.remote_network_index = header.network_pool_size;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_network_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.client_network_index = 60;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_network_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.remote_network_index = 24;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));

    blob = make_network_blob();
    entry = read_forwarded_rule(blob, 0);
    entry.client_network_index = 24;
    write_forwarded_rule(blob, 0, entry);
    EXPECT_FALSE(open_forwarded_blob(blob, view));
}

TEST(forwarded_blob, rejects_a_family_mismatch_between_rule_and_networks)
{
    forwarded_blob_view view{};

    wfp_test::forwarded_blob_builder builder;
    forwarded_blob_rule entry = make_any_rule();
    entry.family = winforward::wfp::rule_family_ipv4;
    entry.remote_network_index = builder.add_network(6, 64, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    builder.add_rule(entry);
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));

    builder = wfp_test::forwarded_blob_builder{};
    entry = make_any_rule();
    entry.family = winforward::wfp::rule_family_ipv6;
    entry.client_network_index = builder.add_network(4, 24, make_ipv4(10, 0, 0, 0));
    builder.add_rule(entry);
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));

    // 规则族是 any，但两个具体网段的族不同：同一个连接不可能落在两个族上。
    builder = wfp_test::forwarded_blob_builder{};
    entry = make_any_rule();
    entry.client_network_index = builder.add_network(4, 24, make_ipv4(10, 0, 0, 0));
    entry.remote_network_index = builder.add_network(6, 64, make_address({ 0x20, 0x01, 0x0d, 0xB8 }));
    builder.add_rule(entry);
    EXPECT_FALSE(open_forwarded_blob(builder.build(), view));
}

TEST(forwarded_blob, leaves_the_view_untouched_on_failure)
{
    std::vector<std::uint8_t> blob = make_simple_blob();
    forwarded_blob_header header = read_forwarded_header(blob);
    header.magic = 0;
    write_forwarded_header(blob, header);

    forwarded_blob_view view{};
    view.data = blob.data();
    view.size = 7;
    view.header = &header;
    view.rule_count = 9;
    EXPECT_FALSE(open_forwarded_blob(blob, view));
    EXPECT_EQ(view.data, blob.data());
    EXPECT_EQ(view.size, 7U);
    EXPECT_EQ(view.header, &header);
    EXPECT_EQ(view.rule_count, 9U);
}
