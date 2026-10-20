#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

// 探针自己的对象键。固定值便于出错后用 netsh wfp show 之类的工具对照，也便于重复
// load/unload 时定位残留对象（动态 session 正常退出时会自动清掉它们）。
constexpr GUID provider_key{ 0x3f6a1d24, 0x7c58, 0x4b9e, { 0x8d, 0x21, 0x6a, 0x0f, 0x5c, 0x3e, 0x91, 0x77 } };
constexpr GUID callout_key{ 0x4a7b2e35, 0x8d69, 0x5c0f, { 0x9e, 0x32, 0x7b, 0x10, 0x6d, 0x4f, 0xa2, 0x88 } };
constexpr GUID filter_key{ 0x5b8c3f46, 0x9e7a, 0x6d10, { 0xaf, 0x43, 0x8c, 0x21, 0x7e, 0x50, 0xb3, 0x99 } };

constexpr wchar_t device_path[] = L"\\Device\\WfProbe";
constexpr wchar_t symbolic_link_path[] = L"\\DosDevices\\WfProbe";
constexpr wchar_t provider_name[] = L"WinForward WFP Probe Provider";
constexpr wchar_t callout_name[] = L"WinForward WFP Probe Connect Redirect";
constexpr wchar_t callout_description[] = L"Phase 0 probe: ALE_CONNECT_REDIRECT_V4 only";
constexpr wchar_t filter_name[] = L"WinForward WFP Probe Redirect Filter";

probe_state g_probe_state{};

NTSTATUS dispatch_create_close(PDEVICE_OBJECT device_object, PIRP irp) noexcept;
NTSTATUS dispatch_device_control(PDEVICE_OBJECT device_object, PIRP irp) noexcept;

[[nodiscard]] bool acquire_lock(KIRQL& old_irql) noexcept
{
    const bool raised = KeGetCurrentIrql() < DISPATCH_LEVEL;
    if (raised)
        KeAcquireSpinLock(&g_probe_state.lock, &old_irql);
    else
        KeAcquireSpinLockAtDpcLevel(&g_probe_state.lock);
    return raised;
}

void release_lock(bool raised, KIRQL old_irql) noexcept
{
    if (raised)
        KeReleaseSpinLock(&g_probe_state.lock, old_irql);
    else
        KeReleaseSpinLockFromDpcLevel(&g_probe_state.lock);
}

NTSTATUS create_device(PDRIVER_OBJECT driver_object) noexcept
{
    UNICODE_STRING device_name{};
    RtlInitUnicodeString(&device_name, device_path);

    // 默认 DACL 是 "RW Everyone"（实测 accesschk），必须显式收紧：只允许 SYSTEM 与
    // Builtin\Administrators 完全控制，其他登录用户连打开设备都不行。
    PDEVICE_OBJECT device = nullptr;
    NTSTATUS status = IoCreateDeviceSecure(driver_object,
                                           0,
                                           &device_name,
                                           FILE_DEVICE_UNKNOWN,
                                           FILE_DEVICE_SECURE_OPEN,
                                           FALSE,
                                           &SDDL_DEVOBJ_SYS_ALL_ADM_ALL,
                                           nullptr,
                                           &device);
    if (!NT_SUCCESS(status))
        return status;

    g_probe_state.device_object = device;

    UNICODE_STRING link_name{};
    RtlInitUnicodeString(&link_name, symbolic_link_path);
    status = IoCreateSymbolicLink(&link_name, &device_name);
    if (!NT_SUCCESS(status))
    {
        IoDeleteDevice(device);
        g_probe_state.device_object = nullptr;
        return status;
    }

    driver_object->MajorFunction[IRP_MJ_CREATE] = dispatch_create_close;
    driver_object->MajorFunction[IRP_MJ_CLOSE] = dispatch_create_close;
    driver_object->MajorFunction[IRP_MJ_DEVICE_CONTROL] = dispatch_device_control;

    device->Flags |= DO_BUFFERED_IO;
    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}

void delete_device() noexcept
{
    if (g_probe_state.device_object == nullptr)
        return;

    UNICODE_STRING link_name{};
    RtlInitUnicodeString(&link_name, symbolic_link_path);
    IoDeleteSymbolicLink(&link_name);
    IoDeleteDevice(g_probe_state.device_object);
    g_probe_state.device_object = nullptr;
}

