#ifndef PHYSICS_XPBD_CHAINS
#define PHYSICS_XPBD_CHAINS
#include "physics_body_prediction.slang"
#include "physics_xpbd_length_math.slang"
#include "physics_xpbd_bend_math.slang"

struct DeviceXpbdChainStep
{
    float duration; float maximumStep; float damping; uint iterations;
    float3 acceleration; uint substep; uint2 number; uint2 time;
};
interface IDeviceXpbdChain
{
    bool active(uint chain);
    DeviceXpbdChainStep step(uint chain);
    uint bodyCount(uint chain);
    uint body(uint chain,uint point);
    float3 target(uint chain,uint point);
    uint lengthRow(uint chain,uint point);
    uint bendCount(uint chain);
    uint bendRow(uint chain,uint bend);
};
struct DeviceXpbdChainBodies : IDeviceBodySelection { bool contains(uint body) { return true; } };

void solveDeviceXpbdChain<TChain : IDeviceXpbdChain>(uint chain,TChain input)
{
    if (!input.active(chain)) return;
    DeviceXpbdChainStep frame = input.step(chain);
    uint steps = uint(ceil(frame.duration/frame.maximumStep));
    DeviceXpbdLengthProjection<DeviceXpbdChainBodies> lengths;
    DeviceXpbdBendProjection<DeviceXpbdChainBodies> bends;
    DeviceXpbdStep projection = (DeviceXpbdStep)0;
    projection.number = frame.number;
    for (uint step = 0; step < steps; step++)
    {
        float h = frame.duration/float(steps);
        DeviceBodyStep bodyStep = (DeviceBodyStep)0;
        bodyStep.duration = h; bodyStep.acceleration = frame.acceleration;
        bodyStep.number = frame.number; bodyStep.time = frame.time;
        bodyStep.substep = frame.substep*steps+step;
        for (uint point = 0; point < input.bodyCount(chain); point++)
        {
            uint index = input.body(chain,point);
            DeviceBodyState body = DeviceBodyStates[index];
            if (body.motion == 1) body.linearVelocity = (input.target(chain,point)-body.position)/(float(steps-step)*h);
            else body.linearVelocity *= exp(-frame.damping*h);
            DeviceBodyStates[index] = predictDeviceBodyState(body,bodyStep,true,false);
        }
        projection.duration = h;
        for (uint point = 1; point < input.bodyCount(chain); point++) lengths.initialize(input.lengthRow(chain,point),projection);
        for (uint bend = 0; bend < input.bendCount(chain); bend++) bends.initialize(input.bendRow(chain,bend),projection);
        for (uint iteration = 0; iteration < frame.iterations; iteration++)
        {
            for (uint point = 1; point < input.bodyCount(chain); point++) lengths.project(input.lengthRow(chain,point),projection);
            for (uint bend = 0; bend < input.bendCount(chain); bend++) bends.project(input.bendRow(chain,bend),projection);
        }
        for (uint point = 0; point < input.bodyCount(chain); point++)
        {
            uint index = input.body(chain,point);
            DeviceBodyStates[index] = updateDeviceBodyVelocityState(DeviceBodyStates[index],h);
        }
    }
}
#endif
