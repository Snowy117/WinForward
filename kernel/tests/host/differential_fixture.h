#pragma once

#include <array>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <expected>
#include <filesystem>
#include <fstream>
#include <optional>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

#include "rule_blob.h"

// 跨语言差分测试的夹具读取器，测试专用，不属于产品头。
//
// 夹具由 src/userland 的 C# 测试生成：一份 manifest.txt 加每个用例一个 .blob。
// 清单是 TAB 分隔的行记录（UTF-8，LF），# 开头为注释：
//
//   version<TAB>1
//   case<TAB><名字><TAB><描述>
//   blob<TAB><文件名>
//   rules<TAB><条数>
//   fallback<TAB>permit|block|redirect
//   connect<TAB><地址 hex><TAB><端口><TAB><协议><TAB><族><TAB><进程路径或 -><TAB>
//           <是否命中 0|1><TAB><规则序号或 -><TAB><动作><TAB><标签>
//
// 地址 hex 在 ipv4 时是 4 字节、ipv6 时是 16 字节；进程路径里不会出现 TAB 与换行。

namespace wfp_test
{

struct fixture_connection
{
    std::array<std::uint8_t, winforward::wfp::network_address_size> address{};
    std::uint16_t port = 0;
    std::uint8_t protocol = 0;
    std::uint8_t family = 0;
    std::string process_path;
    bool has_process_path = false;
    bool expected_matched = false;
    std::uint32_t expected_rule_index = 0;
    winforward::wfp::rule_action expected_action = winforward::wfp::rule_action::permit;
    std::string label;
};

struct fixture_case
{
    std::string name;
    std::string description;
    std::string blob_file;
    std::uint32_t rule_count = 0;
    winforward::wfp::rule_action fallback_action = winforward::wfp::rule_action::permit;
    std::vector<fixture_connection> connections;
    bool has_blob = false;
    bool has_rule_count = false;
    bool has_fallback = false;
};

namespace detail
{

[[nodiscard]] inline std::vector<std::string_view> split_fields(std::string_view line)
{
    std::vector<std::string_view> fields;
    std::size_t start = 0;
    while (true)
    {
        const std::size_t tab = line.find('\t', start);
        if (tab == std::string_view::npos)
        {
            fields.push_back(line.substr(start));
            return fields;
        }
        fields.push_back(line.substr(start, tab - start));
        start = tab + 1;
    }
}

template<typename value_type> [[nodiscard]] inline bool parse_integer(std::string_view text, value_type& out) noexcept
{
    if (text.empty())
    {
        return false;
    }
    const auto result = std::from_chars(text.data(), text.data() + text.size(), out);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size();
}

[[nodiscard]] inline std::optional<winforward::wfp::rule_action> parse_action(std::string_view text) noexcept
{
    if (text == "permit")
    {
        return winforward::wfp::rule_action::permit;
    }
    if (text == "block")
    {
        return winforward::wfp::rule_action::block;
    }
    if (text == "redirect")
    {
        return winforward::wfp::rule_action::redirect;
    }
    return std::nullopt;
}

[[nodiscard]] inline std::optional<std::uint8_t> parse_family(std::string_view text) noexcept
{
    if (text == "ipv4")
    {
        return winforward::wfp::rule_family_ipv4;
    }
    if (text == "ipv6")
    {
        return winforward::wfp::rule_family_ipv6;
    }
    return std::nullopt;
}

[[nodiscard]] inline bool parse_hex(std::string_view text, std::uint8_t* out, std::size_t size) noexcept
{
    if (text.size() != size * 2U)
    {
        return false;
    }

    const auto digit = [](char character) -> int
    {
        if (character >= '0' && character <= '9')
        {
            return character - '0';
        }
        if (character >= 'a' && character <= 'f')
        {
            return character - 'a' + 10;
        }
        return -1;
    };

    for (std::size_t index = 0; index < size; ++index)
    {
        const int high = digit(text[index * 2U]);
        const int low = digit(text[(index * 2U) + 1U]);
        if (high < 0 || low < 0)
        {
            return false;
        }
        out[index] = static_cast<std::uint8_t>((high << 4) | low);
    }
    return true;
}

} // namespace detail

// blob 缓冲区按 4 字节对齐分配：try_open_blob 会直接按 rule_blob_header 解引用。
class blob_buffer
{
  public:
    [[nodiscard]] static std::expected<blob_buffer, std::string> load(const std::filesystem::path& path)
    {
        std::ifstream stream(path, std::ios::binary | std::ios::ate);
        if (!stream.is_open())
        {
            return std::unexpected("无法打开 blob：" + path.string());
        }

        const std::streamoff end = stream.tellg();
        if (end <= 0)
        {
            return std::unexpected("blob 是空文件：" + path.string());
        }

        const std::size_t byte_size = static_cast<std::size_t>(end);
        blob_buffer buffer;
        buffer.words.assign((byte_size + sizeof(std::uint32_t) - 1U) / sizeof(std::uint32_t), 0U);
        buffer.byte_size = static_cast<std::uint32_t>(byte_size);

        stream.seekg(0, std::ios::beg);
        if (!stream.read(reinterpret_cast<char*>(buffer.words.data()), static_cast<std::streamsize>(byte_size)))
        {
            return std::unexpected("读取 blob 失败：" + path.string());
        }
        return buffer;
    }

    [[nodiscard]] const std::uint8_t* data() const noexcept
    {
        return reinterpret_cast<const std::uint8_t*>(words.data());
    }

