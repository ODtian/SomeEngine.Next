#include "benchmark_protocol.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <bit>
#include <charconv>
#include <cmath>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <stdexcept>

namespace someengine::graphics::benchmark
{
namespace
{
[[noreturn]] void usage(const std::string& message)
{
    throw std::invalid_argument(message +
        " Usage: native-runner --profile <warp|diagnose|certify|representative> --variant native-cpp "
        "--adapter <low>:<high> --process-index <n> --warmup <n> --samples <n> "
        "--draws <n> --barriers <n> --shader-dir <path> --output <path>");
}

std::uint64_t parse_u64(std::wstring value)
{
    int base = 10;
    if (value.starts_with(L"0x") || value.starts_with(L"0X"))
    {
        value.erase(0, 2);
        base = 16;
    }
    if (value.empty())
        usage("An unsigned integer is empty.");
    wchar_t* end = nullptr;
    const auto result = std::wcstoull(value.c_str(), &end, base);
    if (end == nullptr || *end != L'\0')
        usage("An unsigned integer is invalid.");
    return result;
}

int parse_positive(const std::wstring& value)
{
    const auto result = parse_u64(value);
    if (result == 0 || result > static_cast<std::uint64_t>(INT_MAX))
        usage("A count must be a positive Int32.");
    return static_cast<int>(result);
}

int parse_non_negative(const std::wstring& value)
{
    const auto result = parse_u64(value);
    if (result > static_cast<std::uint64_t>(INT_MAX))
        usage("A count must be a non-negative Int32.");
    return static_cast<int>(result);
}

const char* disposition_name(disposition value)
{
    switch (value)
    {
    case disposition::passed: return "passed";
    case disposition::failed: return "failed";
    case disposition::functional_only: return "functionalOnly";
    case disposition::unexecuted: return "unexecuted";
    }
    return "failed";
}

const char* workload_name(workload_kind value)
{
    switch (value)
    {
    case workload_kind::empty_submit: return "emptySubmit";
    case workload_kind::persistent_draw: return "persistentDraw10000";
    case workload_kind::transient_draw: return "transientDraw10000";
    case workload_kind::state_suppression: return "stateSuppression10000";
    case workload_kind::explicit_barrier: return "explicitBarrier4096";
    case workload_kind::three_queue_present: return "threeQueuePresent";
    case workload_kind::representative_frame_serial: return "representativeFrameSerial";
    case workload_kind::representative_frame_parallel: return "representativeFrameParallel";
    }
    return "emptySubmit";
}

const char* queue_name(queue_kind value)
{
    switch (value)
    {
    case queue_kind::graphics: return "graphics";
    case queue_kind::compute: return "compute";
    case queue_kind::copy: return "copy";
    }
    return "graphics";
}

void json_string(std::ostream& output, const std::string& value)
{
    output.put('"');
    for (const unsigned char character : value)
    {
        switch (character)
        {
        case '"': output << "\\\""; break;
        case '\\': output << "\\\\"; break;
        case '\b': output << "\\b"; break;
        case '\f': output << "\\f"; break;
        case '\n': output << "\\n"; break;
        case '\r': output << "\\r"; break;
        case '\t': output << "\\t"; break;
        default:
            if (character < 0x20)
            {
                output << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                       << static_cast<unsigned>(character) << std::dec << std::setfill(' ');
            }
            else
            {
                output.put(static_cast<char>(character));
            }
            break;
        }
    }
    output.put('"');
}

double percentile_r7(const std::vector<double>& sorted, double percentile)
{
    const double position = static_cast<double>(sorted.size() - 1) * percentile;
    const auto lower = static_cast<std::size_t>(std::floor(position));
    const auto upper = static_cast<std::size_t>(std::ceil(position));
    if (lower == upper)
        return sorted[lower];
    const double fraction = position - static_cast<double>(lower);
    return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
}

std::string manifest_hash_value(const std::string& manifest, const std::string& entry)
{
    const std::string key = "\"" + entry + "\"";
    const auto key_at = manifest.find(key);
    if (key_at == std::string::npos)
        throw std::runtime_error("Shader manifest omits " + entry + ".");
    const auto colon = manifest.find(':', key_at + key.size());
    const auto quote = manifest.find('"', colon + 1);
    const auto end = manifest.find('"', quote + 1);
    if (colon == std::string::npos || quote == std::string::npos || end == std::string::npos)
        throw std::runtime_error("Shader manifest is malformed.");
    return manifest.substr(quote + 1, end - quote - 1);
}
}

configuration parse_arguments(int argc, wchar_t** argv)
{
    if (argc < 2 || (argc - 1) % 2 != 0)
        usage("Every option requires one value.");
    std::vector<std::pair<std::wstring, std::wstring>> values;
    values.reserve(static_cast<std::size_t>((argc - 1) / 2));
    for (int index = 1; index < argc; index += 2)
    {
        const std::wstring key = argv[index];
        if (!key.starts_with(L"--"))
            usage("Unknown positional argument.");
        if (std::ranges::any_of(values, [&](const auto& pair) { return pair.first == key; }))
            usage("An option was supplied more than once.");
        values.emplace_back(key, argv[index + 1]);
    }
    const auto require = [&](const wchar_t* key) -> std::wstring
    {
        const auto found = std::ranges::find_if(values, [&](const auto& pair) { return pair.first == key; });
        if (found == values.end())
            usage("A required option is missing.");
        return found->second;
    };
    for (const auto& [key, value] : values)
    {
        (void)value;
        constexpr std::array known{
            L"--profile", L"--variant", L"--adapter", L"--process-index", L"--warmup",
            L"--samples", L"--draws", L"--barriers", L"--shader-dir", L"--output"};
        if (std::ranges::find(known, key) == known.end())
            usage("An unknown option was supplied.");
    }

    configuration result;
    const std::wstring profile_value = require(L"--profile");
    if (profile_value == L"warp")
        result.selected_profile = profile::warp;
    else if (profile_value == L"diagnose")
        result.selected_profile = profile::diagnostic;
    else if (profile_value == L"certify")
        result.selected_profile = profile::certification;
    else if (profile_value == L"representative")
        result.selected_profile = profile::representative;
    else
        usage("--profile must be warp, diagnose, certify, or representative.");
    if (require(L"--variant") != L"native-cpp")
        usage("This executable only implements --variant native-cpp.");
    const std::wstring adapter = require(L"--adapter");
    const auto separator = adapter.find(L':');
    if (separator == std::wstring::npos || adapter.find(L':', separator + 1) != std::wstring::npos)
        usage("--adapter must be <low>:<high>.");
    result.adapter_low = parse_u64(adapter.substr(0, separator));
    result.adapter_high = parse_u64(adapter.substr(separator + 1));
    result.process_index = static_cast<int>(parse_u64(require(L"--process-index")));
    result.warmup_frames = parse_positive(require(L"--warmup"));
    result.measured_frames = parse_positive(require(L"--samples"));
    result.draw_count = parse_positive(require(L"--draws"));
    result.barrier_count = result.selected_profile == profile::diagnostic
        ? parse_non_negative(require(L"--barriers"))
        : parse_positive(require(L"--barriers"));
    result.shader_directory = std::filesystem::absolute(require(L"--shader-dir"));
    result.output_path = std::filesystem::absolute(require(L"--output"));
    return result;
}

std::string sha256_bytes(const void* data, std::size_t size)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("BCryptOpenAlgorithmProvider(SHA-256) failed.");
    try
    {
        DWORD object_size = 0;
        DWORD returned = 0;
        if (BCryptGetProperty(
                algorithm,
                BCRYPT_OBJECT_LENGTH,
                reinterpret_cast<PUCHAR>(&object_size),
                sizeof(object_size),
                &returned,
                0) < 0)
            throw std::runtime_error("BCryptGetProperty(OBJECT_LENGTH) failed.");
        std::vector<std::uint8_t> object(object_size);
        if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) < 0)
            throw std::runtime_error("BCryptCreateHash failed.");
        if (size != 0 && BCryptHashData(
                hash,
                const_cast<PUCHAR>(static_cast<const UCHAR*>(data)),
                static_cast<ULONG>(size),
                0) < 0)
            throw std::runtime_error("BCryptHashData failed.");
        std::array<std::uint8_t, 32> digest{};
        if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0)
            throw std::runtime_error("BCryptFinishHash failed.");
        BCryptDestroyHash(hash);
        hash = nullptr;
        BCryptCloseAlgorithmProvider(algorithm, 0);
        algorithm = nullptr;
        constexpr char hex[] = "0123456789ABCDEF";
        std::string result(digest.size() * 2, '\0');
        for (std::size_t index = 0; index < digest.size(); ++index)
        {
            result[index * 2] = hex[digest[index] >> 4];
            result[index * 2 + 1] = hex[digest[index] & 0xF];
        }
        return result;
    }
    catch (...)
    {
        if (hash != nullptr)
            BCryptDestroyHash(hash);
        if (algorithm != nullptr)
            BCryptCloseAlgorithmProvider(algorithm, 0);
        throw;
    }
}

