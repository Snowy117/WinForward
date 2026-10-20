#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr ULONG ipv4_loopback_network_order = 0x0100007Fu; // 127.0.0.1 的 in_addr.S_un.S_addr 值
constexpr std::size_t ipv4_address_size = 4;

[[nodiscard]] UINT16 host_port(const SOCKADDR_IN& address) noexcept
{
    return RtlUshortByteSwap(address.sin_port);
}

[[nodiscard]] bool is_any_address(const SOCKADDR_IN& address) noexcept
{
    return address.sin_addr.S_un.S_addr == 0;
}

[[nodiscard]] probe_event make_event(const probe_config& config,
                                     UINT32 process_id,
                                     const SOCKADDR_IN& original,
                                     const SOCKADDR_IN& local,
                                     UINT16 redirect_port,
                                     probe_decision decision,
                                     redirect_branch branch) noexcept
{
    probe_event event{};
    // 显式清零，避免把结构体填充字节里的内核栈内容带回用户态。
    RtlZeroMemory(&event, sizeof(event));
    event.timestamp_100ns = KeQueryInterruptTime();
    event.process_id = process_id;
    RtlCopyMemory(event.original_address, &original.sin_addr, ipv4_address_size);
    RtlCopyMemory(event.local_address, &local.sin_addr, ipv4_address_size);
    event.original_port = host_port(original);
    event.local_port = host_port(local);
    event.redirect_port = redirect_port;
    event.decision = static_cast<std::uint8_t>(decision);
    event.mode = static_cast<std::uint8_t>(config.mode);
    event.branch = static_cast<std::uint8_t>(branch);
    event.address_family = static_cast<std::uint8_t>(AF_INET);
    return event;
}

// 依据 FwpsAcquireWritableLayerDataPointer0 的 Remarks：该函数会把 classifyOut->actionType 置为
// FWP_ACTION_BLOCK、把 rights 里的 FWPS_RIGHT_ACTION_WRITE 清掉，并且“每次调用都必须配对一次
// FwpsApplyModifiedLayerData0”。
//
// 我们的 filter 是 FWP_ACTION_CALLOUT_TERMINATING，不存在“留给低权重 callout 再改”的需求，
// 所以放行和拒绝都直接写终态：先置 actionType，再把 FWPS_RIGHT_ACTION_WRITE 清掉。
// 放行不清位会让后续 callout 还能改这条连接的决定；对 terminating filter 来说没有必要。
void finish_classify(UINT64 classify_handle, void* writable_data, FWPS_CLASSIFY_OUT0* classify_out, bool block) noexcept
{
    classify_out->actionType = block ? FWP_ACTION_BLOCK : FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
    FwpsApplyModifiedLayerData0(classify_handle, writable_data, 0);
    FwpsReleaseClassifyHandle0(classify_handle);
}

} // namespace

