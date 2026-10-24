#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <initializer_list>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "rule_blob.h"
#include "rule_blob_builder.h"

namespace
{

using wfp_test::open_blob;
using wfp_test::read_header;
using wfp_test::read_rule;
using wfp_test::read_selector;
using wfp_test::write_header;
using wfp_test::write_selector;
using winforward::wfp::blob_view;
using winforward::wfp::network_prefix;
using winforward::wfp::port_range;
using winforward::wfp::process_selector;
using winforward::wfp::process_selector_kind;
using winforward::wfp::rule;
using winforward::wfp::rule_action;
using winforward::wfp::rule_blob_header;

using mutation = std::function<void(std::vector<std::uint8_t>&)>;

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

std::uint8_t* process_bytes(std::vector<std::uint8_t>& blob)
{
    return blob.data() + read_header(blob).process_pool_offset;
}

std::vector<std::uint8_t>
make_blob(std::string_view selector_text, process_selector_kind kind, bool is_directory = false)
{
    wfp_test::rule_blob_builder builder;
    rule entry = wfp_test::make_any_rule();
    entry.process_first = builder.add_selector(selector_text, kind, is_directory);
    entry.process_count = 1;
    entry.network_first = builder.add_network(winforward::wfp::rule_family_ipv4, 24, make_ipv4(10, 0, 0, 0));
    entry.network_count = 1;
    entry.port_first = builder.add_port(443, 443);
    entry.port_count = 1;
    builder.add_rule(entry);
    return builder.build();
}

std::vector<std::uint8_t> make_base_blob()
{
    return make_blob("app.exe", file_name);
}

std::vector<std::uint8_t> make_path_blob()
{
    return make_blob(R"(\device\harddiskvolume3\app.exe)", path);
}

// 表驱动的负向夹具：每个夹具只制造一条校验失败；需要多处写入或重建 blob 的用例
// 单独列在 special_violations() 里。
enum class patch_target : std::uint8_t
{
    header,
    rule,
    selector,
    network,
    port,
    process_pool
};

struct layout_patch
{
    patch_target target;
    std::size_t offset;
    std::uint8_t width;
    std::uint32_t value;
    bool relative;

    constexpr layout_patch(patch_target patch_target_value,
                           std::size_t offset_value,
                           std::uint8_t width_value,
                           std::uint32_t value_value,
                           bool relative_value = false) noexcept
        : target(patch_target_value)
        , offset(offset_value)
        , width(width_value)
        , value(value_value)
        , relative(relative_value)
    {
    }
};

struct layout_case
{
    std::string_view name;
    layout_patch first;
    layout_patch second;
    bool has_second;

    constexpr layout_case(std::string_view case_name, const layout_patch& first_patch)
        : name(case_name)
        , first(first_patch)
        , second(first_patch)
        , has_second(false)
    {
    }

    constexpr layout_case(std::string_view case_name, const layout_patch& first_patch, const layout_patch& second_patch)
        : name(case_name)
        , first(first_patch)
        , second(second_patch)
        , has_second(true)
    {
    }