    [[nodiscard]] std::uint32_t size() const noexcept
    {
        return byte_size;
    }

  private:
    std::vector<std::uint32_t> words;
    std::uint32_t byte_size = 0;
};

[[nodiscard]] inline std::expected<std::vector<fixture_case>, std::string>
load_manifest(const std::filesystem::path& manifest_path)
{
    std::ifstream stream(manifest_path, std::ios::binary);
    if (!stream.is_open())
    {
        return std::unexpected("无法打开夹具清单：" + manifest_path.string());
    }

    std::vector<fixture_case> cases;
    std::string line;
    std::size_t line_number = 0;
    bool version_seen = false;
    bool case_open = false;

    while (std::getline(stream, line))
    {
        ++line_number;
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }
        if (line.empty() || line.front() == '#')
        {
            continue;
        }

        const std::vector<std::string_view> fields = detail::split_fields(line);
        const auto fail = [&line_number](std::string_view message)
        {
            return std::unexpected("夹具清单第 " + std::to_string(line_number) + " 行：" + std::string(message));
        };

        if (fields.front() == "version")
        {
            if (fields.size() != 2 || fields[1] != "1")
            {
                return fail("version 行必须是 version<TAB>1");
            }
            version_seen = true;
            continue;
        }

        if (fields.front() == "case")
        {
            if (!version_seen)
            {
                return fail("case 之前缺少 version 行");
            }
            if (fields.size() < 3)
            {
                return fail("case 行应有名字与描述");
            }
            fixture_case item;
            item.name = std::string(fields[1]);
            item.description = std::string(fields[2]);
            cases.push_back(std::move(item));
            case_open = true;
            continue;
        }

        if (!case_open)
        {
            return fail("blob / rules / fallback / connect 必须出现在某个 case 之后");
        }
        fixture_case& current = cases.back();

        if (fields.front() == "blob")
        {
            if (fields.size() != 2)
            {
                return fail("blob 行应有 1 个文件名");
            }
            current.blob_file = std::string(fields[1]);
            current.has_blob = true;
            continue;
        }

        if (fields.front() == "rules")
        {
            if (fields.size() != 2 || !detail::parse_integer(fields[1], current.rule_count))
            {
                return fail("rules 行应有十进制条数");
            }
            current.has_rule_count = true;
            continue;
        }

        if (fields.front() == "fallback")
        {
            const std::optional<winforward::wfp::rule_action> action =
                fields.size() == 2 ? detail::parse_action(fields[1]) : std::nullopt;
            if (!action.has_value())
            {
                return fail("fallback 行必须是 permit / block / redirect");
            }
            current.fallback_action = *action;
            current.has_fallback = true;
            continue;
        }

        if (fields.front() == "connect")
        {
            if (fields.size() != 10)
            {
                return fail("connect 行应有 10 个字段（记录类型 + 9 个数据字段）");
            }

            fixture_connection connection;
            const std::optional<std::uint8_t> family = detail::parse_family(fields[4]);
            if (!family.has_value())
            {
                return fail("connect 的族必须是 ipv4 或 ipv6");
            }
            connection.family = *family;

            const bool ipv4 = *family == winforward::wfp::rule_family_ipv4;
            const std::size_t address_size = ipv4 ? 4U : winforward::wfp::network_address_size;
            if (!detail::parse_hex(fields[1], connection.address.data(), address_size))
            {
                return fail("connect 的地址不是期望长度的十六进制");
            }
            if (!detail::parse_integer(fields[2], connection.port))
            {
                return fail("connect 的端口不是整数");
            }
            if (!detail::parse_integer(fields[3], connection.protocol))
            {
                return fail("connect 的协议不是整数");
            }
            if (fields[5] != "-")
            {
                connection.process_path = std::string(fields[5]);
                connection.has_process_path = true;
            }
            if (fields[6] == "1")
            {
                connection.expected_matched = true;
            }
            else if (fields[6] != "0")
            {
                return fail("connect 的命中标志必须是 0 或 1");
            }
            if (connection.expected_matched)
            {
                if (!detail::parse_integer(fields[7], connection.expected_rule_index))
                {
                    return fail("命中的 connect 行必须给出规则序号");
                }
            }
            else
            {
                if (fields[7] != "-")
                {
                    return fail("未命中的 connect 行规则序号必须是 -");
                }
                connection.expected_rule_index = winforward::wfp::rule_pool_index_any;
            }

            const std::optional<winforward::wfp::rule_action> action = detail::parse_action(fields[8]);
            if (!action.has_value())
            {
                return fail("connect 的期望动作必须是 permit / block / redirect");
            }
            connection.expected_action = *action;
            connection.label = std::string(fields[9]);
            current.connections.push_back(std::move(connection));
            continue;
        }

        return fail("无法识别的记录类型：" + std::string(fields.front()));
    }

    if (stream.bad())
    {
        return std::unexpected("读取夹具清单失败：" + manifest_path.string());
    }
    if (!version_seen)
    {
        return std::unexpected("夹具清单缺少 version 行");
    }
    if (cases.empty())
    {
        return std::unexpected("夹具清单里没有任何 case");
    }

    for (const fixture_case& item : cases)
    {
        if (!item.has_blob || !item.has_rule_count || !item.has_fallback)
        {
            return std::unexpected("用例 " + item.name + " 缺少 blob / rules / fallback 行");
        }
        if (item.connections.empty())
        {
            return std::unexpected("用例 " + item.name + " 没有任何 connect 行");
        }
    }
    return cases;
}

} // namespace wfp_test