void NTAPI probe_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                             const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                             void* layer_data,
                             const void* classify_context,
                             const FWPS_FILTER1* filter,
                             UINT64 flow_context,
                             FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    UNREFERENCED_PARAMETER(flow_context);

    probe_state& state = probe_state_ref();
    probe_increment(state.stats.classify_calls);

    probe_config config{};
    probe_take_config(config);
    if (config.enabled == 0)
    {
        probe_increment(state.stats.permitted_disabled);
        return;
    }

    // 没有写权限时不能改 actionType，直接把决定权留给前一个 callout。
    if ((classify_out->rights & FWPS_RIGHT_ACTION_WRITE) == 0)
    {
        probe_increment(state.stats.permitted_no_write_right);
        return;
    }

    UINT32 process_id = 0;
    if (FWPS_IS_METADATA_FIELD_PRESENT(in_meta_values, FWPS_METADATA_FIELD_PROCESS_ID))
        process_id = static_cast<UINT32>(in_meta_values->processId);

    // PID 闸放在最前面：既是白名单，也避免给系统里每一条连接都分配内核池。
    if (config.target_process_id != 0 && process_id != config.target_process_id)
    {
        probe_increment(state.stats.permitted_wrong_process);
        return;
    }

    if (layer_data == nullptr)
    {
        probe_increment(state.stats.apply_failures);
        return;
    }

    auto* request = static_cast<FWPS_CONNECT_REQUEST0*>(layer_data);
    SOCKADDR_IN original{};
    SOCKADDR_IN local{};
    RtlCopyMemory(&original, &request->remoteAddressAndPort, sizeof(original));
    RtlCopyMemory(&local, &request->localAddressAndPort, sizeof(local));

    if (original.sin_family != AF_INET)
    {
        probe_increment(state.stats.permitted_non_tcp);
        return;
    }

    const UINT8 protocol = in_fixed_values->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_PROTOCOL].value.uint8;
    if (protocol != IPPROTO_TCP)
    {
        probe_increment(state.stats.permitted_non_tcp);
        return;
    }

    // 自流量闸：目标就是监听端口本身时不重定向，否则监听端的 accept 会被再次改写。
    if (host_port(original) == config.listener_port)
    {
        probe_increment(state.stats.permitted_listener_port);
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::permitted_listener_port,
                                   redirect_branch::none));
        return;
    }

    // 别的 callout 已经把这条连接改写过（previousVersion 里带 localRedirectHandle）时直接放行。
    // 这个判断只读 layerData，不需要 writable 副本，所以放在 acquire 之前，也就没有配对 apply 的问题。
    // 动作与 finish_classify 的放行终态一致：置 PERMIT 后清掉写权限；terminating filter 不需要
    // 把“改许可”的权利留给后面的 callout。
    if (request->previousVersion != nullptr && request->previousVersion->localRedirectHandle != nullptr)
    {
        probe_increment(state.stats.permitted_already_redirected);
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::permitted_already_redirected,
                                   redirect_branch::none));
        classify_out->actionType = FWP_ACTION_PERMIT;
        classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
        return;
    }

    FWPS_CONNECTION_REDIRECT_STATE redirect_state = FWPS_CONNECTION_NOT_REDIRECTED;
    if (FWPS_IS_METADATA_FIELD_PRESENT(in_meta_values, FWPS_METADATA_FIELD_REDIRECT_RECORD_HANDLE))
    {
        redirect_state =
            FwpsQueryConnectionRedirectState0(in_meta_values->redirectRecords, state.redirect_handle, nullptr);
    }

    const UINT32 condition_flags =
        in_fixed_values->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_FLAGS].value.uint32;
    if (redirect_state == FWPS_CONNECTION_REDIRECTED_BY_SELF ||
        redirect_state == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF ||
        (condition_flags & FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED) != 0)
    {
        probe_increment(state.stats.permitted_redirect_state);
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::permitted_redirect_state,
                                   redirect_branch::none));
        return;
    }

    UINT64 classify_handle = 0;
    NTSTATUS status = FwpsAcquireClassifyHandle0(const_cast<void*>(classify_context), 0, &classify_handle);
    if (!NT_SUCCESS(status))
    {
        probe_increment(state.stats.apply_failures);
        state.stats.last_status = status;
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::redirect_failed,
                                   redirect_branch::none));
        return;
    }

    void* writable_data = nullptr;
    status = FwpsAcquireWritableLayerDataPointer0(classify_handle, filter->filterId, 0, &writable_data, classify_out);
    if (!NT_SUCCESS(status) || writable_data == nullptr)
    {
        // 拿不到写副本时 classifyOut 可能已经被引擎改成 BLOCK；这里刻意不再恢复成 PERMIT：
        // 无法修改 layer data 就无法可靠地重定向，fail-closed 是这条失败路径的既定语义。
        FwpsReleaseClassifyHandle0(classify_handle);
        probe_increment(state.stats.apply_failures);
        state.stats.last_status = status;
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::redirect_failed,
                                   redirect_branch::none));
        return;
    }

    auto* writable_request = static_cast<FWPS_CONNECT_REQUEST0*>(writable_data);

    SOCKADDR_IN redirect{};
    redirect.sin_family = AF_INET;
    redirect.sin_port = RtlUshortByteSwap(config.listener_port);
    redirect_branch branch = redirect_branch::none;

    switch (config.mode)
    {
    case redirect_mode::documented_branch:
        if (is_any_address(local))
        {
            redirect.sin_addr.S_un.S_addr = ipv4_loopback_network_order;
            branch = redirect_branch::any_to_loopback;
        }
        else
        {
            redirect.sin_addr.S_un.S_addr = local.sin_addr.S_un.S_addr;
            branch = redirect_branch::local_address;
        }
        break;

    case redirect_mode::force_loopback:
        redirect.sin_addr.S_un.S_addr = ipv4_loopback_network_order;
        branch = redirect_branch::any_to_loopback;
        break;

    case redirect_mode::force_local:
        redirect.sin_addr.S_un.S_addr =
            is_any_address(local) ? config.local_address_override : local.sin_addr.S_un.S_addr;
        branch = redirect_branch::local_address;
        break;

    default:
        // 配置校验不会让非法 mode 走到这里；真到了这里按放行处理，避免一个内部状态把流量全拒掉。
        finish_classify(classify_handle, writable_data, classify_out, false);
        probe_increment(state.stats.apply_failures);
        probe_log_event(make_event(config,
                                   process_id,
                                   original,
                                   local,
                                   config.listener_port,
                                   probe_decision::redirect_failed,
                                   redirect_branch::none));
        return;
    }

    // 官方文档明确要求这块内存用 ExAllocatePoolWithTag 分配（所有权随后交给 WFP）；
    // 新版 WDK 把该 API 标成 deprecated，这里按文档写法并局部抑制掉这个提示。