    constexpr layout_case(std::string_view case_name,
                          const layout_patch& first_patch,
                          const layout_patch& second_patch,
                          bool)
        : layout_case(case_name, first_patch, second_patch)
    {
    }
};

std::uint32_t read_little(const std::uint8_t* source, std::uint8_t width)
{
    std::uint32_t value = 0;
    for (std::uint8_t index = 0; index < width; ++index)
    {
        value |= static_cast<std::uint32_t>(source[index]) << (8U * index);
    }
    return value;
}

void write_little(std::uint8_t* destination, std::uint32_t value, std::uint8_t width)
{
    for (std::uint8_t index = 0; index < width; ++index)
    {
        destination[index] = static_cast<std::uint8_t>((value >> (8U * index)) & 0xFFU);
    }
}

void apply_patch(std::vector<std::uint8_t>& blob, const rule_blob_header& header, const layout_patch& patch)
{
    std::size_t base = 0;
    switch (patch.target)
    {
    case patch_target::header:
        break;
    case patch_target::rule:
        base = header.rules_offset;
        break;
    case patch_target::selector:
        base = header.selector_pool_offset;
        break;
    case patch_target::network:
        base = header.network_pool_offset;
        break;
    case patch_target::port:
        base = header.port_pool_offset;
        break;
    case patch_target::process_pool:
        base = header.process_pool_offset;
        break;
    }

    std::uint8_t* destination = blob.data() + base + patch.offset;
    std::uint32_t value = patch.value;
    if (patch.relative)
    {
        value += read_little(destination, patch.width);
    }
    write_little(destination, value, patch.width);
}

const std::vector<layout_case>& layout_violations()
{
    static const std::vector<layout_case> cases = {
        { "bad-magic", { patch_target::header, offsetof(rule_blob_header, magic), 4, 0 } },
        { "bad-version", { patch_target::header, offsetof(rule_blob_header, version), 2, 3 } },
        { "bad-header-size", { patch_target::header, offsetof(rule_blob_header, header_size), 2, 40 } },
        { "total-size-mismatch", { patch_target::header, offsetof(rule_blob_header, total_size), 4, 4, true } },
        { "bad-fallback-action", { patch_target::header, offsetof(rule_blob_header, fallback_action), 4, 3 } },
        { "rule-count-overrun", { patch_target::header, offsetof(rule_blob_header, rule_count), 4, 0xFFFFFFFFU } },
        // 钉住“kind == file_name 时 is_directory 必须为 0”：1 不在范围检查之外，
        // 因此只触发这一条（范围检查由 special_violations() 的
        // selector-is-directory-invalid 用路径型 + 2 单独覆盖）。
        { "file-name-with-directory-flag", { patch_target::selector, offsetof(process_selector, is_directory), 1, 1 } },
        { "selector-reserved-nonzero", { patch_target::selector, offsetof(process_selector, reserved), 1, 1 } },
        { "selector-string-offset-inside-string",
          { patch_target::selector, offsetof(process_selector, string_offset), 4, 1, true } },
        { "selector-invalid-utf8",
          { patch_target::process_pool, 0, 1, 0xC3 },
          { patch_target::process_pool, 1, 1, 0x28 },
          true },
        { "selector-control-character", { patch_target::process_pool, 0, 1, 0x09 } },
        { "selector-control-character-c1",
          { patch_target::process_pool, 0, 1, 0xC2 },
          { patch_target::process_pool, 1, 1, 0x80 },
          true },
        { "file-name-with-separator", { patch_target::process_pool, 3, 1, '\\' } },
        { "port-start-after-end",
          { patch_target::port, offsetof(port_range, start), 2, 400 },
          { patch_target::port, offsetof(port_range, end), 2, 300 },
          true },
        { "network-family-unknown", { patch_target::network, offsetof(network_prefix, family), 1, 5 } },
        { "network-prefix-too-long", { patch_target::network, offsetof(network_prefix, prefix_length), 1, 33 } },
        { "network-reserved-nonzero", { patch_target::network, offsetof(network_prefix, reserved), 1, 1 } },
        { "network-family-mask-mismatch",
          { patch_target::network, offsetof(network_prefix, family), 1, winforward::wfp::rule_family_ipv6 },
          { patch_target::rule, offsetof(rule, family_mask), 1, winforward::wfp::rule_family_mask_ipv4 },
          true },
        { "rule-process-range-overrun", { patch_target::rule, offsetof(rule, process_count), 4, 2 } },
        { "rule-network-range-overrun", { patch_target::rule, offsetof(rule, network_count), 4, 2 } },
        { "rule-port-range-overrun", { patch_target::rule, offsetof(rule, port_count), 4, 2 } },
        { "rule-process-count-zero-first-nonzero",
          { patch_target::rule, offsetof(rule, process_count), 4, 0 },
          { patch_target::rule, offsetof(rule, process_first), 4, 1 },
          true },
        { "rule-network-count-zero-first-nonzero",
          { patch_target::rule, offsetof(rule, network_count), 4, 0 },
          { patch_target::rule, offsetof(rule, network_first), 4, 1 },
          true },
        { "rule-port-count-zero-first-nonzero",
          { patch_target::rule, offsetof(rule, port_count), 4, 0 },
          { patch_target::rule, offsetof(rule, port_first), 4, 1 },
          true },
        { "rule-protocol-mask-too-large", { patch_target::rule, offsetof(rule, protocol_mask), 1, 4 } },
        { "rule-family-mask-too-large", { patch_target::rule, offsetof(rule, family_mask), 1, 4 } },
        { "rule-action-unknown", { patch_target::rule, offsetof(rule, action), 1, 3 } },
        { "rule-reserved-nonzero", { patch_target::rule, offsetof(rule, reserved), 1, 1 } },
        { "rule-reserved2-nonzero", { patch_target::rule, offsetof(rule, reserved2), 4, 1 } },
    };
    return cases;
}

const std::vector<std::pair<std::string_view, mutation>>& special_violations()
{
    static const std::vector<std::pair<std::string_view, mutation>> cases = {
        { "rules-offset-beyond-blob",
          [](std::vector<std::uint8_t>& blob)
          {
              rule_blob_header header = read_header(blob);
              header.rules_offset = header.total_size + 4;
              write_header(blob, header);
          } },
        { "process-pool-beyond-blob",
          [](std::vector<std::uint8_t>& blob)
          {
              rule_blob_header header = read_header(blob);
              header.process_pool_size = header.total_size;
              write_header(blob, header);
          } },
        { "selector-kind-unknown",
          [](std::vector<std::uint8_t>& blob)
          {
              blob = make_path_blob();
              process_selector selector = read_selector(blob, 0);
              selector.kind = static_cast<process_selector_kind>(2);
              write_selector(blob, 0, selector);
          } },
        { "selector-is-directory-invalid",
          [](std::vector<std::uint8_t>& blob)
          {
              // 钉住“is_directory <= 1”这条范围检查：路径型 + 2 不会同时触发
              // “文件名型必须写 0”那条（该条由 file-name-with-directory-flag 覆盖）。
              blob = make_path_blob();
              process_selector selector = read_selector(blob, 0);
              selector.is_directory = 2;
              write_selector(blob, 0, selector);
          } },
        { "selector-string-offset-out-of-range",
          [](std::vector<std::uint8_t>& blob)
          {
              process_selector selector = read_selector(blob, 0);
              selector.string_offset = read_header(blob).process_pool_size;
              write_selector(blob, 0, selector);
          } },
        { "segment-inside-header",
          [](std::vector<std::uint8_t>& blob)
          {
              const wfp_test::rule_blob_builder builder;
              blob = builder.build();
              rule_blob_header header = read_header(blob);
              header.process_pool_offset = 4;
              header.process_pool_size = 4;
              write_header(blob, header);
          } },
        { "rules-offset-unaligned-with-slack",
          [](std::vector<std::uint8_t>& blob)
          {
              const rule entry = read_rule(blob, 0);
              const std::size_t unaligned = blob.size() + 1;
              blob.resize(unaligned + sizeof(rule), 0);
              std::memcpy(blob.data() + unaligned, &entry, sizeof(entry));
              rule_blob_header header = read_header(blob);
              header.rules_offset = static_cast<std::uint32_t>(unaligned);
              header.total_size = static_cast<std::uint32_t>(blob.size());
              write_header(blob, header);
          } },
        { "selector-port-overlap",
          [](std::vector<std::uint8_t>& blob)
          {
              wfp_test::rule_blob_builder builder;
              rule entry = wfp_test::make_any_rule();
              entry.process_first = builder.add_selector("app.exe", file_name);
              entry.process_count = 1;
              entry.port_first = builder.add_port(0, 0);
              entry.port_count = 1;
              builder.add_rule(entry);
              blob = builder.build();
              rule_blob_header header = read_header(blob);
              header.port_pool_offset = header.selector_pool_offset;
              write_header(blob, header);
          } },
        { "selector-pool-not-divisible",
          [](std::vector<std::uint8_t>& blob)
          {
              wfp_test::rule_blob_builder builder;
              builder.add_selector("app.exe", file_name);
              blob = builder.build();
              rule_blob_header header = read_header(blob);
              header.selector_pool_size -= 1;
              write_header(blob, header);
          } },
        { "network-pool-not-divisible",
          [](std::vector<std::uint8_t>& blob)
          {
              wfp_test::rule_blob_builder builder;
              builder.add_network(winforward::wfp::rule_family_ipv4, 24, make_ipv4(10, 0, 0, 0));
              blob = builder.build();
              rule_blob_header header = read_header(blob);
              header.network_pool_size -= 1;
              write_header(blob, header);
          } },
        { "port-pool-not-divisible",
          [](std::vector<std::uint8_t>& blob)
          {
              wfp_test::rule_blob_builder builder;
              builder.add_port(443, 443);
              blob = builder.build();
              rule_blob_header header = read_header(blob);
              header.port_pool_size -= 2;
              write_header(blob, header);
          } },
        { "process-pool-tail-not-nul",
          [](std::vector<std::uint8_t>& blob)
          {
              const wfp_test::rule_blob_builder builder;
              blob = builder.build();
              blob.resize(sizeof(rule_blob_header) + 4, 'x');
              rule_blob_header header = read_header(blob);
              header.total_size = static_cast<std::uint32_t>(blob.size());
              header.process_pool_offset = static_cast<std::uint32_t>(sizeof(rule_blob_header));
              header.process_pool_size = 4;
              write_header(blob, header);
          } },
        { "selector-too-long",
          [](std::vector<std::uint8_t>& blob)
          {
              blob = make_blob(std::string(512, 'a'), file_name);
          } },
        { "selector-empty",
          [](std::vector<std::uint8_t>& blob)
          {
              blob = make_blob("", file_name);
          } },
        { "path-without-leading-backslash",
          [](std::vector<std::uint8_t>& blob)
          {
              blob = make_path_blob();
              process_bytes(blob)[0] = 'x';
          } },
        { "path-without-device-prefix",
          [](std::vector<std::uint8_t>& blob)
          {
              blob = make_blob(R"(\harddiskvolume3\app.exe)", path);
          } },
        { "path-with-trailing-backslash",
          [](std::vector<std::uint8_t>& blob)
          {
              const std::string text = R"(\device\harddiskvolume3\app.exe)";
              blob = make_blob(text, path);
              process_bytes(blob)[text.size() - 1] = '\\';
          } },
    };
    return cases;
}

} // namespace

