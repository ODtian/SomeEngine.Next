#include "lucid_renderer.h"
#include "scene.h"
#include "simple_renderer.h"
#include "shading.h"
#include <fwk/gfx/camera.h>
#include <fwk/gfx/shader_compiler.h>
#include <fwk/math/frustum.h>
#include <fwk/math/matrix4.h>
#include <fwk/vulkan/vulkan_buffer.h>
#include <fwk/vulkan/vulkan_command_queue.h>
#include <fwk/vulkan/vulkan_device.h>
#include <fwk/vulkan/vulkan_image.h>
#include <fwk/vulkan/vulkan_instance.h>
#include <fwk/vulkan/vulkan_internal.h>
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdlib>
#include <fstream>
#include <iomanip>
#include <string>
#include <numeric>
#include <vector>

struct InputTriangle {
    float positions[12], color[4];
    uint32_t triangleId;
};
static_assert(sizeof(InputTriangle) == 68);

template <class T> T readValue(std::ifstream &input) {
    T value;
    input.read(reinterpret_cast<char *>(&value), sizeof(value));
    return value;
}

float decodeHalf(uint16_t value) {
    uint32_t exponent = (value >> 10) & 31, mantissa = value & 1023;
    float result = exponent == 31 ? std::bit_cast<float>(0x7f800000u | (mantissa << 13)) :
        exponent == 0 ? std::ldexp(float(mantissa), -24) : std::ldexp(1.f + float(mantissa) / 1024, int(exponent) - 15);
    return (value & 0x8000) ? -result : result;
}