// 全部按注册的逆序拆除。用动态 session，即使某一步删除失败，engine 关闭时也会回收
// 本 session 添加的对象，避免开发过程中反复 load/unload 留下残留。
void stop_wfp() noexcept
{
    probe_state& state = g_probe_state;

    if (state.filter_added)
    {
        FwpmFilterDeleteById0(state.engine_handle, state.filter_id);
        state.filter_added = false;
    }
    if (state.callout_added)
    {
        FwpmCalloutDeleteByKey0(state.engine_handle, &callout_key);
        state.callout_added = false;
    }
    if (state.callout_registered)
    {
        // 只有“暂时忙”值得重试：classify 还在跑时返回 STATUS_DEVICE_BUSY（个别路径会给
        // STATUS_FWP_IN_USE）。STATUS_FWP_CALLOUT_NOT_FOUND 说明已经注销过，直接当成功。
        // 其他永久错误重试没有意义，无限重试只会把 sc stop 永久挂成 STOP_PENDING。
        constexpr std::uint32_t retry_limit = 200; // 10 ms * 200 = 2 s
        std::uint32_t retries = 0;
        NTSTATUS unregister_status = FwpsCalloutUnregisterById0(state.callout_id);
        while (!NT_SUCCESS(unregister_status) && retries < retry_limit &&
               (unregister_status == STATUS_DEVICE_BUSY || unregister_status == STATUS_FWP_IN_USE))
        {
            ++retries;
            if (retries == 1 || (retries % 100) == 0)
                DbgPrint("wfprobe: callout unregister busy (0x%08X), retry %u/%u\n",
                         unregister_status,
                         retries,
                         retry_limit);
            LARGE_INTEGER retry_interval{};
            retry_interval.QuadPart = -100000; // 10 ms
            KeDelayExecutionThread(KernelMode, FALSE, &retry_interval);
            unregister_status = FwpsCalloutUnregisterById0(state.callout_id);
        }

        if (NT_SUCCESS(unregister_status) || unregister_status == STATUS_FWP_CALLOUT_NOT_FOUND)
        {
            state.callout_registered = false;
        }
        else
        {
            // 按本驱动当前的不变量，这条兜底路径不可达：
            //   - STATUS_DEVICE_BUSY 只和 FwpsFlowAssociateContext0 关联的 flow context 有关，
            //     本驱动从不关联 flow context；
            //   - STATUS_FWP_IN_USE 需要并发的注册/注销，本驱动只有 DriverUnload 一个卸载线程。
            // 既然不可达，就不选“继续卸载”（会在引擎里留下指向已卸载代码的 callout，之后在任意
            // 上下文随机崩），而用主动 bugcheck 把“不可达”变成可验证的断言：假设被将来的改动
            // 打破时，崩在卸载路径上，参数直接指向问题点，而不是几个月后随机蓝屏。
            // 0x8000FFE0 是本探针自用的 bugcheck code（与 Windows 已用 code 不冲突）。
            // 参数：P1 = 最后一次注销的 NTSTATUS，P2 = callout id，P3 = 重试次数，P4 = 0。
            DbgPrint(
                "wfprobe: callout unregister failed 0x%08X after %u retries; bugchecking to avoid dangling callout\n",
                unregister_status,
                retries);
            KeBugCheckEx(0x8000FFE0,
                         static_cast<ULONG_PTR>(unregister_status),
                         static_cast<ULONG_PTR>(state.callout_id),
                         static_cast<ULONG_PTR>(retries),
                         0);
        }
    }
    if (state.redirect_handle != nullptr)
    {
        FwpsRedirectHandleDestroy0(state.redirect_handle);
        state.redirect_handle = nullptr;
    }
    if (state.provider_added)
    {
        FwpmProviderDeleteByKey0(state.engine_handle, &provider_key);
        state.provider_added = false;
    }
    if (state.engine_handle != nullptr)
    {
        FwpmEngineClose0(state.engine_handle);
        state.engine_handle = nullptr;
    }
}

NTSTATUS start_wfp() noexcept
{
    probe_state& state = g_probe_state;

    FWPM_SESSION0 session{};
    session.flags = FWPM_SESSION_FLAG_DYNAMIC;
    NTSTATUS status = FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session, &state.engine_handle);
    if (!NT_SUCCESS(status))
    {
        state.engine_handle = nullptr;
        return status;
    }

    status = FwpsRedirectHandleCreate0(&provider_key, 0, &state.redirect_handle);
    if (!NT_SUCCESS(status))
    {
        state.redirect_handle = nullptr;
        stop_wfp();
        return status;
    }

    FWPS_CALLOUT1 callout{};
    callout.calloutKey = callout_key;
    callout.flags = 0;
    callout.classifyFn = probe_classify_fn;
    callout.notifyFn = probe_notify_fn;
    callout.flowDeleteFn = nullptr;
    status = FwpsCalloutRegister1(state.device_object, &callout, &state.callout_id);
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }
    state.callout_registered = true;

    FWPM_PROVIDER0 provider{};
    provider.providerKey = provider_key;
    provider.displayData.name = const_cast<wchar_t*>(provider_name);
    status = FwpmProviderAdd0(state.engine_handle, &provider, nullptr);
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }
    state.provider_added = true;

    FWPM_CALLOUT0 callout_definition{};
    callout_definition.calloutKey = callout_key;
    callout_definition.displayData.name = const_cast<wchar_t*>(callout_name);
    callout_definition.displayData.description = const_cast<wchar_t*>(callout_description);
    callout_definition.providerKey = const_cast<GUID*>(&provider_key);
    callout_definition.applicableLayer = FWPM_LAYER_ALE_CONNECT_REDIRECT_V4;
    status = FwpmCalloutAdd0(state.engine_handle, &callout_definition, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }
    state.callout_added = true;

    FWPM_FILTER0 filter{};
    filter.filterKey = filter_key;
    filter.displayData.name = const_cast<wchar_t*>(filter_name);
    filter.layerKey = FWPM_LAYER_ALE_CONNECT_REDIRECT_V4;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);
    filter.weight.type = FWP_EMPTY;
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = callout_key;
    status = FwpmFilterAdd0(state.engine_handle, &filter, nullptr, &state.filter_id);
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }
    state.filter_added = true;

    return STATUS_SUCCESS;
}

NTSTATUS apply_config(const void* buffer, ULONG input_length) noexcept
{
    if (buffer == nullptr || input_length < sizeof(probe_config))
        return STATUS_INVALID_PARAMETER;

    probe_config candidate{};
    RtlCopyMemory(&candidate, buffer, sizeof(candidate));

    if (candidate.abi_version != probe_abi_version)
        return STATUS_REVISION_MISMATCH;
    if (candidate.mode != redirect_mode::documented_branch && candidate.mode != redirect_mode::force_loopback &&
        candidate.mode != redirect_mode::force_local)
        return STATUS_INVALID_PARAMETER;

    if (candidate.enabled != 0)
    {
        if (candidate.listener_port == 0 || candidate.listener_process_id == 0)
            return STATUS_INVALID_PARAMETER;
        if (candidate.mode == redirect_mode::force_local && candidate.local_address_override == 0)
            return STATUS_INVALID_PARAMETER;
    }

    KIRQL old_irql{};
    const bool raised = acquire_lock(old_irql);
    g_probe_state.config = candidate;
    release_lock(raised, old_irql);
    return STATUS_SUCCESS;
}

NTSTATUS get_config(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_config))
        return STATUS_BUFFER_TOO_SMALL;

    probe_config snapshot{};
    probe_take_config(snapshot);
    RtlCopyMemory(buffer, &snapshot, sizeof(snapshot));
    return STATUS_SUCCESS;
}

NTSTATUS get_stats(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_stats))
        return STATUS_BUFFER_TOO_SMALL;

    probe_stats snapshot{};
    RtlCopyMemory(&snapshot, &g_probe_state.stats, sizeof(snapshot));
    RtlCopyMemory(buffer, &snapshot, sizeof(snapshot));
    return STATUS_SUCCESS;
}

// 设备 DACL 已经把非管理员挡在 open 之外，这里再做一次调用者校验：即便以后 DACL 被改松，
// 也仍然只有 SYSTEM / Administrators 能下发配置。
[[nodiscard]] bool caller_is_admin() noexcept
{
    // PsReferencePrimaryToken/SeTokenIsAdmin 都是 PASSIVE_LEVEL only；用户态 IOCTL 必然是
    // PASSIVE，但内核调用者打开设备不走 DACL，理论上能在 DISPATCH_LEVEL 发 IOCTL，先挡掉。
    if (KeGetCurrentIrql() != PASSIVE_LEVEL)
        return false;

    PACCESS_TOKEN token = PsReferencePrimaryToken(PsGetCurrentProcess());
    if (token == nullptr)
        return false;
    const BOOLEAN is_admin = SeTokenIsAdmin(token);
    PsDereferencePrimaryToken(token);
    return is_admin != FALSE;
}

