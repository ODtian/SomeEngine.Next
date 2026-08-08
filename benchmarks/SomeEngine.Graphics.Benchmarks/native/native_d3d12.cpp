#include "native_d3d12.h"

#include <Windows.h>
#include <TlHelp32.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <iomanip>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string_view>

using Microsoft::WRL::ComPtr;

namespace someengine::graphics::benchmark
{
namespace
{
constexpr UINT sdk_version = 619;
constexpr UINT render_width = 64;
constexpr UINT render_height = 64;
constexpr UINT64 texture_byte_size = render_width * render_height * 4ULL;
constexpr UINT64 constant_buffer_alignment = 256;

bool normalized_float_equal(float left, float right) noexcept
{
    return left == right || (std::isnan(left) && std::isnan(right));
}

void verify_state_equality_contract()
{
    const float negative_zero = std::bit_cast<float>(std::uint32_t{0x80000000U});
    const float first_nan = std::bit_cast<float>(std::uint32_t{0x7FC00001U});
    const float second_nan = std::bit_cast<float>(std::uint32_t{0xFFC01234U});
    if (!normalized_float_equal(0.0F, negative_zero) ||
        !normalized_float_equal(first_nan, second_nan) ||
        normalized_float_equal(1.0F, 2.0F))
    {
        throw std::runtime_error("Native state shadow does not implement the fixed normalized float equality contract.");
    }
}

void check(HRESULT result, std::string_view operation)
{
    if (FAILED(result))
    {
        std::ostringstream message;
        message << operation << " failed with HRESULT 0x" << std::hex << std::uppercase
                << static_cast<std::uint32_t>(result) << '.';
        throw std::runtime_error(message.str());
    }
}

std::int64_t qpc()
{
    LARGE_INTEGER value{};
    if (!QueryPerformanceCounter(&value))
        throw std::runtime_error("QueryPerformanceCounter failed.");
    return value.QuadPart;
}

std::int64_t qpc_frequency()
{
    LARGE_INTEGER value{};
    if (!QueryPerformanceFrequency(&value))
        throw std::runtime_error("QueryPerformanceFrequency failed.");
    return value.QuadPart;
}

double ticks_to_microseconds(std::int64_t ticks)
{
    static const double scale = 1'000'000.0 / static_cast<double>(qpc_frequency());
    return static_cast<double>(ticks) * scale;
}

struct calibrated_timestamp
{
    std::int64_t cpu_counter = 0;
    std::int64_t cpu_frequency = 0;
    std::uint64_t queue_counter = 0;
    std::uint64_t queue_frequency = 0;
};

class native_queue
{
public:
    native_queue() = default;

