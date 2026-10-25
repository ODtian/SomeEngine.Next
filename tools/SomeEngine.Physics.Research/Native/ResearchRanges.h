#pragma once
#include <type_traits>
#include "BridgeState.h"
#include "ResearchStep.h"

// The managed scheduler joins before this call returns. Each native range has
// its own step context; scene topology and registration stay on the caller.
template<class Function>
void researchParallelRanges(int count, Function&& execute)
{
    if (count == 0) return;
    ResearchStep* step = researchStep;
    int batches = std::min(count, std::max(1, step->workerCount) * 4);
    struct Result
    {
        float maximumChangeSquared = 0;
#ifdef AVBD_PROFILE
        StepMeasurements measurements;
#endif
    };
    std::vector<Result> results(batches);
    using Action = std::remove_reference_t<Function>;
    struct Work
    {
        ResearchStep* step;
        Action* execute;
        Result* results;
        int count, batches;
    } work{step, &execute, results.data(), count, batches};
    auto run = [](void* context, int batch)
    {
        auto& work = *static_cast<Work*>(context);
        ResearchStep local = *work.step;
        local.maximumBodyChangeSquared = 0;
#ifdef AVBD_PROFILE
        local.measurements = &work.results[batch].measurements;
#endif
        ResearchStep* previous = researchStep;
        researchStep = &local;
        (*work.execute)(work.count * batch / work.batches, work.count * (batch + 1) / work.batches);
        work.results[batch].maximumChangeSquared = local.maximumBodyChangeSquared;
        researchStep = previous;
    };
    step->runRanges(step->jobContext, batches, run, &work);
    for (const auto& result : results)
    {
        step->maximumBodyChangeSquared = max(step->maximumBodyChangeSquared, result.maximumChangeSquared);
#ifdef AVBD_PROFILE
        auto& total = researchMeasurements();
        for (int phase = 0; phase < (int)StepPhase::Count; ++phase)
            total.milliseconds[phase] += result.measurements.milliseconds[phase];
        for (int counter = 0; counter < (int)SceneCount::Count; ++counter)
            total.counts[counter] += result.measurements.counts[counter];
#endif
    }
}