std::vector<uint16_t> readColors(VulkanDevice &device, PVImage image, int width, int height) {
    VkDevice vkDevice = device.handle();
    VkBufferCreateInfo bufferInfo{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bufferInfo.size = uint64_t(width) * height * 8;
    bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    VkBuffer buffer;
    vkCreateBuffer(vkDevice, &bufferInfo, nullptr, &buffer);
    VkMemoryRequirements requirements;
    vkGetBufferMemoryRequirements(vkDevice, buffer, &requirements);
    auto properties = device.physInfo().mem_properties;
    uint32_t memoryType = 0;
    while ((requirements.memoryTypeBits & (1u << memoryType)) == 0 ||
        (properties.memoryTypes[memoryType].propertyFlags & (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) !=
        (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) memoryType++;
    VkMemoryAllocateInfo memoryInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    memoryInfo.allocationSize = requirements.size;
    memoryInfo.memoryTypeIndex = memoryType;
    VkDeviceMemory memory;
    vkAllocateMemory(vkDevice, &memoryInfo, nullptr, &memory);
    vkBindBufferMemory(vkDevice, buffer, memory, 0);
    auto &commands = device.cmdQueue();
    commands.fullBarrier();
    VkBufferImageCopy copy{};
    copy.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    copy.imageExtent = {uint32_t(width), uint32_t(height), 1};
    vkCmdCopyImageToBuffer(commands.bufferHandle(), image->handle(), VK_IMAGE_LAYOUT_GENERAL, buffer, 1, &copy);
    commands.submit();
    commands.finish();
    void *mapped;
    vkMapMemory(vkDevice, memory, 0, memoryInfo.allocationSize, 0, &mapped);
    auto *values = static_cast<uint16_t *>(mapped);
    std::vector<uint16_t> result(values, values + width * height * 4);
    vkUnmapMemory(vkDevice, memory);
    vkDestroyBuffer(vkDevice, buffer, nullptr);
    vkFreeMemory(vkDevice, memory, nullptr);
    return result;
}

int runTransparencyComparison(const char *inputPath, const char *outputPath) {
    auto instance = VulkanInstance::create({}).get();
    VDeviceSetup setup;
    setup.features.emplace();
    setup.features->shaderInt64 = VK_TRUE;
    setup.features->samplerAnisotropy = VK_TRUE;
    setup.features->fillModeNonSolid = VK_TRUE;
    setup.extensions = {"VK_EXT_shader_subgroup_vote", "VK_EXT_shader_subgroup_ballot"};
    auto adapter = *instance->physicalDeviceIds().begin();
    for (auto candidate : instance->physicalDeviceIds())
        if (instance->info(candidate).defaultScore() > instance->info(adapter).defaultScore()) adapter = candidate;
    setup.queues.emplace_back(instance->info(adapter).findQueues(VQueueCap::graphics | VQueueCap::compute)[0], 1);
    auto device = instance->createDevice(adapter, setup).get();
    auto &commands = device->cmdQueue();
    ShaderCompilerSetup compilerSetup;
    compilerSetup.vulkan_version = device->version();
    compilerSetup.source_dirs.emplace_back(dataPath("shaders"));
    const char *depthFilter = std::getenv("LUCID_DEPTH_FILTER");
    if (!depthFilter) depthFilter = "3";
    compilerSetup.spirv_cache_dir = dataPath(format("spirv_transparency_df%", depthFilter));
    ShaderCompiler compiler(compilerSetup);
    auto shaderConfig = getShaderConfig(*device);
    shaderConfig.predefined_macros.emplace_back("BENCHMARK_DEPTH_FILTER", depthFilter);
    LucidRenderer::addShaderDefs(*device, compiler, shaderConfig);
    SimpleRenderer::addShaderDefs(*device, compiler, getShaderConfig(*device));
    std::ifstream input(inputPath, std::ios::binary);
    int cases = readValue<int>(input);
    std::ofstream output(outputPath);
    output << std::setprecision(12) << "{\"Device\":\"" << device->physInfo().properties.deviceName
        << "\",\"InputColor\":\"RGBA8 UNORM\",\"OutputColor\":\"RGBA16F\",\"DepthFilter\":" << depthFilter
        << ",\"Coverage\":\"Barycentric sample test\",\"Measurements\":[\n";
    bool firstRow = true;
    std::vector<std::string> inputChecks;
    for (int sample = 0; sample < cases; sample++) {
        int nameLength = readValue<int>(input);
        std::string name(nameLength, ' ');
        input.read(name.data(), nameLength);
        int models = readValue<int>(input);
        float alpha = readValue<float>(input);
        int width = readValue<int>(input), height = readValue<int>(input), triangleCount = readValue<int>(input);
        std::vector<InputTriangle> triangles(triangleCount);
        input.read(reinterpret_cast<char *>(triangles.data()), triangles.size() * sizeof(InputTriangle));
        std::vector<float> reference;
        if (width == 128) {
            reference.resize(width * height * 3);
            input.read(reinterpret_cast<char *>(reference.data()), reference.size() * 4);
        }
        device->beginFrame().check();
        {
            CameraParams cameraParams;
            cameraParams.viewport = IRect(0, 0, width, height);
            cameraParams.depth = {1.f, 1024.f};
            Camera camera(float3(0), float3(0, 0, 1), float3(0, 1, 0), cameraParams);
            Matrix4 inverse = inverseOrZero(camera.matrix());
            Frustum frustum = camera.frustum();
            Scene scene;
            scene.materials.emplace_back("Flat visibility color");
            SceneMesh mesh;
            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++) {
                auto &triangle = triangles[triangleIndex];
                int first = scene.positions.size();
                IColor color(int(std::lround(triangle.color[0] * 255)), int(std::lround(triangle.color[1] * 255)),
                    int(std::lround(triangle.color[2] * 255)), int(std::lround(triangle.color[3] * 255)));
                bool paired = triangleIndex + 1 < triangleCount;
                if (paired) {
                    auto &second = triangles[triangleIndex + 1];
                    for (int component = 0; component < 4; component++)
                        paired &= triangle.positions[component] == second.positions[component] &&
                            triangle.positions[8 + component] == second.positions[4 + component] &&
                            triangle.color[component] == second.color[component];
                }
                for (int vertex = 0; vertex < (paired ? 4 : 3); vertex++) {
                    auto *position = vertex == 3 ? triangles[triangleIndex + 1].positions + 8 : triangle.positions + vertex * 4;
                    float4 world = inverse * float4(position[0], -position[1], position[2] * 2 - 1, 1);
                    scene.positions.emplace_back(world.xyz() / world.w);
                    scene.colors.emplace_back(color);
                }
                mesh.tris.emplace_back(first, first + 1, first + 2);
                mesh.quads.emplace_back(first, first + 1, first + 2, first + (paired ? 3 : 2));
                if (paired) {
                    mesh.tris.emplace_back(first, first + 2, first + 3);
                    triangleIndex++;
                }
            }
            scene.meshes.emplace_back(std::move(mesh));
            scene.updateRenderingData(*device).check();
            SceneDrawCall draw;
            draw.material_id = 0;
            draw.num_tris = triangleCount;
            draw.num_quads = scene.meshes[0].quads.size();
            draw.opts = DrawCallOpt::has_vertex_colors;
            SceneLighting lighting = SceneLighting::makeDefault();
            auto image = VulkanImage::create(*device, {VColorFormat::rgba16_sfloat, int2(width, height),
                VImageUsage::storage | VImageUsage::color_att | VImageUsage::transfer_src | VImageUsage::transfer_dst, VImageLayout::general}).get();
            auto target = VulkanImageView::create(image);
            RenderContext context{scene, *device, {}, scene.verts, scene.tris_ib, scene.quads_ib, {draw},
                scene.materials, device->dummyImage2D(), device->dummyImage2D(), lighting, frustum, camera, target};
            LucidRenderer renderer;
            uint binReferences = 0;
            for (auto quad : scene.meshes[0].quads) {
                int firstX = width, firstY = height, lastX = 0, lastY = 0;
                for (uint vertex : quad) {
                    float4 clip = camera.matrix() * float4(scene.positions[vertex], 1);
                    int x = int((clip.x / clip.w * .5f + .5f) * width);
                    int y = int((clip.y / clip.w * .5f + .5f) * height);
                    firstX = std::min(firstX, x); lastX = std::max(lastX, x);
                    firstY = std::min(firstY, y); lastY = std::max(lastY, y);
                }
                firstX = std::clamp(firstX, 0, width - 1); lastX = std::clamp(lastX, 0, width - 1);
                firstY = std::clamp(firstY, 0, height - 1); lastY = std::clamp(lastY, 0, height - 1);
                binReferences += (lastX / 32 - firstX / 32 + 1) * (lastY / 32 - firstY / 32 + 1);
            }
            renderer.exConstruct(*device, compiler, LucidRenderOpt::alpha_threshold, int2(width, height),
                scene.meshes[0].quads.size(), binReferences).check();
            renderer.render(context).check();
            renderer.render(context).check();
            commands.submit();
            commands.finish();
            auto measure = [&](bool cached, int warmup, int samples) {
                int frames = warmup + samples;
                VkQueryPoolCreateInfo queryInfo{VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO};
                queryInfo.queryType = VK_QUERY_TYPE_TIMESTAMP;
                queryInfo.queryCount = frames * 2;
                VkQueryPool queries;
                vkCreateQueryPool(device->handle(), &queryInfo, nullptr, &queries);
                vkCmdResetQueryPool(commands.bufferHandle(), queries, 0, frames * 2);
                for (int frame = 0; frame < frames; frame++) {
                    commands.clearColor(image, VClearValue(float4(.125f, .125f, .125f, 1)));
                    commands.fullBarrier();
                    vkCmdWriteTimestamp(commands.bufferHandle(), VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, queries, frame * 2);
                    if (cached) renderer.renderCached(context).check();
                    else renderer.render(context).check();
                    vkCmdWriteTimestamp(commands.bufferHandle(), VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, queries, frame * 2 + 1);
                }
                commands.submit();
                commands.finish();
                std::vector<uint64_t> values(frames * 2);
                vkGetQueryPoolResults(device->handle(), queries, 0, frames * 2, values.size() * 8, values.data(), 8,
                    VK_QUERY_RESULT_64_BIT | VK_QUERY_RESULT_WAIT_BIT);
                std::vector<double> times(samples);
                for (int frame = warmup; frame < frames; frame++)
                    times[frame - warmup] = (values[frame * 2 + 1] - values[frame * 2]) * device->physInfo().properties.limits.timestampPeriod / 1e6;
                vkDestroyQueryPool(device->handle(), queries, nullptr);
                return times;
            };
            double warmupGpuMs = 0;
            if (width == 1280) {
                auto warmup = measure(false, 0, 16);
                warmupGpuMs = std::accumulate(warmup.begin(), warmup.end(), 0.0);
                while (warmupGpuMs < 250) {
                    double mean = std::accumulate(warmup.begin(), warmup.end(), 0.0) / warmup.size();
                    int frames = int(std::ceil((250 - warmupGpuMs) / mean));
                    warmup = measure(false, 0, frames);
                    warmupGpuMs += std::accumulate(warmup.begin(), warmup.end(), 0.0);
                }
            }
            for (int cached = 0; cached < 2; cached++) {
                auto times = measure(cached != 0, 32, 24);
                std::sort(times.begin(), times.end());
                double squared = 0, maximum = 0;
                if (width == 128) {
                    auto colors = readColors(*device, image, width, height);
                    for (int pixel = 0; pixel < width * height; pixel++)
                    for (int channel = 0; channel < 3; channel++) {
                        double difference = decodeHalf(colors[pixel * 4 + channel]) - reference[pixel * 3 + channel];
                        squared += difference * difference;
                        maximum = std::max(maximum, std::abs(difference));
                    }
                }
                if (!firstRow) output << ",\n";
                firstRow = false;
                output << "{\"Geometry\":\"" << name << "\",\"Models\":" << models << ",\"Alpha\":" << alpha
                    << ",\"Width\":" << width << ",\"Height\":" << height << ",\"Method\":\"Lucid "
                    << (cached ? "cached geometry" : "full pipeline") << "\",\"MedianGpuMs\":" << times[12]
                    << ",\"P95GpuMs\":" << times[22]
                    << ",\"RendererBufferBytes\":" << renderer.transparencyBytes()
                    << ",\"GeometryInputBytes\":" << scene.verts.positions.byteSize() + scene.verts.colors.byteSize() + scene.tris_ib.byteSize() + scene.quads_ib.byteSize()
                    << ",\"AdditionalGpuBytes\":" << renderer.transparencyBytes() + scene.verts.positions.byteSize() + scene.verts.colors.byteSize() + scene.tris_ib.byteSize() + scene.quads_ib.byteSize()
                    << ",\"CommonWarmupGpuMs\":" << warmupGpuMs << ",\"Rmse\":" << std::sqrt(squared / (width * height * 3)) << ",\"MaximumError\":" << maximum << "}";
                output.flush();
                print("% % alpha %: % % ms max %\n", name.c_str(), models, alpha, cached ? "cached" : "full", times[12], maximum);
                if ((sample == 4 || sample == 18) && cached == 1 && std::atoi(depthFilter) == 3) {
                    vector<u32> indices;
                    for (int index = scene.meshes[0].tris.size() - 1; index >= 0; index--)
                        for (uint value : scene.meshes[0].tris[index]) indices.emplace_back(value);
                    auto hardwareContext = context;
                    hardwareContext.tris_ib = VulkanBuffer::createAndUpload(*device, indices,
                        VBufferUsage::storage | VBufferUsage::index).get();
                    SimpleRenderer hardware;
                    hardware.exConstruct(*device, compiler, IRect(0, 0, width, height),
                        VAttachment(VColorFormat::rgba16_sfloat, VAttachmentSync(VLoadOp::load, VStoreOp::store,
                            VImageLayout::general, VImageLayout::general, VImageLayout::general))).check();
                    commands.clearColor(image, VClearValue(float4(.125f, .125f, .125f, 1)));
                    commands.fullBarrier();
                    hardware.render(hardwareContext, false).check();
                    commands.submit(); commands.finish();
                    auto colors = readColors(*device, image, width, height);
                    double squared = 0, maximum = 0;
                    for (int pixel = 0; pixel < width * height; pixel++)
                    for (int channel = 0; channel < 3; channel++) {
                        double delta = decodeHalf(colors[pixel * 4 + channel]) - reference[pixel * 3 + channel];
                        squared += delta * delta; maximum = std::max(maximum, std::abs(delta));
                    }
                    print("Native hardware check: RMSE % max %\n", std::sqrt(squared / (width * height * 3)), maximum);
                    inputChecks.push_back("{\"Geometry\":\"" + name + "\",\"Rmse\":" +
                        std::to_string(std::sqrt(squared / (width * height * 3))) + ",\"MaximumError\":" + std::to_string(maximum) + "}");
                }
            }
        }
        device->finishFrame().check();
        for (int frame = 0; frame < 3; frame++) { device->beginFrame().check(); device->finishFrame().check(); }
        device->waitForIdle();
    }
    output << "\n],\"InputChecks\":[";
    for (size_t index = 0; index < inputChecks.size(); index++) output << (index ? "," : "") << inputChecks[index];
    output << "]}\n";
    return 0;
}
