#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

struct ipv4_header
{
    std::uint8_t version_ihl;
    std::uint8_t tos;
    std::uint16_t total_length;
    std::uint16_t identification;
    std::uint16_t flags_fragment;
    std::uint8_t ttl;
    std::uint8_t protocol;
    std::uint16_t checksum;
    std::uint32_t source;
    std::uint32_t destination;
};

struct udp_header
{
    std::uint16_t source_port;
    std::uint16_t destination_port;
    std::uint16_t length;
    std::uint16_t checksum;
};

struct inject_allocation
{
    PVOID buffer;
    PMDL mdl;
};

HANDLE g_injection_handle = nullptr;
NDIS_HANDLE g_nbl_pool = nullptr;

[[nodiscard]] std::uint16_t ip_checksum(const std::uint8_t* data, std::size_t length) noexcept
{
    std::uint32_t sum = 0;
    for (std::size_t index = 0; index + 1 < length; index += 2)
    {
        sum += (static_cast<std::uint32_t>(data[index]) << 8) | data[index + 1];
    }
    if ((length & 1U) != 0)
    {
        sum += static_cast<std::uint32_t>(data[length - 1]) << 8;
    }
    while ((sum >> 16) != 0)
    {
        sum = (sum & 0xFFFFU) + (sum >> 16);
    }
    return static_cast<std::uint16_t>(~sum);
}

void NTAPI inject_complete(void* context, NET_BUFFER_LIST* net_buffer_list, BOOLEAN dispatch_level) noexcept
{
    UNREFERENCED_PARAMETER(dispatch_level);
    auto* allocation = static_cast<inject_allocation*>(context);
    if (net_buffer_list != nullptr)
    {
        if (!NT_SUCCESS(net_buffer_list->Status))
        {
            udp_probe_note_return_status(static_cast<std::uint32_t>(net_buffer_list->Status));
        }
        FwpsFreeNetBufferList0(net_buffer_list);
    }
    if (allocation != nullptr)
    {
        if (allocation->mdl != nullptr)
        {
            IoFreeMdl(allocation->mdl);
        }
        if (allocation->buffer != nullptr)
        {
            ExFreePoolWithTag(allocation->buffer, pool_tag);
        }
        ExFreePoolWithTag(allocation, pool_tag);
    }
}

} // namespace

void udp_inject_start() noexcept
{
    if (g_injection_handle == nullptr)
    {
        NTSTATUS status = FwpsInjectionHandleCreate0(AF_INET, FWPS_INJECTION_TYPE_NETWORK, &g_injection_handle);
        if (!NT_SUCCESS(status))
        {
            g_injection_handle = nullptr;
            DbgPrint("wfudp: injection handle create failed 0x%08X\n", status);
        }
    }

    if (g_nbl_pool == nullptr)
    {
        NET_BUFFER_LIST_POOL_PARAMETERS parameters{};
        parameters.Header.Type = NDIS_OBJECT_TYPE_DEFAULT;
        parameters.Header.Revision = NET_BUFFER_LIST_POOL_PARAMETERS_REVISION_1;
        parameters.Header.Size = NDIS_SIZEOF_NET_BUFFER_LIST_POOL_PARAMETERS_REVISION_1;
        parameters.ProtocolId = NDIS_PROTOCOL_ID_DEFAULT;
        parameters.fAllocateNetBuffer = TRUE;
        parameters.ContextSize = 0;
        parameters.PoolTag = pool_tag;
        parameters.DataSize = 0;
        g_nbl_pool = NdisAllocateNetBufferListPool(nullptr, &parameters);
    }
}

void udp_inject_stop() noexcept
{
    if (g_injection_handle != nullptr)
    {
        FwpsInjectionHandleDestroy0(g_injection_handle);
        g_injection_handle = nullptr;
    }
    if (g_nbl_pool != nullptr)
    {
        NdisFreeNetBufferListPool(g_nbl_pool);
        g_nbl_pool = nullptr;
    }
}