#pragma warning(push)
#pragma warning(disable : 4996)
    auto* context =
        static_cast<redirect_context*>(ExAllocatePoolWithTag(NonPagedPoolNx, sizeof(redirect_context), pool_tag));
#pragma warning(pop)
    if (context == nullptr)
    {
        // 分配不到 context 就带不回原始目的地。这里刻意 fail-closed：仍然配对 apply
        // （此时数据还没被改），然后把这条连接 BLOCK。
        finish_classify(classify_handle, writable_data, classify_out, true);
        probe_increment(state.stats.context_alloc_failures);
        probe_log_event(make_event(
            config, process_id, original, local, config.listener_port, probe_decision::context_alloc_failed, branch));
        return;
    }
    probe_increment(state.stats.contexts_allocated);

    original_destination destination{};
    destination.family = address_family::ipv4;
    destination.protocol = transport_protocol::tcp;
    destination.port = host_port(original);
    RtlCopyMemory(destination.address, &original.sin_addr, ipv4_address_size);
    *context = make_redirect_context(destination, process_id, 0);

    // 从 Windows 8 起，这三个字段是本地重定向的全部硬要求：目标的 PID、redirect handle、
    // 以及一块由 ExAllocatePoolWithTag 分配、所有权随后交给 WFP 的 context。
    writable_request->localRedirectTargetPID = config.listener_process_id;
    writable_request->localRedirectHandle = state.redirect_handle;
    writable_request->localRedirectContext = context;
    writable_request->localRedirectContextSize = sizeof(redirect_context);
    RtlCopyMemory(&writable_request->remoteAddressAndPort, &redirect, sizeof(redirect));

    finish_classify(classify_handle, writable_data, classify_out, false);

    probe_increment(state.stats.redirected);
    probe_log_event(
        make_event(config, process_id, original, local, config.listener_port, probe_decision::redirected, branch));
}

NTSTATUS NTAPI probe_notify_fn(FWPS_CALLOUT_NOTIFY_TYPE notify_type,
                               const GUID* filter_key,
                               FWPS_FILTER1* filter) noexcept
{
    UNREFERENCED_PARAMETER(notify_type);
    UNREFERENCED_PARAMETER(filter_key);
    UNREFERENCED_PARAMETER(filter);
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe
