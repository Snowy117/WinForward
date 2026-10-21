#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

// 本驱动自己的对象键。固定值便于出错后用 netsh wfp show 对照，也便于重复 load/unload
// 时定位残留对象（动态 session 正常退出时会自动清掉它们）。

// V4 与 V6 各注册一个 callout：FWPM_CALLOUT0 只有一个 applicableLayer，calloutKey 也必须唯一。
// 只挂 V4 会漏掉 dual-mode socket 的连接（本地端点是 [::ffff:...]），见 spec §7.5。
constexpr GUID callout_key_v4{ 0x4a7b2e35, 0x8d69, 0x5c0f, { 0x9e, 0x32, 0x7b, 0x10, 0x6d, 0x4f, 0xa2, 0x88 } };
constexpr GUID callout_key_v6{ 0x6c9d4057, 0xaf8b, 0x7e21, { 0xb0, 0x54, 0x9d, 0x32, 0x8f, 0x61, 0xc4, 0xaa } };
constexpr GUID filter_key_v4{ 0x5b8c3f46, 0x9e7a, 0x6d10, { 0xaf, 0x43, 0x8c, 0x21, 0x7e, 0x50, 0xb3, 0x99 } };
constexpr GUID filter_key_v6{ 0x7da05168, 0xb09c, 0x8f32, { 0xc1, 0x65, 0xae, 0x43, 0x90, 0x72, 0xd5, 0xbb } };

constexpr wchar_t device_path[] = L"\\Device\\WfProbe";
constexpr wchar_t symbolic_link_path[] = L"\\DosDevices\\WfProbe";
constexpr wchar_t provider_name[] = L"WinForward WFP Redirect Provider";
constexpr wchar_t callout_name_v4[] = L"WinForward ALE_CONNECT_REDIRECT_V4";
constexpr wchar_t callout_name_v6[] = L"WinForward ALE_CONNECT_REDIRECT_V6";
constexpr wchar_t callout_description[] = L"WinForward rule-driven connect redirect (V4 + V6)";
constexpr wchar_t filter_name_v4[] = L"WinForward Redirect Filter V4";
constexpr wchar_t filter_name_v6[] = L"WinForward Redirect Filter V6";

probe_state g_probe_state{};

NTSTATUS create_device(PDRIVER_OBJECT driver_object) noexcept
{
    UNICODE_STRING device_name{};
    RtlInitUnicodeString(&device_name, device_path);

    // 默认 DACL 是 "RW Everyone"（Phase 0 实测 accesschk），必须显式收紧：只允许 SYSTEM 与
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
    {
        return status;
    }

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

    // C28175：赋值发生在 DriverEntry 调用链上的辅助函数里（create_device），分析器不认为此时处于
    // 允许访问 DRIVER_OBJECT 成员的上下文（DRIVER_INITIALIZE / DRIVER_UNLOAD）；WDM 允许自填 MajorFunction 表。
#pragma warning(suppress : 28175)
    driver_object->MajorFunction[IRP_MJ_CREATE] = dispatch_create_close;
    // C28175：同上，CLOSE 与 CREATE 共用同一例程；赋值位置同样不在分析器认可的上下文里。
#pragma warning(suppress : 28175)
    driver_object->MajorFunction[IRP_MJ_CLOSE] = dispatch_create_close;
    // C28175：同上，DEVICE_CONTROL 是本驱动唯一的控制码入口；赋值位置同样不在分析器认可的上下文里。
#pragma warning(suppress : 28175)
    driver_object->MajorFunction[IRP_MJ_DEVICE_CONTROL] = dispatch_device_control;

    device->Flags |= DO_BUFFERED_IO;
    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}

void delete_device() noexcept
{
    if (g_probe_state.device_object == nullptr)
    {
        return;
    }

    UNICODE_STRING link_name{};
    RtlInitUnicodeString(&link_name, symbolic_link_path);
    IoDeleteSymbolicLink(&link_name);
    IoDeleteDevice(g_probe_state.device_object);
    g_probe_state.device_object = nullptr;
}

// 注销只对「暂时忙」重试：classify 还在跑时返回 STATUS_DEVICE_BUSY（个别路径会给
// STATUS_FWP_IN_USE）。STATUS_FWP_CALLOUT_NOT_FOUND 说明已经注销过，直接当成功。
// 其他永久错误重试没有意义，无限重试只会把 sc stop 永久挂成 STOP_PENDING。
void unregister_callout(layer_state& layer) noexcept
{
    if (!layer.callout_registered)
    {
        return;
    }

    constexpr std::uint32_t retry_limit = 200; // 10 ms * 200 = 2 s
    std::uint32_t retries = 0;
    NTSTATUS unregister_status = FwpsCalloutUnregisterById0(layer.callout_id);
    while (!NT_SUCCESS(unregister_status) && retries < retry_limit &&
           (unregister_status == STATUS_DEVICE_BUSY || unregister_status == STATUS_FWP_IN_USE))
    {
        ++retries;
        if (retries == 1 || (retries % 100) == 0)
        {
            DbgPrint("wfprobe: callout %u unregister busy (0x%08X), retry %u/%u\n",
                     layer.callout_id,
                     unregister_status,
                     retries,
                     retry_limit);
        }
        LARGE_INTEGER retry_interval{};
        retry_interval.QuadPart = -100000; // 10 ms
        KeDelayExecutionThread(KernelMode, FALSE, &retry_interval);
        unregister_status = FwpsCalloutUnregisterById0(layer.callout_id);
    }

    if (NT_SUCCESS(unregister_status) || unregister_status == STATUS_FWP_CALLOUT_NOT_FOUND)
    {
        layer.callout_registered = false;
        return;
    }

    // 按本驱动当前的不变量，这条兜底路径不可达：
    //   - STATUS_DEVICE_BUSY 只和 FwpsFlowAssociateContext0 关联的 flow context 有关，
    //     本驱动从不关联 flow context；
    //   - STATUS_FWP_IN_USE 需要并发的注册/注销，本驱动只有 DriverUnload 一个卸载线程。
    // 既然不可达，就不选「继续卸载」（会在引擎里留下指向已卸载代码的 callout，之后在任意
    // 上下文随机崩），而用主动 bugcheck 把「不可达」变成可验证的断言。
    // 0x8000FFE0 是本驱动自用的 bugcheck code（与 Windows 已用 code 不冲突）。
    DbgPrint("wfprobe: callout %u unregister failed 0x%08X after %u retries; bugchecking\n",
             layer.callout_id,
             unregister_status,
             retries);
    KeBugCheckEx(0x8000FFE0,
                 static_cast<ULONG_PTR>(unregister_status),
                 static_cast<ULONG_PTR>(layer.callout_id),
                 static_cast<ULONG_PTR>(retries),
                 0);
}

void remove_layer_filter(layer_state& layer) noexcept
{
    if (layer.filter_added)
    {
        FwpmFilterDeleteById0(g_probe_state.engine_handle, layer.filter_id);
        layer.filter_added = false;
    }
}

void unregister_callout_layer(layer_state& layer, const GUID& callout_key) noexcept
{
    remove_layer_filter(layer);
    if (layer.callout_added)
    {
        FwpmCalloutDeleteByKey0(g_probe_state.engine_handle, &callout_key);
        layer.callout_added = false;
    }
    unregister_callout(layer);
}

// 全部按注册的逆序拆除。用动态 session，即使某一步删除失败，engine 关闭时也会回收
// 本 session 添加的对象，避免开发过程中反复 load/unload 留下残留。
void stop_wfp() noexcept
{
    probe_state& state = g_probe_state;
    udp_probe_stop();

    probe_delete_block_filters(state.block_filter_ids, state.block_filter_count);
    unregister_callout_layer(state.v6, callout_key_v6);
    unregister_callout_layer(state.v4, callout_key_v4);

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

NTSTATUS register_callout_layer(layer_state& layer,
                                const GUID& callout_key,
                                const GUID& layer_key,
                                FWPS_CALLOUT_CLASSIFY_FN1 classify_fn,
                                const wchar_t* callout_name) noexcept
{
    probe_state& state = g_probe_state;

    FWPS_CALLOUT1 callout{};
    callout.calloutKey = callout_key;
    callout.flags = 0;
    callout.classifyFn = classify_fn;
    callout.notifyFn = probe_notify_fn;
    callout.flowDeleteFn = nullptr;
    NTSTATUS status = FwpsCalloutRegister1(state.device_object, &callout, &layer.callout_id);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    layer.callout_registered = true;

    FWPM_CALLOUT0 callout_definition{};
    callout_definition.calloutKey = callout_key;
    callout_definition.displayData.name = const_cast<wchar_t*>(callout_name);
    callout_definition.displayData.description = const_cast<wchar_t*>(callout_description);
    callout_definition.providerKey = const_cast<GUID*>(&provider_key);
    callout_definition.applicableLayer = layer_key;
    status = FwpmCalloutAdd0(state.engine_handle, &callout_definition, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    layer.callout_added = true;
    return STATUS_SUCCESS;
}

NTSTATUS add_layer_filter(layer_state& layer,
                          const GUID& filter_key,
                          const GUID& layer_key,
                          const GUID& callout_key,
                          const wchar_t* filter_name,
                          bool explicit_weight) noexcept
{
    FWPM_FILTER0 filter{};
    filter.filterKey = filter_key;
    filter.displayData.name = const_cast<wchar_t*>(filter_name);
    filter.layerKey = layer_key;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);
    if (explicit_weight)
    {
        // 同子层内的显式次序。跨子层由 FWPM_SUBLAYER_UNIVERSAL 的权重决定：Windows 防火墙与
        // 第三方安全产品都在更高权重的子层里，先于本 filter 仲裁，因此这里的终止型 PERMIT
        // 不会截胡它们的拒绝（tools/windows/firewall-check.ps1 有实证）。FWP_EMPTY 会把权重交给
        // 引擎按行动作推导，明确取值是为了让这条次序不随引擎默认行为漂移。
        filter.weight.type = FWP_UINT64;
        filter.weight.uint64 = const_cast<UINT64*>(&auth_filter_weight);
    }
    else
    {
        filter.weight.type = FWP_EMPTY;
    }
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = callout_key;

    probe_state& state = g_probe_state;
    NTSTATUS status = FwpmFilterAdd0(state.engine_handle, &filter, nullptr, &layer.filter_id);
    if (NT_SUCCESS(status))
    {
        layer.filter_added = true;
    }
    return status;
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

    // redirect 层：callout 与 filter 常驻，负责重定向/放行以及四道闸。
    status = register_callout_layer(
        state.v4, callout_key_v4, FWPM_LAYER_ALE_CONNECT_REDIRECT_V4, probe_classify_fn, callout_name_v4);
    if (NT_SUCCESS(status))
    {
        status = add_layer_filter(
            state.v4, filter_key_v4, FWPM_LAYER_ALE_CONNECT_REDIRECT_V4, callout_key_v4, filter_name_v4, false);
    }
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }

    status = register_callout_layer(
        state.v6, callout_key_v6, FWPM_LAYER_ALE_CONNECT_REDIRECT_V6, probe_classify_fn, callout_name_v6);
    if (NT_SUCCESS(status))
    {
        // <mutation id="v6-filter-not-added">
        status = add_layer_filter(
            state.v6, filter_key_v6, FWPM_LAYER_ALE_CONNECT_REDIRECT_V6, callout_key_v6, filter_name_v6, false);
        // </mutation>
    }
    if (!NT_SUCCESS(status))
    {
        stop_wfp();
        return status;
    }

    udp_probe_start(state.engine_handle);
    return STATUS_SUCCESS;
}

} // namespace

probe_state& probe_state_ref() noexcept
{
    return g_probe_state;
}

_IRQL_requires_max_(DISPATCH_LEVEL) _IRQL_saves_global_(OldIrql, old_irql)
    _IRQL_raises_(DISPATCH_LEVEL) bool probe_lock(KIRQL& old_irql) noexcept
{
    const bool raised = KeGetCurrentIrql() < DISPATCH_LEVEL;
    if (raised)
    {
        KeAcquireSpinLock(&g_probe_state.lock, &old_irql);
    }
    else
    {
        KeAcquireSpinLockAtDpcLevel(&g_probe_state.lock);
    }
    return raised;
}

_IRQL_requires_(DISPATCH_LEVEL)
    _IRQL_restores_global_(OldIrql, old_irql) void probe_unlock(bool raised, KIRQL old_irql) noexcept
{
    if (raised)
    {
        KeReleaseSpinLock(&g_probe_state.lock, old_irql);
    }
    else
    {
        KeReleaseSpinLockFromDpcLevel(&g_probe_state.lock);
    }
}

void probe_driver_unload(PDRIVER_OBJECT driver_object) noexcept
{
    UNREFERENCED_PARAMETER(driver_object);
    // 先注销 callout（之后不可能再有 classify），再释放规则表，最后删设备。
    // 与 rule IOCTL 共用同一把互斥量：卸载期间不会再有人增删 block filter。
    KeWaitForSingleObject(&g_probe_state.rule_mutex, Executive, KernelMode, FALSE, nullptr);
    stop_wfp();
    probe_release_all_rules();
    KeReleaseMutex(&g_probe_state.rule_mutex, FALSE);
    delete_device();
}

NTSTATUS probe_driver_entry(PDRIVER_OBJECT driver_object) noexcept
{
    probe_state& state = probe_state_ref();
    KeInitializeSpinLock(&state.lock);
    KeInitializeMutex(&state.rule_mutex, 0);
    state.stats.abi_version = probe_abi_version;
    state.config.abi_version = probe_abi_version;

    NTSTATUS status = create_device(driver_object);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = start_wfp();
    if (!NT_SUCCESS(status))
    {
        delete_device();
        return status;
    }

    // C28175：赋值在 probe_driver_entry 辅助函数里，分析器不认为处于 DRIVER_UNLOAD 的允许上下文；WDM 通过 DriverUnload
    // 挂卸载回调是文档化写法。
#pragma warning(suppress : 28175)
    driver_object->DriverUnload = probe_driver_unload;
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe

// 角色类型声明：CodeQL 的 WdmDriverEntry 与 PREfast 的 C28175 都按声明项上的
// DRIVER_INITIALIZE typedef 判定「是否处于 DriverEntry 上下文」，只有 SAL 注解不够。
// 少了这条声明，辅助函数里给 DriverObject 成员赋值的语句会被判成越权访问。
extern "C" DRIVER_INITIALIZE DriverEntry;

extern "C" _Function_class_(DRIVER_INITIALIZE) _IRQL_requires_same_ _IRQL_requires_(PASSIVE_LEVEL)
NTSTATUS
DriverEntry(_In_ PDRIVER_OBJECT driver_object, _In_ PUNICODE_STRING registry_path)
{
    UNREFERENCED_PARAMETER(registry_path);
    return winforward::wfp::probe::probe_driver_entry(driver_object);
}
