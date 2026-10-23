#include <acl/core/ansi_allocator.h>
#include <acl/core/compressed_tracks.h>
#include <acl/core/sample_rounding_policy.h>
#include <acl/core/track_writer.h>
#include <acl/compression/compress.h>
#include <acl/compression/track_array.h>
#include <acl/compression/transform_error_metrics.h>
#include <acl/decompression/decompress.h>
#include <rtm/qvvf.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <utility>

struct BoneTransform
{
    float translation[3];
    float rotation[4];
    float scale[3];
};

struct PoseWriter final : acl::track_writer
{
    BoneTransform* output;

    explicit PoseWriter(BoneTransform* output) : output(output) {}

    void RTM_SIMD_CALL write_rotation(uint32_t bone, rtm::quatf_arg0 value)
    {
        rtm::quat_store(value, output[bone].rotation);
    }

    void RTM_SIMD_CALL write_translation(uint32_t bone, rtm::vector4f_arg0 value)
    {
        rtm::vector_store3(value, output[bone].translation);
    }

    void RTM_SIMD_CALL write_scale(uint32_t bone, rtm::vector4f_arg0 value)
    {
        rtm::vector_store3(value, output[bone].scale);
    }
};

extern "C" __declspec(dllexport) int se_acl_compress(const BoneTransform* samples,
    uint32_t bone_count, uint32_t sample_count, float sample_rate, const int16_t* parents,
    float precision, float shell_distance, void** output, uint32_t* output_size,
    char* error, int error_capacity)
{
    acl::ansi_allocator allocator;
    acl::track_array_qvvf tracks(allocator, bone_count);
    for (uint32_t bone = 0; bone < bone_count; ++bone)
    {
        acl::track_desc_transformf description;
        description.output_index = bone;
        description.parent_index = parents[bone] < 0 ? acl::k_invalid_track_index : parents[bone];
        description.precision = precision;
        description.shell_distance = shell_distance;
        auto track = acl::track_qvvf::make_reserve(description, allocator, sample_count, sample_rate);
        for (uint32_t sample = 0; sample < sample_count; ++sample)
        {
            const BoneTransform& value = samples[sample * bone_count + bone];
            track[sample] = rtm::qvv_set(
                rtm::quat_set(value.rotation[0], value.rotation[1], value.rotation[2], value.rotation[3]),
                rtm::vector_set(value.translation[0], value.translation[1], value.translation[2]),
                rtm::vector_set(value.scale[0], value.scale[1], value.scale[2]));
        }
        tracks[bone] = std::move(track);
    }

    acl::qvvf_matrix3x4f_transform_error_metric error_metric;
    acl::compression_settings settings = acl::get_default_compression_settings();
    settings.level = acl::compression_level8::medium;
    settings.rotation_format = acl::rotation_format8::quatf_drop_w_variable;
    settings.translation_format = acl::vector_format8::vector3f_variable;
    settings.scale_format = acl::vector_format8::vector3f_variable;
    settings.error_metric = &error_metric;
    settings.optimize_loops = false;
    acl::compressed_tracks* compressed = nullptr;
    acl::output_stats statistics;
    acl::error_result result = acl::compress_track_list(allocator, tracks, settings, compressed, statistics);
    if (result.any())
    {
        std::snprintf(error, error_capacity, "%s", result.c_str());
        return 1;
    }
    *output_size = compressed->get_size();
    *output = std::malloc(*output_size);
    std::memcpy(*output, compressed, *output_size);
    allocator.deallocate(compressed, *output_size);
    return 0;
}

extern "C" __declspec(dllexport) void se_acl_free(void* data)
{
    std::free(data);
}

extern "C" __declspec(dllexport) int se_acl_info(const void* data, uint32_t size,
    uint32_t* bone_count, float* sample_rate, char* error, int error_capacity)
{
    if (size < sizeof(acl::compressed_tracks))
    {
        std::snprintf(error, error_capacity, "ACL track header is incomplete.");
        return 1;
    }
    const auto& tracks = *static_cast<const acl::compressed_tracks*>(data);
    if (tracks.get_size() != size)
    {
        std::snprintf(error, error_capacity, "ACL track size differs from its stored data.");
        return 1;
    }
    acl::error_result result = tracks.is_valid(false);
    if (result.any())
    {
        std::snprintf(error, error_capacity, "%s", result.c_str());
        return 1;
    }
    *bone_count = tracks.get_num_tracks();
    *sample_rate = tracks.get_sample_rate();
    return 0;
}

extern "C" __declspec(dllexport) void se_acl_sample(const void* data, const float* times,
    int sample_count, const int16_t* bones, int bone_count, BoneTransform* output, int row_stride)
{
    const auto& tracks = *static_cast<const acl::compressed_tracks*>(data);
    acl::decompression_context<acl::default_transform_decompression_settings> context;
    context.initialize(tracks);
    for (int sample = 0; sample < sample_count; ++sample)
    {
        context.seek(times[sample], acl::sample_rounding_policy::none);
        PoseWriter writer(output + sample * row_stride);
        if (bone_count == 0)
            context.decompress_tracks(writer);
        else
            for (int bone = 0; bone < bone_count; ++bone)
                context.decompress_track(bones[bone], writer);
    }
}
