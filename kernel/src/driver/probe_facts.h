#pragma once

// host classify 的连接事实采集：从 FWPS_CONNECT_REQUEST0 的 layer data 与固定字段里取出匹配、
// 重定向与诊断需要的全部事实。probe_classify.cpp 只消费这里的结果。

#include "probe_driver.h"

namespace winforward::wfp::probe
{

// remote / local 保留 WFP 的原始 SOCKADDR_STORAGE：redirect target 按它的族选层。
// remote_address 是 WFP 原值（诊断事件用），matcher_remote_address 是归一后交给 matcher 的
// 地址（IPv4 只用前 4 字节）。family 是 AF_INET / AF_INET6，matcher_family 是 address_family
// 编码的 4 / 6。
struct connection_facts
{
    SOCKADDR_STORAGE remote;
    SOCKADDR_STORAGE local;
    std::uint8_t remote_address[16];
    std::uint8_t local_address[16];
    std::uint8_t matcher_remote_address[16];
    UINT16 remote_port;
    UINT16 local_port;
    std::uint8_t protocol;
    std::uint8_t family;
    std::uint8_t matcher_family;
    UINT32 process_id;
    UINT32 condition_flags;
    char process_path[max_process_path_length];
    std::uint32_t process_path_length;
};

// layer data 不是已知的 redirect 层、或地址族与层不符时返回 false。
[[nodiscard]] bool gather_facts(const FWPS_INCOMING_VALUES0* values,
                                const FWPS_INCOMING_METADATA_VALUES0* metadata,
                                void* layer_data,
                                connection_facts& facts) noexcept;

} // namespace winforward::wfp::probe