    native_queue(ID3D12Device* device, D3D12_COMMAND_LIST_TYPE type)
        : type_(type)
    {
        D3D12_COMMAND_QUEUE_DESC description{};
        description.Type = type;
        description.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
        description.NodeMask = 1;
        check(device->CreateCommandQueue(&description, IID_PPV_ARGS(&queue_)), "ID3D12Device::CreateCommandQueue");
        check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence_)), "ID3D12Device::CreateFence");
        check(device->CreateCommandAllocator(type, IID_PPV_ARGS(&allocator_)), "ID3D12Device::CreateCommandAllocator");
        check(device->CreateCommandList(0, type, allocator_.Get(), nullptr, IID_PPV_ARGS(&list_)),
            "ID3D12Device::CreateCommandList");
        check(list_->Close(), "ID3D12GraphicsCommandList::Close(initial)");
        event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (event_ == nullptr)
            throw std::runtime_error("CreateEventW failed.");
        check(queue_->GetTimestampFrequency(&frequency_), "ID3D12CommandQueue::GetTimestampFrequency");
    }

    native_queue(const native_queue&) = delete;
    native_queue& operator=(const native_queue&) = delete;
    native_queue(native_queue&&) = delete;
    native_queue& operator=(native_queue&&) = delete;

    ID3D12GraphicsCommandList* begin()
    {
        check(allocator_->Reset(), "ID3D12CommandAllocator::Reset");
        check(list_->Reset(allocator_.Get(), nullptr), "ID3D12GraphicsCommandList::Reset");
        return list_.Get();
    }

    ID3D12GraphicsCommandList7* enhanced_list() const noexcept { return list_.Get(); }

    std::uint64_t execute()
    {
        check(list_->Close(), "ID3D12GraphicsCommandList::Close");
        ID3D12CommandList* lists[]{list_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        return signal();
    }

    std::uint64_t signal_only()
    {
        return signal();
    }

    void wait_gpu(const native_queue& source, std::uint64_t value)
    {
        check(queue_->Wait(source.fence_.Get(), value), "ID3D12CommandQueue::Wait");
    }

    void wait_cpu(std::uint64_t value)
    {
        if (fence_->GetCompletedValue() >= value)
            return;
        check(fence_->SetEventOnCompletion(value, event_.get()), "ID3D12Fence::SetEventOnCompletion");
        const DWORD result = WaitForSingleObject(event_.get(), 30'000);
        if (result != WAIT_OBJECT_0)
            throw std::runtime_error("D3D12 fence wait timed out.");
    }

    calibrated_timestamp calibrate() const
    {
        calibrated_timestamp result{};
        check(queue_->GetClockCalibration(&result.queue_counter,
            reinterpret_cast<UINT64*>(&result.cpu_counter)), "ID3D12CommandQueue::GetClockCalibration");
        result.cpu_frequency = qpc_frequency();
        result.queue_frequency = frequency_;
        return result;
    }

    ID3D12CommandQueue* queue() const noexcept { return queue_.Get(); }

private:
    struct close_handle
    {
        void operator()(void* value) const noexcept
        {
            if (value != nullptr)
                CloseHandle(value);
        }
    };

    std::uint64_t signal()
    {
        if (next_value_ == UINT64_MAX)
            throw std::runtime_error("The native fence domain is exhausted.");
        const std::uint64_t value = next_value_++;
        check(queue_->Signal(fence_.Get(), value), "ID3D12CommandQueue::Signal");
        return value;
    }

    D3D12_COMMAND_LIST_TYPE type_ = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<ID3D12Fence> fence_;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList7> list_;
    std::unique_ptr<void, close_handle> event_;
    std::uint64_t next_value_ = 1;
    std::uint64_t frequency_ = 0;
};

D3D12_RESOURCE_BARRIER transition(
    ID3D12Resource* resource,
    D3D12_RESOURCE_STATES before,
    D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER result{};
    result.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    result.Transition.pResource = resource;
    result.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    result.Transition.StateBefore = before;
    result.Transition.StateAfter = after;
    return result;
}

D3D12_RESOURCE_BARRIER uav_barrier()
{
    D3D12_RESOURCE_BARRIER result{};
    result.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    result.UAV.pResource = nullptr;
    return result;
}

std::vector<std::byte> load_shader(const configuration& config, const wchar_t* file)
{
    return read_binary_file(config.shader_directory / file);
}

class d3d12_context
{
public:
    explicit d3d12_context(const configuration& config)
    {
        ComPtr<ID3D12SDKConfiguration> sdk;
        check(D3D12GetInterface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&sdk)),
            "D3D12GetInterface(ID3D12SDKConfiguration)");
        check(sdk->SetSDKVersion(sdk_version, ".\\D3D12\\"), "ID3D12SDKConfiguration::SetSDKVersion");
        check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory_)), "CreateDXGIFactory2");
        select_adapter(config);
        check(D3D12CreateDevice(adapter_.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device_)),
            "D3D12CreateDevice");
        D3D12_FEATURE_DATA_D3D12_OPTIONS12 options12{};
        check(device_->CheckFeatureSupport(
                  D3D12_FEATURE_D3D12_OPTIONS12,
                  &options12,
                  sizeof(options12)),
            "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS12)");
        enhanced_barriers_ = options12.EnhancedBarriersSupported != FALSE;
        graphics_ = std::make_unique<native_queue>(device_.Get(), D3D12_COMMAND_LIST_TYPE_DIRECT);
        compute_ = std::make_unique<native_queue>(device_.Get(), D3D12_COMMAND_LIST_TYPE_COMPUTE);
        copy_ = std::make_unique<native_queue>(device_.Get(), D3D12_COMMAND_LIST_TYPE_COPY);
        create_root_signatures();
        create_pipelines(
            load_shader(config, L"vertex.dxil"),
            load_shader(config, L"pixel.dxil"),
            load_shader(config, L"compute.dxil"));
    }

    ID3D12Device* device() const noexcept { return device_.Get(); }
    IDXGIFactory6* factory() const noexcept { return factory_.Get(); }
    IDXGIAdapter4* adapter() const noexcept { return adapter_.Get(); }
    native_queue& graphics() noexcept { return *graphics_; }
    native_queue& compute() noexcept { return *compute_; }
    native_queue& copy() noexcept { return *copy_; }
    ID3D12RootSignature* graphics_root() const noexcept { return graphics_root_.Get(); }
    ID3D12RootSignature* compute_root() const noexcept { return compute_root_.Get(); }
    ID3D12PipelineState* graphics_pipeline() const noexcept { return graphics_pipeline_.Get(); }
    ID3D12PipelineState* compute_pipeline() const noexcept { return compute_pipeline_.Get(); }
    bool enhanced_barriers() const noexcept { return enhanced_barriers_; }
    const DXGI_ADAPTER_DESC3& adapter_description() const noexcept { return adapter_description_; }

    ComPtr<ID3D12Resource> create_buffer(
        std::uint64_t size,
        D3D12_HEAP_TYPE heap_type,
        D3D12_RESOURCE_STATES initial_state) const
    {
        D3D12_HEAP_PROPERTIES properties{};
        properties.Type = heap_type;
        properties.CreationNodeMask = 1;
        properties.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC description{};
        description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        description.Width = size;
        description.Height = 1;
        description.DepthOrArraySize = 1;
        description.MipLevels = 1;
        description.SampleDesc.Count = 1;
        description.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ComPtr<ID3D12Resource> result;
        check(device_->CreateCommittedResource(
            &properties,
            D3D12_HEAP_FLAG_NONE,
            &description,
            initial_state,
            nullptr,
            IID_PPV_ARGS(&result)),
            "ID3D12Device::CreateCommittedResource(buffer)");
        return result;
    }

    ComPtr<ID3D12Resource> create_target_texture() const
    {
        D3D12_HEAP_PROPERTIES properties{};
        properties.Type = D3D12_HEAP_TYPE_DEFAULT;
        properties.CreationNodeMask = 1;
        properties.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC description{};
        description.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        description.Width = render_width;
        description.Height = render_height;
        description.DepthOrArraySize = 1;
        description.MipLevels = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        description.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        ComPtr<ID3D12Resource> result;
        check(device_->CreateCommittedResource(
            &properties,
            D3D12_HEAP_FLAG_NONE,
            &description,
            D3D12_RESOURCE_STATE_COMMON,
            nullptr,
            IID_PPV_ARGS(&result)),
            "ID3D12Device::CreateCommittedResource(texture)");
        return result;
    }

    ComPtr<ID3D12DescriptorHeap> create_rtv_heap() const
    {
        D3D12_DESCRIPTOR_HEAP_DESC description{};
        description.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        description.NumDescriptors = 1;
        description.NodeMask = 1;
        ComPtr<ID3D12DescriptorHeap> result;
        check(device_->CreateDescriptorHeap(&description, IID_PPV_ARGS(&result)),
            "ID3D12Device::CreateDescriptorHeap");
        return result;
    }

    D3D12_CPU_DESCRIPTOR_HANDLE create_rtv(
        ID3D12Resource* texture,
        ID3D12DescriptorHeap* heap) const
    {
        const auto handle = heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_RENDER_TARGET_VIEW_DESC description{};
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
        device_->CreateRenderTargetView(texture, &description, handle);
        return handle;
    }

    ComPtr<ID3D12QueryHeap> create_timestamp_heap(D3D12_COMMAND_LIST_TYPE type, UINT count) const
    {
        D3D12_QUERY_HEAP_DESC description{};
        description.Type = type == D3D12_COMMAND_LIST_TYPE_COPY
            ? D3D12_QUERY_HEAP_TYPE_COPY_QUEUE_TIMESTAMP
            : D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        description.Count = count;
        description.NodeMask = 1;
        ComPtr<ID3D12QueryHeap> result;
        check(device_->CreateQueryHeap(&description, IID_PPV_ARGS(&result)),
            "ID3D12Device::CreateQueryHeap");
        return result;
    }

