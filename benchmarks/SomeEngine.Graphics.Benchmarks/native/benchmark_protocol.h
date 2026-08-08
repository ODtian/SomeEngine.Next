#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace someengine::graphics::benchmark
{
enum class profile
{
    warp,
    certification,
};

enum class disposition
{
    passed,
    failed,
    functional_only,
    unexecuted,
};

enum class workload_kind
{
    empty_submit,
    persistent_draw,
    transient_draw,
    state_suppression,
    explicit_barrier,
    three_queue_present,
};

enum class queue_kind
{
    graphics,
    compute,
    copy,
};

struct configuration
{
    profile selected_profile = profile::warp;
    std::uint64_t adapter_low = 0;
    std::uint64_t adapter_high = 0;
    int process_index = 0;
    int warmup_frames = 0;
    int measured_frames = 0;
    int draw_count = 0;
    int barrier_count = 0;
    std::filesystem::path shader_directory;
    std::filesystem::path output_path;
};

struct calibration
{
    queue_kind queue = queue_kind::graphics;
    int frame_index = 0;
    std::int64_t cpu_counter = 0;
    std::int64_t cpu_frequency = 0;
    std::uint64_t queue_counter = 0;
    std::uint64_t queue_frequency = 0;
};

struct frame_sample
{
    int frame_index = 0;
    std::int64_t cpu_ticks = 0;
    double cpu_microseconds = 0;
    std::optional<double> gpu_microseconds;
    std::uint64_t completion_value = 0;
};

struct barrier_evidence
{
    int public_ordinal = 0;
    std::string public_kind;
    int native_ordinal = 0;
    int native_expansion_count = 1;
    std::optional<std::string> expansion_reason;
};

struct setter_evidence
{
    int pipeline = 0;
    int persistent_binding = 0;
    int viewport = 0;
    int scissor = 0;
    int draws = 0;
};

struct metric_distribution
{
    double p50 = 0;
    double p95 = 0;
    double p99 = 0;
    double maximum = 0;
};

struct workload_run
{
    workload_kind workload = workload_kind::empty_submit;
    disposition result = disposition::failed;
    std::string reason;
    int warmup_frames = 0;
    int measured_frames = 0;
    int draw_count = 0;
    int barrier_count = 0;
    std::vector<frame_sample> samples;
    std::vector<calibration> calibrations;
    std::string output_sha256;
    std::string shader_manifest_sha256;
    std::vector<barrier_evidence> barriers;
    setter_evidence setters;
    std::optional<metric_distribution> cpu;
    std::optional<metric_distribution> gpu;
};

struct build_identity
{
    std::string executable_path;
    std::string executable_sha256;
    std::string payload_sha256;
    std::string assembly_version = "native";
    std::string configuration;
    std::string commit;
    bool worktree_dirty = false;
    std::string toolchain;
};

struct runtime_environment
{
    std::string operating_system;
    std::string architecture = "X64";
    std::string processor_name;
    std::uint32_t process_id = 0;
    int process_index = 0;
    std::int64_t affinity_mask = 0;
    std::string priority;
    std::string power_mode;
    std::string adapter_name;
    std::uint32_t vendor_id = 0;
    std::uint32_t device_id = 0;
    std::uint64_t adapter_luid_low = 0;
    std::uint64_t adapter_luid_high = 0;
    std::string driver_version;
    bool hardware_accelerated = false;
    std::uint32_t agility_sdk_version = 619;
    bool validation_enabled = false;
    bool dred_enabled = false;
    bool capture_tool_loaded = false;
    build_identity build;
};

struct process_run
{
    disposition result = disposition::failed;
    std::string reason;
    runtime_environment environment;
    std::vector<workload_run> workloads;
};

configuration parse_arguments(int argc, wchar_t** argv);
std::string sha256_bytes(const void* data, std::size_t size);
std::string sha256_file(const std::filesystem::path& path);
std::vector<std::byte> read_binary_file(const std::filesystem::path& path);
std::string read_text_file(const std::filesystem::path& path);
void verify_shader_artifacts(const configuration& config);
metric_distribution summarize(const std::vector<frame_sample>& samples, bool gpu);
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
    setter_evidence setters);
std::string fixed_output_hash(workload_kind kind, const std::string& shader_manifest_sha256);
void write_process_json(const std::filesystem::path& path, const process_run& run);
std::string utf8(const std::wstring& value);
std::string exception_message();
const char* workload_pascal(workload_kind value);
}