NTSTATUS dispatch_create_close(PDEVICE_OBJECT device_object, PIRP irp) noexcept
{
    UNREFERENCED_PARAMETER(device_object);
    irp->IoStatus.Status = STATUS_SUCCESS;
    irp->IoStatus.Information = 0;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

NTSTATUS dispatch_device_control(PDEVICE_OBJECT device_object, PIRP irp) noexcept
{
    UNREFERENCED_PARAMETER(device_object);

    if (!caller_is_admin())
    {
        irp->IoStatus.Status = STATUS_ACCESS_DENIED;
        irp->IoStatus.Information = 0;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
        return STATUS_ACCESS_DENIED;
    }

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    void* buffer = irp->AssociatedIrp.SystemBuffer;
    const ULONG input_length = stack->Parameters.DeviceIoControl.InputBufferLength;
    const ULONG output_length = stack->Parameters.DeviceIoControl.OutputBufferLength;
    ULONG_PTR information = 0;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    switch (stack->Parameters.DeviceIoControl.IoControlCode)
    {
    case ioctl_set_config:
        status = apply_config(buffer, input_length);
        break;

    case ioctl_get_config:
        status = get_config(buffer, output_length);
        if (NT_SUCCESS(status))
            information = sizeof(probe_config);
        break;

    case ioctl_get_stats:
        status = get_stats(buffer, output_length);
        if (NT_SUCCESS(status))
            information = sizeof(probe_stats);
        break;

    case ioctl_reset_stats:
        probe_reset_stats();
        status = STATUS_SUCCESS;
        break;

    case ioctl_get_events:
    {
        std::uint32_t event_count = 0;
        status = probe_read_events(buffer, output_length, event_count);
        if (NT_SUCCESS(status))
            information = static_cast<ULONG_PTR>(event_count) * sizeof(probe_event);
        break;
    }

    default:
        break;
    }

    irp->IoStatus.Status = status;
    irp->IoStatus.Information = information;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

} // namespace

probe_state& probe_state_ref() noexcept
{
    return g_probe_state;
}

void probe_take_config(probe_config& target) noexcept
{
    KIRQL old_irql{};
    const bool raised = acquire_lock(old_irql);
    target = g_probe_state.config;
    release_lock(raised, old_irql);
}

void probe_increment(std::uint64_t& counter) noexcept
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&counter));
}

void probe_log_event(probe_event event) noexcept
{
    KIRQL old_irql{};
    const bool raised = acquire_lock(old_irql);
    event.sequence = static_cast<std::uint32_t>(g_probe_state.event_total);
    g_probe_state.events[g_probe_state.event_write_index] = event;
    g_probe_state.event_write_index = (g_probe_state.event_write_index + 1) % event_capacity;
    ++g_probe_state.event_total;
    release_lock(raised, old_irql);
}

NTSTATUS probe_read_events(void* buffer, std::uint32_t buffer_size, std::uint32_t& event_count) noexcept
{
    event_count = 0;
    if (buffer == nullptr || buffer_size < sizeof(probe_event))
        return STATUS_BUFFER_TOO_SMALL;

    auto* target = static_cast<probe_event*>(buffer);
    const std::uint32_t capacity = buffer_size / sizeof(probe_event);

    KIRQL old_irql{};
    const bool raised = acquire_lock(old_irql);
    const std::uint32_t available = g_probe_state.event_total < event_capacity
                                        ? static_cast<std::uint32_t>(g_probe_state.event_total)
                                        : event_capacity;
    const std::uint32_t wanted = available < capacity ? available : capacity;
    const std::uint32_t first = (g_probe_state.event_write_index + event_capacity - wanted) % event_capacity;
    for (std::uint32_t index = 0; index < wanted; ++index)
        target[index] = g_probe_state.events[(first + index) % event_capacity];
    release_lock(raised, old_irql);

    event_count = wanted;
    return STATUS_SUCCESS;
}

void probe_reset_stats() noexcept
{
    KIRQL old_irql{};
    const bool raised = acquire_lock(old_irql);
    RtlZeroMemory(&g_probe_state.stats, sizeof(g_probe_state.stats));
    g_probe_state.stats.abi_version = probe_abi_version;
    g_probe_state.event_total = 0;
    g_probe_state.event_write_index = 0;
    RtlZeroMemory(g_probe_state.events, sizeof(g_probe_state.events));
    release_lock(raised, old_irql);
}

void probe_driver_unload(PDRIVER_OBJECT driver_object) noexcept
{
    UNREFERENCED_PARAMETER(driver_object);
    stop_wfp();
    delete_device();
}

NTSTATUS probe_driver_entry(PDRIVER_OBJECT driver_object) noexcept
{
    probe_state& state = probe_state_ref();
    KeInitializeSpinLock(&state.lock);
    state.stats.abi_version = probe_abi_version;
    state.config.abi_version = probe_abi_version;

    NTSTATUS status = create_device(driver_object);
    if (!NT_SUCCESS(status))
        return status;

    status = start_wfp();
    if (!NT_SUCCESS(status))
    {
        delete_device();
        return status;
    }

    driver_object->DriverUnload = probe_driver_unload;
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe

extern "C" NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT driver_object, _In_ PUNICODE_STRING registry_path)
{
    UNREFERENCED_PARAMETER(registry_path);
    return winforward::wfp::probe::probe_driver_entry(driver_object);
}