private:
    void select_adapter(const configuration& config)
    {
        const auto matches = [&](const DXGI_ADAPTER_DESC3& value)
        {
            const auto high = static_cast<std::uint64_t>(static_cast<std::int64_t>(value.AdapterLuid.HighPart));
            return value.AdapterLuid.LowPart == config.adapter_low && high == config.adapter_high;
        };
        for (UINT index = 0;; ++index)
        {
            ComPtr<IDXGIAdapter4> candidate;
            const HRESULT result = factory_->EnumAdapterByGpuPreference(
                index,
                DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                IID_PPV_ARGS(&candidate));
            if (result == DXGI_ERROR_NOT_FOUND)
                break;
            check(result, "IDXGIFactory6::EnumAdapterByGpuPreference");
            DXGI_ADAPTER_DESC3 description{};
            check(candidate->GetDesc3(&description), "IDXGIAdapter4::GetDesc3");
            if (matches(description) &&
                SUCCEEDED(D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_12_0, __uuidof(ID3D12Device), nullptr)))
            {
                adapter_ = candidate;
                adapter_description_ = description;
                return;
            }
        }
        ComPtr<IDXGIAdapter4> warp;
        check(factory_->EnumWarpAdapter(IID_PPV_ARGS(&warp)), "IDXGIFactory4::EnumWarpAdapter");
        DXGI_ADAPTER_DESC3 description{};
        check(warp->GetDesc3(&description), "IDXGIAdapter4::GetDesc3(WARP)");
        if (matches(description) &&
            SUCCEEDED(D3D12CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_12_0, __uuidof(ID3D12Device), nullptr)))
        {
            adapter_ = warp;
            adapter_description_ = description;
            return;
        }
        throw std::runtime_error("The selected native D3D12 adapter is unavailable.");
    }

    ComPtr<ID3D12RootSignature> create_root_signature(
        const D3D12_ROOT_PARAMETER1* parameters,
        UINT count,
        D3D12_ROOT_SIGNATURE_FLAGS flags) const
    {
        D3D12_VERSIONED_ROOT_SIGNATURE_DESC versioned{};
        versioned.Version = D3D_ROOT_SIGNATURE_VERSION_1_1;
        versioned.Desc_1_1.NumParameters = count;
        versioned.Desc_1_1.pParameters = parameters;
        versioned.Desc_1_1.Flags = flags;
        ComPtr<ID3DBlob> serialized;
        ComPtr<ID3DBlob> errors;
        const HRESULT serialize = D3D12SerializeVersionedRootSignature(&versioned, &serialized, &errors);
        if (FAILED(serialize))
        {
            const std::string detail = errors
                ? std::string(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize())
                : std::string{};
            throw std::runtime_error("D3D12SerializeVersionedRootSignature failed: " + detail);
        }
        ComPtr<ID3D12RootSignature> result;
        check(device_->CreateRootSignature(
            1,
            serialized->GetBufferPointer(),
            serialized->GetBufferSize(),
            IID_PPV_ARGS(&result)),
            "ID3D12Device::CreateRootSignature");
        return result;
    }

    void create_root_signatures()
    {
        D3D12_ROOT_PARAMETER1 parameter{};
        parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        parameter.Descriptor.ShaderRegister = 0;
        parameter.Descriptor.RegisterSpace = 0;
        parameter.Descriptor.Flags = D3D12_ROOT_DESCRIPTOR_FLAG_DATA_STATIC;
        parameter.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        graphics_root_ = create_root_signature(
            &parameter,
            1,
            D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_AMPLIFICATION_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_MESH_SHADER_ROOT_ACCESS);
        compute_root_ = create_root_signature(
            nullptr,
            0,
            D3D12_ROOT_SIGNATURE_FLAG_DENY_VERTEX_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_PIXEL_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_AMPLIFICATION_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_MESH_SHADER_ROOT_ACCESS);
    }

    void create_pipelines(
        const std::vector<std::byte>& vertex,
        const std::vector<std::byte>& pixel,
        const std::vector<std::byte>& compute)
    {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC graphics{};
        graphics.pRootSignature = graphics_root_.Get();
        graphics.VS = {vertex.data(), vertex.size()};
        graphics.PS = {pixel.data(), pixel.size()};
        graphics.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND_ONE;
        graphics.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_ZERO;
        graphics.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
        graphics.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
        graphics.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_ZERO;
        graphics.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
        graphics.BlendState.RenderTarget[0].LogicOp = D3D12_LOGIC_OP_COPY;
        graphics.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        graphics.SampleMask = UINT_MAX;
        graphics.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        graphics.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        graphics.RasterizerState.FrontCounterClockwise = TRUE;
        graphics.RasterizerState.DepthClipEnable = TRUE;
        graphics.DepthStencilState.DepthEnable = FALSE;
        graphics.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
        graphics.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
        graphics.DepthStencilState.StencilReadMask = D3D12_DEFAULT_STENCIL_READ_MASK;
        graphics.DepthStencilState.StencilWriteMask = D3D12_DEFAULT_STENCIL_WRITE_MASK;
        graphics.DepthStencilState.FrontFace = {
            D3D12_STENCIL_OP_KEEP,
            D3D12_STENCIL_OP_KEEP,
            D3D12_STENCIL_OP_KEEP,
            D3D12_COMPARISON_FUNC_NEVER};
        graphics.DepthStencilState.BackFace = graphics.DepthStencilState.FrontFace;
        graphics.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        graphics.NumRenderTargets = 1;
        graphics.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
        graphics.SampleDesc.Count = 1;
        graphics.NodeMask = 1;
        check(device_->CreateGraphicsPipelineState(&graphics, IID_PPV_ARGS(&graphics_pipeline_)),
            "ID3D12Device::CreateGraphicsPipelineState");

        D3D12_COMPUTE_PIPELINE_STATE_DESC compute_description{};
        compute_description.pRootSignature = compute_root_.Get();
        compute_description.CS = {compute.data(), compute.size()};
        compute_description.NodeMask = 1;
        check(device_->CreateComputePipelineState(&compute_description, IID_PPV_ARGS(&compute_pipeline_)),
            "ID3D12Device::CreateComputePipelineState");
    }

    ComPtr<IDXGIFactory6> factory_;
    ComPtr<IDXGIAdapter4> adapter_;
    DXGI_ADAPTER_DESC3 adapter_description_{};
    ComPtr<ID3D12Device> device_;
    std::unique_ptr<native_queue> graphics_;
    std::unique_ptr<native_queue> compute_;
    std::unique_ptr<native_queue> copy_;
    ComPtr<ID3D12RootSignature> graphics_root_;
    ComPtr<ID3D12RootSignature> compute_root_;
    ComPtr<ID3D12PipelineState> graphics_pipeline_;
    ComPtr<ID3D12PipelineState> compute_pipeline_;
    bool enhanced_barriers_ = false;
};

void* map_write(ID3D12Resource* resource)
{
    D3D12_RANGE read{};
    void* result = nullptr;
    check(resource->Map(0, &read, &result), "ID3D12Resource::Map(write)");
    return result;
}

const std::byte* map_read(ID3D12Resource* resource, std::size_t size)
{
    D3D12_RANGE read{0, size};
    void* result = nullptr;
    check(resource->Map(0, &read, &result), "ID3D12Resource::Map(read)");
    return static_cast<const std::byte*>(result);
}

std::pair<std::uint64_t, std::uint64_t> read_timestamp_pair(ID3D12Resource* buffer)
{
    const auto* data = map_read(buffer, 16);
    std::pair<std::uint64_t, std::uint64_t> result{};
    std::memcpy(&result.first, data, sizeof(result.first));
    std::memcpy(&result.second, data + 8, sizeof(result.second));
    const D3D12_RANGE written{};
    buffer->Unmap(0, &written);
    return result;
}

std::uint64_t read_timestamp(ID3D12Resource* buffer)
{
    const auto* data = map_read(buffer, 8);
    std::uint64_t result = 0;
    std::memcpy(&result, data, sizeof(result));
    const D3D12_RANGE written{};
    buffer->Unmap(0, &written);
    return result;
}

calibration to_calibration(queue_kind queue, int frame, const calibrated_timestamp& value)
{
    return {queue, frame, value.cpu_counter, value.cpu_frequency, value.queue_counter, value.queue_frequency};
}

double map_queue_tick(std::uint64_t tick, const calibrated_timestamp& value)
{
    return static_cast<double>(value.cpu_counter) * (1'000'000.0 / value.cpu_frequency) +
        (static_cast<double>(tick) - static_cast<double>(value.queue_counter)) *
            (1'000'000.0 / static_cast<double>(value.queue_frequency));
}

void write_tint(void* destination, float red, float green, float blue, float alpha)
{
    const std::array values{red, green, blue, alpha};
    std::memcpy(destination, values.data(), sizeof(values));
}