bool udp_probe_return_packet(const udp_probe_return_header& header, const std::uint8_t* payload) noexcept
{
    if (g_injection_handle == nullptr || payload == nullptr || header.family != 4 || header.payload_length > 65507)
    {
        udp_probe_note_return_status(0xC0000001U);
        return false;
    }

    const std::uint32_t total_length = sizeof(ipv4_header) + sizeof(udp_header) + header.payload_length;
    auto* allocation =
        static_cast<inject_allocation*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(inject_allocation), pool_tag));
    if (allocation == nullptr)
    {
        udp_probe_note_return_status(0xC000009AU);
        return false;
    }
    RtlZeroMemory(allocation, sizeof(*allocation));

    allocation->buffer = ExAllocatePool2(POOL_FLAG_NON_PAGED, total_length, pool_tag);
    if (allocation->buffer == nullptr)
    {
        ExFreePoolWithTag(allocation, pool_tag);
        udp_probe_note_return_status(0xC000009AU);
        return false;
    }

    allocation->mdl = IoAllocateMdl(allocation->buffer, total_length, FALSE, FALSE, nullptr);
    if (allocation->mdl == nullptr)
    {
        ExFreePoolWithTag(allocation->buffer, pool_tag);
        ExFreePoolWithTag(allocation, pool_tag);
        udp_probe_note_return_status(0xC000009AU);
        return false;
    }
    MmBuildMdlForNonPagedPool(allocation->mdl);

    NET_BUFFER_LIST* net_buffer_list = nullptr;
    NTSTATUS status =
        FwpsAllocateNetBufferAndNetBufferList0(g_nbl_pool, 0, 0, allocation->mdl, 0, total_length, &net_buffer_list);
    if (!NT_SUCCESS(status) || net_buffer_list == nullptr)
    {
        IoFreeMdl(allocation->mdl);
        ExFreePoolWithTag(allocation->buffer, pool_tag);
        ExFreePoolWithTag(allocation, pool_tag);
        udp_probe_note_return_status(static_cast<std::uint32_t>(status));
        return false;
    }

    auto* data = static_cast<std::uint8_t*>(allocation->buffer);
    RtlZeroMemory(data, total_length);
    auto* ip = reinterpret_cast<ipv4_header*>(data);
    ip->version_ihl = 0x45;
    ip->total_length = RtlUshortByteSwap(static_cast<std::uint16_t>(total_length));
    ip->ttl = 64;
    ip->protocol = IPPROTO_UDP;
    ip->source = header.original_address;
    ip->destination = header.app_address;
    ip->checksum = ip_checksum(data, sizeof(ipv4_header));

    auto* udp = reinterpret_cast<udp_header*>(data + sizeof(ipv4_header));
    udp->source_port = RtlUshortByteSwap(header.original_port);
    udp->destination_port = RtlUshortByteSwap(header.app_port);
    udp->length = RtlUshortByteSwap(static_cast<std::uint16_t>(sizeof(udp_header) + header.payload_length));
    udp->checksum = 0;

    RtlCopyMemory(data + sizeof(ipv4_header) + sizeof(udp_header), payload, header.payload_length);

    status = FwpsInjectNetworkReceiveAsync0(g_injection_handle,
                                            nullptr,
                                            0,
                                            static_cast<COMPARTMENT_ID>(header.compartment_id),
                                            header.interface_index,
                                            0,
                                            net_buffer_list,
                                            inject_complete,
                                            allocation);
    if (!NT_SUCCESS(status))
    {
        FwpsFreeNetBufferList0(net_buffer_list);
        IoFreeMdl(allocation->mdl);
        ExFreePoolWithTag(allocation->buffer, pool_tag);
        ExFreePoolWithTag(allocation, pool_tag);
        udp_probe_note_return_status(static_cast<std::uint32_t>(status));
        return false;
    }
    return true;
}

} // namespace winforward::wfp::probe