std::vector<std::byte> read_binary_file(const std::filesystem::path& path)
{
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input)
        throw std::runtime_error("Cannot open " + path.string() + ".");
    const auto length = input.tellg();
    if (length < 0)
        throw std::runtime_error("Cannot determine the size of " + path.string() + ".");
    std::vector<std::byte> result(static_cast<std::size_t>(length));
    input.seekg(0);
    input.read(reinterpret_cast<char*>(result.data()), length);
    if (!input)
        throw std::runtime_error("Cannot read " + path.string() + ".");
    return result;
}

std::string read_text_file(const std::filesystem::path& path)
{
    const auto bytes = read_binary_file(path);
    return std::string(reinterpret_cast<const char*>(bytes.data()), bytes.size());
}

std::string sha256_file(const std::filesystem::path& path)
{
    const auto bytes = read_binary_file(path);
    return sha256_bytes(bytes.data(), bytes.size());
}

void verify_shader_artifacts(const configuration& config)
{
    const std::string manifest = read_text_file(config.shader_directory / "manifest.json");
    constexpr std::array entries{
        std::pair{"vertexMain", "vertex.dxil"},
        std::pair{"pixelMain", "pixel.dxil"},
        std::pair{"computeMain", "compute.dxil"}};
    for (const auto& [entry, file] : entries)
    {
        const std::string expected = manifest_hash_value(manifest, entry);
        const std::string actual = sha256_file(config.shader_directory / file);
        if (expected != actual)
            throw std::runtime_error(std::string("Shared Slang DXIL hash mismatch for ") + entry + ".");
    }
}