void write_packet(void* destination, int draw_index)
{
    const float red = static_cast<float>((draw_index * 17) & 255) / 255.0F;
    const float green = static_cast<float>((draw_index * 29 + 31) & 255) / 255.0F;
    const float blue = static_cast<float>((draw_index * 43 + 7) & 255) / 255.0F;
    write_tint(destination, red, green, blue, 1.0F);
}

void set_graphics_state(
    ID3D12GraphicsCommandList* list,
    d3d12_context& context,
    D3D12_CPU_DESCRIPTOR_HANDLE rtv,
    D3D12_GPU_VIRTUAL_ADDRESS constant_buffer)
{
    const D3D12_VIEWPORT viewport{0, 0, static_cast<float>(render_width), static_cast<float>(render_height), 0, 1};
    const D3D12_RECT scissor{0, 0, render_width, render_height};
    const float clear[]{0.0625F, 0.125F, 0.25F, 1.0F};
    list->SetPipelineState(context.graphics_pipeline());
    list->SetGraphicsRootSignature(context.graphics_root());
    list->SetGraphicsRootConstantBufferView(0, constant_buffer);
    list->RSSetViewports(1, &viewport);
    list->RSSetScissorRects(1, &scissor);
    list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
    list->ClearRenderTargetView(rtv, clear, 0, nullptr);
}

class graphics_state_shadow
{
public:
    explicit graphics_state_shadow(ID3D12GraphicsCommandList* list) noexcept
        : list_(list)
    {
    }

    void set_pipeline(ID3D12PipelineState* pipeline, ID3D12RootSignature* root) noexcept
    {
        if (pipeline_ == pipeline && root_ == root)
            return;
        list_->SetPipelineState(pipeline);
        list_->SetGraphicsRootSignature(root);
        pipeline_ = pipeline;
        root_ = root;
    }

    void set_persistent_binding(D3D12_GPU_VIRTUAL_ADDRESS address) noexcept
    {
        if (persistent_address_ == address)
            return;
        list_->SetGraphicsRootConstantBufferView(0, address);
        persistent_address_ = address;
    }

    void set_viewport(const D3D12_VIEWPORT& viewport) noexcept
    {
        if (has_viewport_ &&
            normalized_float_equal(viewport_.TopLeftX, viewport.TopLeftX) &&
            normalized_float_equal(viewport_.TopLeftY, viewport.TopLeftY) &&
            normalized_float_equal(viewport_.Width, viewport.Width) &&
            normalized_float_equal(viewport_.Height, viewport.Height) &&
            normalized_float_equal(viewport_.MinDepth, viewport.MinDepth) &&
            normalized_float_equal(viewport_.MaxDepth, viewport.MaxDepth))
        {
            return;
        }
        list_->RSSetViewports(1, &viewport);
        viewport_ = viewport;
        has_viewport_ = true;
    }

    void set_scissor(const D3D12_RECT& scissor) noexcept
    {
        if (has_scissor_ &&
            scissor_.left == scissor.left &&
            scissor_.top == scissor.top &&
            scissor_.right == scissor.right &&
            scissor_.bottom == scissor.bottom)
        {
            return;
        }
        list_->RSSetScissorRects(1, &scissor);
        scissor_ = scissor;
        has_scissor_ = true;
    }

private:
    ID3D12GraphicsCommandList* list_{};
    ID3D12PipelineState* volatile pipeline_{};
    ID3D12RootSignature* volatile root_{};
    volatile D3D12_GPU_VIRTUAL_ADDRESS persistent_address_{};
    D3D12_VIEWPORT viewport_{};
    D3D12_RECT scissor_{};
    volatile bool has_viewport_{};
    volatile bool has_scissor_{};
};

std::string read_texture_hash(d3d12_context& context, ID3D12Resource* texture)
{
    auto readback = context.create_buffer(texture_byte_size, D3D12_HEAP_TYPE_READBACK,
        D3D12_RESOURCE_STATE_COPY_DEST);
    auto* list = context.graphics().begin();
    D3D12_TEXTURE_COPY_LOCATION destination{};
    destination.pResource = readback.Get();
    destination.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    destination.PlacedFootprint.Offset = 0;
    destination.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    destination.PlacedFootprint.Footprint.Width = render_width;
    destination.PlacedFootprint.Footprint.Height = render_height;
    destination.PlacedFootprint.Footprint.Depth = 1;
    destination.PlacedFootprint.Footprint.RowPitch = 256;
    D3D12_TEXTURE_COPY_LOCATION source{};
    source.pResource = texture;
    source.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    source.SubresourceIndex = 0;
    list->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
    const auto completion = context.graphics().execute();
    context.graphics().wait_cpu(completion);
    const auto* data = map_read(readback.Get(), texture_byte_size);
    const std::string result = sha256_bytes(data, texture_byte_size);
    const D3D12_RANGE written{};
    readback->Unmap(0, &written);
    return result;
}

struct frame_measurement
{
    frame_sample sample;
    calibrated_timestamp calibration;
};

workload_run run_empty_submit(
    d3d12_context& context,
    const configuration& config,
    const std::string& shader_manifest)
{
    for (int frame = 0; frame < config.warmup_frames; ++frame)
    {
        const auto completion = context.copy().signal_only();
        context.copy().wait_cpu(completion);
    }
    std::vector<frame_sample> samples;
    samples.reserve(config.measured_frames);
    for (int frame = 0; frame < config.measured_frames; ++frame)
    {
        const auto started = qpc();
        const auto completion = context.copy().signal_only();
        const auto stopped = qpc();
        context.copy().wait_cpu(completion);
        samples.push_back({frame, stopped - started, ticks_to_microseconds(stopped - started), std::nullopt, completion});
    }
    return complete_workload(
        config,
        workload_kind::empty_submit,
        0,
        0,
        std::move(samples),
        {},
        fixed_output_hash(workload_kind::empty_submit, shader_manifest),
        shader_manifest,
        {},
        {});
}