TEST(rule_blob, accepts_device_prefix_case_insensitively)
{
    const std::vector<std::uint8_t> blob = make_blob(R"(\DEVICE\harddiskvolume3\app.exe)", path);
    blob_view view{};
    ASSERT_TRUE(open_blob(blob, view));
    ASSERT_EQ(view.selector_pool_count, 1U);
    EXPECT_EQ(view.selector_pool[0].kind, path);
}

TEST(rule_blob, rejects_each_v2_layout_violation)
{
    for (const layout_case& item : layout_violations())
    {
        SCOPED_TRACE(item.name);
        std::vector<std::uint8_t> blob = make_base_blob();
        const rule_blob_header header = read_header(blob);
        apply_patch(blob, header, item.first);
        if (item.has_second)
        {
            apply_patch(blob, header, item.second);
        }
        blob_view view{};
        EXPECT_FALSE(open_blob(blob, view)) << "夹具必须只违反一条 v2 校验：" << item.name;
    }

    for (const auto& [name, mutate] : special_violations())
    {
        SCOPED_TRACE(name);
        std::vector<std::uint8_t> blob = make_base_blob();
        mutate(blob);
        blob_view view{};
        EXPECT_FALSE(open_blob(blob, view)) << "夹具必须只违反一条 v2 校验：" << name;
    }
}