metric_distribution summarize(const std::vector<frame_sample>& samples, bool gpu)
{
    std::vector<double> values;
    values.reserve(samples.size());
    for (const auto& sample : samples)
    {
        if (gpu)
        {
            if (sample.gpu_microseconds.has_value())
                values.push_back(*sample.gpu_microseconds);
        }
        else
        {
            values.push_back(sample.cpu_microseconds);
        }
    }
    if (values.empty())
        throw std::runtime_error("A metric distribution has no samples.");
    std::ranges::sort(values);
    return {
        percentile_r7(values, 0.50),
        percentile_r7(values, 0.95),
        percentile_r7(values, 0.99),
        values.back()};
}

std::optional<metric_distribution> summarize_post_close_cleanup(
    const std::vector<frame_sample>& samples)
{
    std::vector<double> values;
    values.reserve(samples.size());
    for (const auto& sample : samples)
    {
        if (sample.post_close_cleanup_microseconds.has_value())
            values.push_back(*sample.post_close_cleanup_microseconds);
    }
    if (values.empty())
        return std::nullopt;
    std::ranges::sort(values);
    return metric_distribution{
        percentile_r7(values, 0.50),
        percentile_r7(values, 0.95),
        percentile_r7(values, 0.99),
        values.back()};
}

workload_run complete_workload(
    const configuration& config,
    workload_kind kind,
    int draw_count,
    int barrier_count,
    std::vector<frame_sample> samples,
    std::vector<calibration> calibrations,
    std::string output_sha256,
    std::string shader_manifest_sha256,
    std::vector<barrier_evidence> barriers,
    setter_evidence setters)
{
    workload_run result;
    result.workload = kind;
    result.result = config.selected_profile == profile::certification
        ? disposition::passed
        : disposition::functional_only;
    switch (config.selected_profile)
    {
    case profile::warp:
        result.reason = "Reduced-count WARP functional workload executed; not performance evidence.";
        break;
    case profile::diagnostic:
        result.reason = "Fast hardware diagnostic workload executed; never vendor-certification evidence.";
        break;
    case profile::certification:
        result.reason = "Fixed vendor workload executed.";
        break;
    case profile::representative:
        result.reason = "Public-source representative CPU frame workload executed without Queue submission.";
        break;
    }
    result.warmup_frames = config.warmup_frames;
    result.measured_frames = config.measured_frames;
    result.draw_count = draw_count;
    result.barrier_count = barrier_count;
    result.samples = std::move(samples);
    result.calibrations = std::move(calibrations);
    result.output_sha256 = std::move(output_sha256);
    result.shader_manifest_sha256 = std::move(shader_manifest_sha256);
    result.barriers = std::move(barriers);
    result.setters = setters;
    result.cpu = summarize(result.samples, false);
    if (kind != workload_kind::empty_submit &&
        kind != workload_kind::representative_frame_serial &&
        kind != workload_kind::representative_frame_parallel)
        result.gpu = summarize(result.samples, true);
    result.post_close_cleanup = summarize_post_close_cleanup(result.samples);
    return result;
}