frame_measurement execute_draw_frame(
    d3d12_context& context,
    ID3D12Resource* target,
    D3D12_CPU_DESCRIPTOR_HANDLE rtv,
    ID3D12Resource* persistent,
    ID3D12Resource* transient,
    std::byte* transient_data,
    ID3D12QueryHeap* query_heap,
    ID3D12Resource* query_readback,
    workload_kind kind,
    int draw_count,
    bool& initialized,
    int frame_index)
{
    const auto calibration = context.graphics().calibrate();
    const auto started = qpc();
    if (kind == workload_kind::transient_draw)
    {
        for (int draw = 0; draw < draw_count; ++draw)
            write_packet(transient_data + static_cast<std::size_t>(draw) * constant_buffer_alignment, draw);
    }
    auto* list = context.graphics().begin();
    list->EndQuery(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 0);
    auto first = transition(target,
        initialized ? D3D12_RESOURCE_STATE_COPY_SOURCE : D3D12_RESOURCE_STATE_COMMON,
        D3D12_RESOURCE_STATE_RENDER_TARGET);
    list->ResourceBarrier(1, &first);
    const auto constant_buffer = kind == workload_kind::transient_draw
        ? transient->GetGPUVirtualAddress()
        : persistent->GetGPUVirtualAddress();
    const bool suppress_state = kind == workload_kind::state_suppression;
    graphics_state_shadow state_shadow(list);
    if (suppress_state)
    {
        const D3D12_VIEWPORT viewport{
            0, 0, static_cast<float>(render_width), static_cast<float>(render_height), 0, 1};
        const D3D12_RECT scissor{0, 0, render_width, render_height};
        const float clear[]{0.0625F, 0.125F, 0.25F, 1.0F};
        state_shadow.set_pipeline(context.graphics_pipeline(), context.graphics_root());
        state_shadow.set_persistent_binding(constant_buffer);
        state_shadow.set_viewport(viewport);
        state_shadow.set_scissor(scissor);
        list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
        list->ClearRenderTargetView(rtv, clear, 0, nullptr);
    }
    else
    {
        set_graphics_state(list, context, rtv, constant_buffer);
    }
    for (int draw = 0; draw < draw_count; ++draw)
    {
        if (kind == workload_kind::transient_draw)
        {
            list->SetGraphicsRootConstantBufferView(
                0,
                transient->GetGPUVirtualAddress() + static_cast<UINT64>(draw) * constant_buffer_alignment);
        }
        else if (suppress_state)
        {
            const D3D12_VIEWPORT viewport{
                0, 0, static_cast<float>(render_width), static_cast<float>(render_height), 0, 1};
            const D3D12_RECT scissor{0, 0, render_width, render_height};
            state_shadow.set_pipeline(context.graphics_pipeline(), context.graphics_root());
            state_shadow.set_persistent_binding(constant_buffer);
            state_shadow.set_viewport(viewport);
            state_shadow.set_scissor(scissor);
        }
        list->DrawInstanced(3, 1, 0, 0);
    }
    auto second = transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
    list->ResourceBarrier(1, &second);
    list->EndQuery(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 1);
    list->ResolveQueryData(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 0, 2, query_readback, 0);
    const auto completion = context.graphics().execute();
    const auto stopped = qpc();
    context.graphics().wait_cpu(completion);
    const auto [gpu_start, gpu_end] = read_timestamp_pair(query_readback);
    initialized = true;
    return {
        {frame_index,
            stopped - started,
            ticks_to_microseconds(stopped - started),
            static_cast<double>(gpu_end - gpu_start) * (1'000'000.0 / calibration.queue_frequency),
            completion},
        calibration};
}

