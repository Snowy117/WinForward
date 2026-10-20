#include "probe_driver.h"

namespace winforward::wfp::probe
{
// 规则的进程选择器是 UTF-8 字节串（用户态编译产物），连接侧从 ALE_APP_ID 取到的却是
// UTF-16LE 的完整路径。这里做一次手工的 UTF-16 -> UTF-8 转换：纯算术、无分配，可以在
// DISPATCH_LEVEL 跑（RtlUnicodeToUTF8N 只允许 PASSIVE_LEVEL，不能用）。
// 只折叠 ASCII A-Z（与 matcher.h 的 ascii_lower 一致）；代理对按标准展开。
// 输出是 UTF-8 字节数上限 max_process_path_length（含结尾 NUL 时最多 511 字节）；
// 超限或遇到孤立代理项时整条路径按不匹配处理，绝不截断。
void collect_process_path(const void* data, std::uint32_t byte_count, char* target, std::uint32_t& length) noexcept
{
    length = 0;
    target[0] = '\0';
    if (data == nullptr || byte_count < 2)
    {
        return;
    }

    const auto* units = static_cast<const std::uint16_t*>(data);
    const std::uint32_t unit_count = byte_count / 2;
    std::uint32_t index = 0;
    while (index < unit_count)
    {
        std::uint32_t code_point = units[index++];
        if (code_point == 0)
        {
            break;
        }
        if (code_point >= 0xD800 && code_point <= 0xDBFF)
        {
            if (index >= unit_count)
            {
                length = 0;
                target[0] = '\0';
                return;
            }
            const std::uint32_t low = units[index++];
            if (low < 0xDC00 || low > 0xDFFF)
            {
                length = 0;
                target[0] = '\0';
                return;
            }
            code_point = 0x10000 + ((code_point - 0xD800) << 10) + (low - 0xDC00);
        }
        else if (code_point >= 0xDC00 && code_point <= 0xDFFF)
        {
            length = 0;
            target[0] = '\0';
            return;
        }

        std::uint8_t encoded[4]{};
        std::uint32_t encoded_length = 0;
        if (code_point < 0x80)
        {
            encoded[0] = static_cast<std::uint8_t>(code_point);
            encoded_length = 1;
        }
        else if (code_point < 0x800)
        {
            encoded[0] = static_cast<std::uint8_t>(0xC0 | (code_point >> 6));
            encoded[1] = static_cast<std::uint8_t>(0x80 | (code_point & 0x3F));
            encoded_length = 2;
        }
        else if (code_point < 0x10000)
        {
            encoded[0] = static_cast<std::uint8_t>(0xE0 | (code_point >> 12));
            encoded[1] = static_cast<std::uint8_t>(0x80 | ((code_point >> 6) & 0x3F));
            encoded[2] = static_cast<std::uint8_t>(0x80 | (code_point & 0x3F));
            encoded_length = 3;
        }
        else
        {
            encoded[0] = static_cast<std::uint8_t>(0xF0 | (code_point >> 18));
            encoded[1] = static_cast<std::uint8_t>(0x80 | ((code_point >> 12) & 0x3F));
            encoded[2] = static_cast<std::uint8_t>(0x80 | ((code_point >> 6) & 0x3F));
            encoded[3] = static_cast<std::uint8_t>(0x80 | (code_point & 0x3F));
            encoded_length = 4;
        }

        if (length + encoded_length + 1 > max_process_path_length)
        {
            length = 0;
            target[0] = '\0';
            return;
        }
        for (std::uint32_t byte = 0; byte < encoded_length; ++byte)
        {
            target[length] = static_cast<char>(detail::ascii_lower(encoded[byte]));
            ++length;
        }
    }
    target[length] = '\0';
}

void probe_measure_process_path(const void* data,
                                std::uint32_t byte_count,
                                char* target,
                                std::uint32_t& length) noexcept
{
    collect_process_path(data, byte_count, target, length);
}

} // namespace winforward::wfp::probe