const char* workload_pascal(workload_kind value)
{
    switch (value)
    {
    case workload_kind::empty_submit: return "EmptySubmit";
    case workload_kind::persistent_draw: return "PersistentDraw10000";
    case workload_kind::transient_draw: return "TransientDraw10000";
    case workload_kind::state_suppression: return "StateSuppression10000";
    case workload_kind::explicit_barrier: return "ExplicitBarrier4096";
    case workload_kind::three_queue_present: return "ThreeQueuePresent";
    case workload_kind::representative_frame_serial: return "RepresentativeFrameSerial";
    case workload_kind::representative_frame_parallel: return "RepresentativeFrameParallel";
    }
    return "EmptySubmit";
}

std::string fixed_output_hash(workload_kind kind, const std::string& shader_manifest_sha256)
{
    const std::string text = std::string("SomeEngine/RHI/RHI-EVID-003/") + workload_pascal(kind) +
        "/seed=0x5EED/" + shader_manifest_sha256;
    return sha256_bytes(text.data(), text.size());
}

void write_process_json(const std::filesystem::path& path, const process_run& run)
{
    if (!path.parent_path().empty())
        std::filesystem::create_directories(path.parent_path());
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output)
        throw std::runtime_error("Cannot create process JSON output.");
    output << std::setprecision(17);
    output << "{\n  \"variant\": \"nativeCpp\",\n  \"disposition\": \""
           << disposition_name(run.result) << "\",\n  \"reason\": ";
    json_string(output, run.reason);
    const auto& environment = run.environment;
    output << ",\n  \"environment\": {\n    \"operatingSystem\": ";
    json_string(output, environment.operating_system);
    output << ",\n    \"architecture\": "; json_string(output, environment.architecture);
    output << ",\n    \"processorName\": "; json_string(output, environment.processor_name);
    output << ",\n    \"processId\": " << environment.process_id
           << ",\n    \"processIndex\": " << environment.process_index
           << ",\n    \"affinityMask\": " << environment.affinity_mask
           << ",\n    \"priority\": "; json_string(output, environment.priority);
    output << ",\n    \"powerMode\": "; json_string(output, environment.power_mode);
    output << ",\n    \"adapterName\": "; json_string(output, environment.adapter_name);
    output << ",\n    \"vendorId\": " << environment.vendor_id
           << ",\n    \"deviceId\": " << environment.device_id
           << ",\n    \"adapterLuidLow\": " << environment.adapter_luid_low
           << ",\n    \"adapterLuidHigh\": " << environment.adapter_luid_high
           << ",\n    \"driverVersion\": "; json_string(output, environment.driver_version);
    output << ",\n    \"hardwareAccelerated\": " << (environment.hardware_accelerated ? "true" : "false")
           << ",\n    \"agilitySdkVersion\": " << environment.agility_sdk_version
           << ",\n    \"validationEnabled\": " << (environment.validation_enabled ? "true" : "false")
           << ",\n    \"dredEnabled\": " << (environment.dred_enabled ? "true" : "false")
           << ",\n    \"captureToolLoaded\": " << (environment.capture_tool_loaded ? "true" : "false")
           << ",\n    \"build\": {\n      \"executablePath\": ";
    json_string(output, environment.build.executable_path);
    output << ",\n      \"executableSha256\": "; json_string(output, environment.build.executable_sha256);
    output << ",\n      \"payloadSha256\": "; json_string(output, environment.build.payload_sha256);
    output << ",\n      \"assemblyVersion\": "; json_string(output, environment.build.assembly_version);
    output << ",\n      \"configuration\": "; json_string(output, environment.build.configuration);
    output << ",\n      \"commit\": "; json_string(output, environment.build.commit);
    output << ",\n      \"worktreeDirty\": " << (environment.build.worktree_dirty ? "true" : "false")
           << ",\n      \"toolchain\": "; json_string(output, environment.build.toolchain);
    output << ",\n      \"commandConstructionBoundary\": ";
    json_string(output, environment.build.command_construction_boundary);
    output << "\n    }\n  },\n  \"workloads\": [";

    for (std::size_t workload_index = 0; workload_index < run.workloads.size(); ++workload_index)
    {
        const auto& item = run.workloads[workload_index];
        output << (workload_index == 0 ? "\n" : ",\n") << "    {\n      \"workload\": \""
               << workload_name(item.workload) << "\",\n      \"disposition\": \""
               << disposition_name(item.result) << "\",\n      \"reason\": ";
        json_string(output, item.reason);
        output << ",\n      \"warmupFrames\": " << item.warmup_frames
               << ",\n      \"measuredFrames\": " << item.measured_frames
               << ",\n      \"drawCount\": " << item.draw_count
               << ",\n      \"barrierCount\": " << item.barrier_count
               << ",\n      \"samples\": [";
        for (std::size_t sample_index = 0; sample_index < item.samples.size(); ++sample_index)
        {
            const auto& sample = item.samples[sample_index];
            output << (sample_index == 0 ? "\n" : ",\n")
                   << "        {\"frameIndex\": " << sample.frame_index
                   << ", \"cpuStopwatchTicks\": " << sample.cpu_ticks
                   << ", \"cpuMicroseconds\": " << sample.cpu_microseconds
                   << ", \"gpuMicroseconds\": ";
            if (sample.gpu_microseconds.has_value()) output << *sample.gpu_microseconds; else output << "null";
            output << ", \"managedAllocatedBytes\": 0, \"etwAllocationEvents\": 0, \"completionValue\": "
                   << sample.completion_value
                   << ", \"postCloseCleanupStopwatchTicks\": ";
            if (sample.post_close_cleanup_ticks.has_value()) output << *sample.post_close_cleanup_ticks; else output << "null";
            output << ", \"postCloseCleanupMicroseconds\": ";
            if (sample.post_close_cleanup_microseconds.has_value()) output << *sample.post_close_cleanup_microseconds; else output << "null";
            output << "}";
        }
        output << (item.samples.empty() ? "" : "\n      ") << "],\n      \"calibrations\": [";
        for (std::size_t calibration_index = 0; calibration_index < item.calibrations.size(); ++calibration_index)
        {
            const auto& value = item.calibrations[calibration_index];
            output << (calibration_index == 0 ? "\n" : ",\n")
                   << "        {\"queue\": \"" << queue_name(value.queue)
                   << "\", \"frameIndex\": " << value.frame_index
                   << ", \"cpuCounter\": " << value.cpu_counter
                   << ", \"cpuFrequency\": " << value.cpu_frequency
                   << ", \"queueCounter\": " << value.queue_counter
                   << ", \"queueFrequency\": " << value.queue_frequency << "}";
        }
        output << (item.calibrations.empty() ? "" : "\n      ") << "],\n      \"outputSha256\": ";
        json_string(output, item.output_sha256);
        output << ",\n      \"shaderManifestSha256\": "; json_string(output, item.shader_manifest_sha256);
        output << ",\n      \"barriers\": [";
        for (std::size_t barrier_index = 0; barrier_index < item.barriers.size(); ++barrier_index)
        {
            const auto& value = item.barriers[barrier_index];
            output << (barrier_index == 0 ? "\n" : ",\n")
                   << "        {\"publicOrdinal\": " << value.public_ordinal << ", \"publicKind\": ";
            json_string(output, value.public_kind);
            output << ", \"nativeOrdinal\": " << value.native_ordinal
                   << ", \"nativeExpansionCount\": " << value.native_expansion_count
                   << ", \"expansionReason\": ";
            if (value.expansion_reason.has_value()) json_string(output, *value.expansion_reason); else output << "null";
            output << "}";
        }
        output << (item.barriers.empty() ? "" : "\n      ") << "],\n      \"nativeSetters\": {"
               << "\"pipelineSetters\": " << item.setters.pipeline
               << ", \"persistentBindingSetters\": " << item.setters.persistent_binding
               << ", \"viewportSetters\": " << item.setters.viewport
               << ", \"scissorSetters\": " << item.setters.scissor
               << ", \"drawCalls\": " << item.setters.draws << "},\n      \"workloadEvidence\": ";
        if (!item.workload_evidence.has_value())
        {
            output << "null";
        }
        else
        {
            const auto& evidence = *item.workload_evidence;
            output << "{\"objectPacketCount\": " << evidence.object_packet_count
                   << ", \"logicalDrawRequests\": " << evidence.logical_draw_requests
                   << ", \"logicalMaterialBindingRequests\": "
                   << evidence.logical_material_binding_requests
                   << ", \"nativeDrawCommands\": " << evidence.native_draw_commands
                   << ", \"nativeMaterialBindingCommands\": "
                   << evidence.native_material_binding_commands
                   << ", \"commandListResetCount\": " << evidence.command_list_reset_count
                   << ", \"commandListCloseCount\": " << evidence.command_list_close_count
                   << ", \"barrierCommands\": " << evidence.barrier_commands
                   << ", \"workerCount\": " << evidence.worker_count
                   << ", \"drawCallShape\": ";
            json_string(output, evidence.draw_call_shape);
            output << "}";
        }
        output << ",\n      \"cpu\": ";
        const auto write_metric = [&](const std::optional<metric_distribution>& metric)
        {
            if (!metric.has_value()) { output << "null"; return; }
            output << "{\"p50\": " << metric->p50 << ", \"p95\": " << metric->p95
                   << ", \"p99\": " << metric->p99 << ", \"maximum\": " << metric->maximum << "}";
        };
        write_metric(item.cpu);
        output << ",\n      \"gpu\": ";
        write_metric(item.gpu);
        output << ",\n      \"postCloseCleanup\": ";
        write_metric(item.post_close_cleanup);
        output << "\n    }";
    }
    output << (run.workloads.empty() ? "" : "\n  ") << "]\n}\n";
    if (!output)
        throw std::runtime_error("Writing process JSON failed.");
}

std::string utf8(const std::wstring& value)
{
    if (value.empty())
        return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0)
        throw std::runtime_error("WideCharToMultiByte failed.");
    std::string result(static_cast<std::size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
            result.data(), size, nullptr, nullptr) != size)
        throw std::runtime_error("WideCharToMultiByte failed.");
    return result;
}

std::string exception_message()
{
    try
    {
        throw;
    }
    catch (const std::exception& exception)
    {
        return exception.what();
    }
    catch (...)
    {
        return "Unknown native exception.";
    }
}
}