workload_run run_draw(
    d3d12_context& context,
    const configuration& config,
    const std::string& shader_manifest,
    workload_kind kind)
{
    auto target = context.create_target_texture();
    auto rtv_heap = context.create_rtv_heap();
    const auto rtv = context.create_rtv(target.Get(), rtv_heap.Get());
    auto persistent = context.create_buffer(constant_buffer_alignment, D3D12_HEAP_TYPE_UPLOAD,
        D3D12_RESOURCE_STATE_GENERIC_READ);
    auto* persistent_data = map_write(persistent.Get());
    write_tint(persistent_data, 1, 1, 1, 1);
    ComPtr<ID3D12Resource> transient;
    std::byte* transient_data = nullptr;
    if (kind == workload_kind::transient_draw)
    {
        transient = context.create_buffer(static_cast<UINT64>(config.draw_count) * constant_buffer_alignment,
            D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
        transient_data = static_cast<std::byte*>(map_write(transient.Get()));
    }
    auto query_heap = context.create_timestamp_heap(D3D12_COMMAND_LIST_TYPE_DIRECT, 2);
    auto query_readback = context.create_buffer(16, D3D12_HEAP_TYPE_READBACK,
        D3D12_RESOURCE_STATE_COPY_DEST);
    bool initialized = false;
    for (int frame = 0; frame < config.warmup_frames; ++frame)
    {
        (void)execute_draw_frame(context, target.Get(), rtv, persistent.Get(), transient.Get(),
            transient_data, query_heap.Get(), query_readback.Get(), kind, config.draw_count, initialized, frame);
    }
    std::vector<frame_sample> samples;
    std::vector<calibration> calibrations;
    samples.reserve(config.measured_frames);
    calibrations.reserve(config.measured_frames);
    for (int frame = 0; frame < config.measured_frames; ++frame)
    {
        auto measurement = execute_draw_frame(context, target.Get(), rtv, persistent.Get(), transient.Get(),
            transient_data, query_heap.Get(), query_readback.Get(), kind, config.draw_count, initialized, frame);
        samples.push_back(measurement.sample);
        calibrations.push_back(to_calibration(queue_kind::graphics, frame, measurement.calibration));
    }
    const std::string output_hash = read_texture_hash(context, target.Get());
    D3D12_RANGE written{};
    persistent->Unmap(0, &written);
    if (transient)
        transient->Unmap(0, &written);
    std::vector<barrier_evidence> barriers{
        {0, "TextureBarrier", 0, 1, std::nullopt},
        {1, "TextureBarrier", 1, 1, std::nullopt}};
    const setter_evidence setters{
        1,
        kind == workload_kind::transient_draw ? 0 : 1,
        1,
        1,
        config.draw_count};
    return complete_workload(config, kind, config.draw_count, static_cast<int>(barriers.size()),
        std::move(samples), std::move(calibrations), output_hash, shader_manifest,
        std::move(barriers), setters);
}

frame_measurement execute_barrier_frame(
    d3d12_context& context,
    ID3D12QueryHeap* query_heap,
    ID3D12Resource* query_readback,
    int barrier_count,
    int frame_index)
{
    const auto calibration = context.compute().calibrate();
    const auto started = qpc();
    auto* list = context.compute().begin();
    list->EndQuery(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 0);
    if (context.enhanced_barriers())
    {
        const D3D12_GLOBAL_BARRIER barrier{
            D3D12_BARRIER_SYNC_COMPUTE_SHADING,
            D3D12_BARRIER_SYNC_COMPUTE_SHADING,
            D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
            D3D12_BARRIER_ACCESS_UNORDERED_ACCESS};
        const D3D12_BARRIER_GROUP group{
            D3D12_BARRIER_TYPE_GLOBAL,
            1,
            {.pGlobalBarriers = &barrier}};
        auto* enhanced_list = context.compute().enhanced_list();
        for (int index = 0; index < barrier_count; ++index)
            enhanced_list->Barrier(1, &group);
    }
    else
    {
        const auto barrier = uav_barrier();
        for (int index = 0; index < barrier_count; ++index)
            list->ResourceBarrier(1, &barrier);
    }
    list->SetPipelineState(context.compute_pipeline());
    list->SetComputeRootSignature(context.compute_root());
    list->Dispatch(1, 1, 1);
    list->EndQuery(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 1);
    list->ResolveQueryData(query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 0, 2, query_readback, 0);
    const auto completion = context.compute().execute();
    const auto stopped = qpc();
    context.compute().wait_cpu(completion);
    const auto [gpu_start, gpu_end] = read_timestamp_pair(query_readback);
    return {
        {frame_index,
            stopped - started,
            ticks_to_microseconds(stopped - started),
            static_cast<double>(gpu_end - gpu_start) * (1'000'000.0 / calibration.queue_frequency),
            completion},
        calibration};
}

workload_run run_explicit_barriers(
    d3d12_context& context,
    const configuration& config,
    const std::string& shader_manifest)
{
    auto query_heap = context.create_timestamp_heap(D3D12_COMMAND_LIST_TYPE_COMPUTE, 2);
    auto query_readback = context.create_buffer(16, D3D12_HEAP_TYPE_READBACK,
        D3D12_RESOURCE_STATE_COPY_DEST);
    for (int frame = 0; frame < config.warmup_frames; ++frame)
        (void)execute_barrier_frame(context, query_heap.Get(), query_readback.Get(), config.barrier_count, frame);
    std::vector<frame_sample> samples;
    std::vector<calibration> calibrations;
    samples.reserve(config.measured_frames);
    calibrations.reserve(config.measured_frames);
    for (int frame = 0; frame < config.measured_frames; ++frame)
    {
        auto measurement = execute_barrier_frame(
            context, query_heap.Get(), query_readback.Get(), config.barrier_count, frame);
        samples.push_back(measurement.sample);
        calibrations.push_back(to_calibration(queue_kind::compute, frame, measurement.calibration));
    }
    std::vector<barrier_evidence> barriers;
    barriers.reserve(config.barrier_count);
    for (int index = 0; index < config.barrier_count; ++index)
        barriers.push_back({index, "MemoryBarrier", index, 1, std::nullopt});
    return complete_workload(
        config,
        workload_kind::explicit_barrier,
        0,
        config.barrier_count,
        std::move(samples),
        std::move(calibrations),
        fixed_output_hash(workload_kind::explicit_barrier, shader_manifest),
        shader_manifest,
        std::move(barriers),
        {1, 0, 0, 0, 0});
}

class benchmark_window
{
public:
    benchmark_window()
    {
        handle_ = CreateWindowExW(
            0,
            L"STATIC",
            L"SomeEngine Graphics Native Benchmark",
            WS_OVERLAPPEDWINDOW,
            0,
            0,
            render_width,
            render_height,
            nullptr,
            nullptr,
            nullptr,
            nullptr);
        if (handle_ == nullptr)
            throw std::runtime_error("CreateWindowExW failed.");
    }

    ~benchmark_window()
    {
        if (handle_ != nullptr)
            DestroyWindow(handle_);
    }

    benchmark_window(const benchmark_window&) = delete;
    benchmark_window& operator=(const benchmark_window&) = delete;
    HWND handle() const noexcept { return handle_; }

private:
    HWND handle_ = nullptr;
};

class native_swapchain
{
public:
    native_swapchain(d3d12_context& context, HWND window)
    {
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = render_width;
        description.Height = render_height;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_BACK_BUFFER;
        description.BufferCount = 2;
        description.Scaling = DXGI_SCALING_STRETCH;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> initial;
        check(context.factory()->CreateSwapChainForHwnd(
            context.graphics().queue(),
            window,
            &description,
            nullptr,
            nullptr,
            &initial),
            "IDXGIFactory6::CreateSwapChainForHwnd");
        check(initial.As(&swapchain_), "IDXGISwapChain1::QueryInterface(IDXGISwapChain4)");
        check(swapchain_->GetBuffer(0, IID_PPV_ARGS(&buffers_[0])), "IDXGISwapChain::GetBuffer(0)");
        check(swapchain_->GetBuffer(1, IID_PPV_ARGS(&buffers_[1])), "IDXGISwapChain::GetBuffer(1)");
    }

    ID3D12Resource* current_buffer() const
    {
        const UINT index = swapchain_->GetCurrentBackBufferIndex();
        if (index >= buffers_.size())
            throw std::runtime_error("The two-buffer swapchain returned an invalid index.");
        return buffers_[index].Get();
    }

    void present()
    {
        check(swapchain_->Present(0, 0), "IDXGISwapChain::Present");
    }

private:
    ComPtr<IDXGISwapChain4> swapchain_;
    std::array<ComPtr<ID3D12Resource>, 2> buffers_;
};

struct three_queue_measurement
{
    frame_sample sample;
    calibrated_timestamp copy_calibration;
    calibrated_timestamp graphics_calibration;
};

three_queue_measurement execute_three_queue_frame(
    d3d12_context& context,
    native_swapchain& swapchain,
    ID3D12Resource* target,
    D3D12_CPU_DESCRIPTOR_HANDLE rtv,
    ID3D12Resource* persistent,
    ID3D12Resource* upload,
    ID3D12Resource* work,
    ID3D12Resource* sink,
    ID3D12QueryHeap* copy_query,
    ID3D12QueryHeap* graphics_query,
    ID3D12Resource* copy_timestamp,
    ID3D12Resource* graphics_timestamp,
    bool& initialized,
    int frame_index)
{
    auto* back_buffer = swapchain.current_buffer();
    const auto copy_calibration = context.copy().calibrate();
    const auto graphics_calibration = context.graphics().calibrate();
    const auto started = qpc();

    auto* copy_list = context.copy().begin();
    copy_list->EndQuery(copy_query, D3D12_QUERY_TYPE_TIMESTAMP, 0);
    auto copy_acquire = transition(work, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
    copy_list->ResourceBarrier(1, &copy_acquire);
    copy_list->CopyBufferRegion(work, 0, upload, 0, 256);
    auto copy_release = transition(work, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
    copy_list->ResourceBarrier(1, &copy_release);
    copy_list->ResolveQueryData(copy_query, D3D12_QUERY_TYPE_TIMESTAMP, 0, 1, copy_timestamp, 0);
    const auto copy_completion = context.copy().execute();

    context.compute().wait_gpu(context.copy(), copy_completion);
    auto* compute_list = context.compute().begin();
    auto compute_acquire = transition(
        work, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    compute_list->ResourceBarrier(1, &compute_acquire);
    compute_list->SetPipelineState(context.compute_pipeline());
    compute_list->SetComputeRootSignature(context.compute_root());
    compute_list->Dispatch(1, 1, 1);
    auto compute_release = transition(
        work, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
    compute_list->ResourceBarrier(1, &compute_release);
    const auto compute_completion = context.compute().execute();

    context.graphics().wait_gpu(context.compute(), compute_completion);
    auto* graphics_list = context.graphics().begin();
    auto graphics_acquire = transition(work, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
    graphics_list->ResourceBarrier(1, &graphics_acquire);
    graphics_list->CopyBufferRegion(sink, 0, work, 0, 256);
    auto work_return = transition(work, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
    graphics_list->ResourceBarrier(1, &work_return);
    auto target_to_render = transition(target,
        initialized ? D3D12_RESOURCE_STATE_COPY_SOURCE : D3D12_RESOURCE_STATE_COMMON,
        D3D12_RESOURCE_STATE_RENDER_TARGET);
    graphics_list->ResourceBarrier(1, &target_to_render);
    set_graphics_state(graphics_list, context, rtv, persistent->GetGPUVirtualAddress());
    graphics_list->DrawInstanced(3, 1, 0, 0);
    auto target_to_copy = transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
    graphics_list->ResourceBarrier(1, &target_to_copy);
    auto swapchain_to_copy = transition(back_buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
    graphics_list->ResourceBarrier(1, &swapchain_to_copy);
    graphics_list->CopyResource(back_buffer, target);
    auto swapchain_to_present = transition(back_buffer, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
    graphics_list->ResourceBarrier(1, &swapchain_to_present);
    graphics_list->EndQuery(graphics_query, D3D12_QUERY_TYPE_TIMESTAMP, 0);
    graphics_list->ResolveQueryData(graphics_query, D3D12_QUERY_TYPE_TIMESTAMP, 0, 1, graphics_timestamp, 0);
    const auto graphics_completion = context.graphics().execute();
    swapchain.present();
    const auto stopped = qpc();
    context.graphics().wait_cpu(graphics_completion);
    const auto copy_tick = read_timestamp(copy_timestamp);
    const auto graphics_tick = read_timestamp(graphics_timestamp);
    const double gpu_time = std::max(0.0,
        map_queue_tick(graphics_tick, graphics_calibration) - map_queue_tick(copy_tick, copy_calibration));
    initialized = true;
    return {
        {frame_index, stopped - started, ticks_to_microseconds(stopped - started), gpu_time, graphics_completion},
        copy_calibration,
        graphics_calibration};
}

workload_run run_three_queue_present(
    d3d12_context& context,
    const configuration& config,
    const std::string& shader_manifest)
{
    benchmark_window window;
    native_swapchain swapchain(context, window.handle());
    auto target = context.create_target_texture();
    auto rtv_heap = context.create_rtv_heap();
    const auto rtv = context.create_rtv(target.Get(), rtv_heap.Get());
    auto persistent = context.create_buffer(constant_buffer_alignment, D3D12_HEAP_TYPE_UPLOAD,
        D3D12_RESOURCE_STATE_GENERIC_READ);
    auto* persistent_data = map_write(persistent.Get());
    write_tint(persistent_data, 1, 1, 1, 1);
    auto upload = context.create_buffer(256, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
    auto* upload_data = static_cast<std::byte*>(map_write(upload.Get()));
    for (int index = 0; index < 256; ++index)
        upload_data[index] = static_cast<std::byte>((0x5E + index * 29) & 0xFF);
    auto work = context.create_buffer(256, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COMMON);
    auto sink = context.create_buffer(256, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST);
    auto copy_query = context.create_timestamp_heap(D3D12_COMMAND_LIST_TYPE_COPY, 1);
    auto graphics_query = context.create_timestamp_heap(D3D12_COMMAND_LIST_TYPE_DIRECT, 1);
    auto copy_timestamp = context.create_buffer(8, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
    auto graphics_timestamp = context.create_buffer(8, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
    bool initialized = false;
    for (int frame = 0; frame < config.warmup_frames; ++frame)
    {
        (void)execute_three_queue_frame(context, swapchain, target.Get(), rtv, persistent.Get(), upload.Get(),
            work.Get(), sink.Get(), copy_query.Get(), graphics_query.Get(), copy_timestamp.Get(),
            graphics_timestamp.Get(), initialized, frame);
    }
    std::vector<frame_sample> samples;
    std::vector<calibration> calibrations;
    samples.reserve(config.measured_frames);
    calibrations.reserve(static_cast<std::size_t>(config.measured_frames) * 2);
    for (int frame = 0; frame < config.measured_frames; ++frame)
    {
        auto measurement = execute_three_queue_frame(context, swapchain, target.Get(), rtv, persistent.Get(),
            upload.Get(), work.Get(), sink.Get(), copy_query.Get(), graphics_query.Get(), copy_timestamp.Get(),
            graphics_timestamp.Get(), initialized, frame);
        samples.push_back(measurement.sample);
        calibrations.push_back(to_calibration(queue_kind::copy, frame, measurement.copy_calibration));
        calibrations.push_back(to_calibration(queue_kind::graphics, frame, measurement.graphics_calibration));
    }
    const std::string output_hash = read_texture_hash(context, target.Get());
    D3D12_RANGE written{};
    persistent->Unmap(0, &written);
    upload->Unmap(0, &written);
    std::vector<barrier_evidence> barriers{
        {0, "QueueAcquire", 0, 1, std::nullopt},
        {1, "QueueRelease", 1, 1, std::nullopt},
        {2, "QueueAcquire", 2, 1, std::nullopt},
        {3, "QueueRelease", 3, 1, std::nullopt},
        {4, "QueueAcquire", 4, 1, std::nullopt},
        {5, "QueueRelease", 5, 1, std::nullopt},
        {6, "TextureBarrier", 6, 1, std::nullopt},
        {7, "TextureBarrier", 7, 1, std::nullopt},
        {8, "TextureBarrier", 8, 1, std::nullopt},
        {9, "TextureBarrier", 9, 1, std::nullopt}};
    return complete_workload(
        config,
        workload_kind::three_queue_present,
        1,
        static_cast<int>(barriers.size()),
        std::move(samples),
        std::move(calibrations),
        output_hash,
        shader_manifest,
        std::move(barriers),
        {1, 1, 1, 1, 1});
}

std::string trim(std::string value)
{
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
        value.pop_back();
    std::size_t first = 0;
    while (first < value.size() && std::isspace(static_cast<unsigned char>(value[first])))
        ++first;
    return value.substr(first);
}

std::string run_capture(const wchar_t* command)
{
    std::unique_ptr<FILE, decltype(&_pclose)> pipe(_wpopen(command, L"rt"), &_pclose);
    if (!pipe)
        return "unavailable";
    std::string result;
    std::array<char, 512> buffer{};
    while (std::fgets(buffer.data(), static_cast<int>(buffer.size()), pipe.get()) != nullptr)
        result += buffer.data();
    return trim(result);
}

std::string active_power_mode()
{
    std::string output = run_capture(L"powercfg.exe /getactivescheme 2>NUL");
    for (std::size_t index = 0; index + 36 <= output.size(); ++index)
    {
        std::string_view candidate(output.data() + index, 36);
        if (candidate[8] != '-' || candidate[13] != '-' || candidate[18] != '-' || candidate[23] != '-')
            continue;
        if (!std::ranges::all_of(candidate, [position = std::size_t{0}](char value) mutable
            {
                const bool separator = position == 8 || position == 13 || position == 18 || position == 23;
                ++position;
                return separator ? value == '-' : std::isxdigit(static_cast<unsigned char>(value)) != 0;
            }))
        {
            continue;
        }

        std::string result(candidate);
        std::ranges::transform(result, result.begin(), [](char value)
            { return static_cast<char>(std::tolower(static_cast<unsigned char>(value))); });
        return result;
    }
    return "unavailable";
}

std::string operating_system_version()
{
    using rtl_get_version = LONG(WINAPI*)(RTL_OSVERSIONINFOW*);
    HMODULE module = GetModuleHandleW(L"ntdll.dll");
    auto get_version = module == nullptr
        ? nullptr
        : reinterpret_cast<rtl_get_version>(GetProcAddress(module, "RtlGetVersion"));
    if (get_version == nullptr)
        return "unavailable";

    RTL_OSVERSIONINFOW version{};
    version.dwOSVersionInfoSize = sizeof(version);
    if (get_version(&version) != 0)
        return "unavailable";

    std::ostringstream result;
    result << "Microsoft Windows " << version.dwMajorVersion << '.'
           << version.dwMinorVersion << '.' << version.dwBuildNumber;
    return result.str();
}

std::string processor_name()
{
    std::array<wchar_t, 256> value{};
    DWORD byte_count = static_cast<DWORD>(value.size() * sizeof(wchar_t));
    const LONG result = RegGetValueW(
        HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
        L"ProcessorNameString",
        RRF_RT_REG_SZ,
        nullptr,
        value.data(),
        &byte_count);
    if (result != ERROR_SUCCESS)
        return "unavailable";
    return trim(utf8(value.data()));
}

bool capture_tool_loaded()
{
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
        return true;
    MODULEENTRY32W module{};
    module.dwSize = sizeof(module);
    bool found = false;
    if (Module32FirstW(snapshot, &module))
    {
        do
        {
            std::wstring name = module.szModule;
            std::ranges::transform(name, name.begin(), [](wchar_t value) { return std::towlower(value); });
            if (name.find(L"winpixgpucapturer") != std::wstring::npos ||
                name.find(L"renderdoc") != std::wstring::npos ||
                name.find(L"nsight") != std::wstring::npos)
            {
                found = true;
                break;
            }
        } while (Module32NextW(snapshot, &module));
    }
    else
    {
        found = true;
    }
    CloseHandle(snapshot);
    return found;
}

std::filesystem::path executable_path()
{
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length == buffer.size())
        throw std::runtime_error("GetModuleFileNameW failed.");
    buffer.resize(length);
    return buffer;
}

std::string build_payload_sha256(const std::filesystem::path& executable)
{
    const std::array paths =
    {
        executable,
        executable.parent_path() / L"D3D12" / L"D3D12Core.dll",
        executable.parent_path() / L"D3D12" / L"d3d12SDKLayers.dll",
    };
    std::string manifest;
    for (const std::filesystem::path& path : paths)
    {
        if (!std::filesystem::is_regular_file(path))
            throw std::runtime_error("The native benchmark payload is incomplete.");
        manifest += utf8(path.filename().wstring());
        manifest.push_back('\n');
        manifest += sha256_file(path);
        manifest.push_back('\n');
    }
    return sha256_bytes(manifest.data(), manifest.size());
}

runtime_environment capture_environment(
    const configuration& config,
    d3d12_context& context,
    std::int64_t affinity)
{
    runtime_environment result;
    result.operating_system = operating_system_version();
    result.processor_name = processor_name();
    result.process_id = GetCurrentProcessId();
    result.process_index = config.process_index;
    result.affinity_mask = affinity;
    result.priority = "High";
    result.power_mode = active_power_mode();
    const auto& adapter = context.adapter_description();
    result.adapter_name = utf8(adapter.Description);
    result.vendor_id = adapter.VendorId;
    result.device_id = adapter.DeviceId;
    result.adapter_luid_low = adapter.AdapterLuid.LowPart;
    result.adapter_luid_high = static_cast<std::uint64_t>(static_cast<std::int64_t>(adapter.AdapterLuid.HighPart));
    LARGE_INTEGER driver{};
    if (SUCCEEDED(context.adapter()->CheckInterfaceSupport(__uuidof(IDXGIDevice), &driver)))
    {
        std::ostringstream version;
        version << HIWORD(driver.HighPart) << '.' << LOWORD(driver.HighPart) << '.'
                << HIWORD(driver.LowPart) << '.' << LOWORD(driver.LowPart);
        result.driver_version = version.str();
    }
    else
    {
        result.driver_version = "unavailable";
    }
    result.hardware_accelerated = (adapter.Flags & DXGI_ADAPTER_FLAG3_SOFTWARE) == 0;
    result.capture_tool_loaded = capture_tool_loaded();
    const auto executable = executable_path();
    result.build.executable_path = utf8(executable.wstring());
    result.build.executable_sha256 = sha256_file(executable);
    result.build.payload_sha256 = build_payload_sha256(executable);
#ifdef NDEBUG
    result.build.configuration = "Release";
#else
    result.build.configuration = "Debug";
#endif
    const std::string commit = run_capture(L"git.exe rev-parse HEAD 2>NUL");
    result.build.commit = commit.empty() ? "unknown" : commit;
    const std::string status = run_capture(L"git.exe status --porcelain 2>NUL");
    result.build.worktree_dirty = !status.empty() && status != "unavailable";
    result.build.toolchain = "MSVC " + std::to_string(_MSC_FULL_VER) + " / native D3D12";
    return result;
}

std::int64_t establish_scheduling(const configuration& config)
{
    DWORD_PTR process_mask = 0;
    DWORD_PTR system_mask = 0;
    if (!GetProcessAffinityMask(GetCurrentProcess(), &process_mask, &system_mask) || process_mask == 0)
        throw std::runtime_error("GetProcessAffinityMask failed.");
    const DWORD_PTR selected = std::bit_floor(process_mask);
    const bool affinity_set = SetProcessAffinityMask(GetCurrentProcess(), selected) != FALSE;
    const bool priority_set = SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS) != FALSE;
    if ((!affinity_set || !priority_set) && config.selected_profile == profile::certification)
        throw std::runtime_error("Certification CPU affinity/high-priority policy could not be established.");
    return static_cast<std::int64_t>(selected);
}
}

runtime_environment unavailable_environment(const configuration& config)
{
    runtime_environment result;
    result.operating_system = operating_system_version();
    result.processor_name = processor_name();
    result.process_id = GetCurrentProcessId();
    result.process_index = config.process_index;
    result.priority = "unavailable";
    result.power_mode = "unavailable";
    result.adapter_luid_low = config.adapter_low;
    result.adapter_luid_high = config.adapter_high;
    result.driver_version = "unavailable";
    result.build.configuration = "unknown";
    result.build.commit = "unknown";
    result.build.worktree_dirty = true;
    result.build.toolchain = "native D3D12 unavailable";
    return result;
}

process_run run_native_d3d12(const configuration& config)
{
    verify_state_equality_contract();
    verify_shader_artifacts(config);
    const std::int64_t affinity = establish_scheduling(config);
    d3d12_context context(config);
    runtime_environment environment = capture_environment(config, context, affinity);
    if (config.selected_profile == profile::certification && environment.capture_tool_loaded)
    {
        return {
            disposition::unexecuted,
            "A capture tool is loaded.",
            std::move(environment),
            {}};
    }
    const std::string shader_manifest = sha256_file(config.shader_directory / "manifest.json");
    std::vector<workload_run> workloads;
    workloads.reserve(6);
    workloads.push_back(run_empty_submit(context, config, shader_manifest));
    workloads.push_back(run_draw(context, config, shader_manifest, workload_kind::persistent_draw));
    workloads.push_back(run_draw(context, config, shader_manifest, workload_kind::transient_draw));
    workloads.push_back(run_draw(context, config, shader_manifest, workload_kind::state_suppression));
    workloads.push_back(run_explicit_barriers(context, config, shader_manifest));
    workloads.push_back(run_three_queue_present(context, config, shader_manifest));
    const auto result = config.selected_profile == profile::certification
        ? disposition::passed
        : disposition::functional_only;
    return {
        result,
        config.selected_profile == profile::certification
            ? "All fixed native C++ D3D12 workloads executed."
            : "All reduced-count native C++ D3D12 workloads executed on WARP; not performance evidence.",
        std::move(environment),
        std::move(workloads)};
}
}
