#pragma once

#ifdef AVBD_PROFILE
#include <chrono>

enum class StepPhase { CandidatePairs, ContactGeometry, SupportBuild, Primal, Dual, SupportRefresh, ForceTransfer, JointBounds, Prediction, Velocity, Count };
enum class SceneCount { SweepVisits, CandidatePairs, NarrowQueries, GeometryReuses, GraphReuses, GraphBuilds, SupportNodes, Islands, LargestIsland, ContactPoints, ColorGroups, Count };

struct StepMeasurements
{
    double milliseconds[(int)StepPhase::Count]{};
    double counts[(int)SceneCount::Count]{};
};

struct StepTimer
{
    StepMeasurements& measurements;
    StepPhase phase;
    std::chrono::steady_clock::time_point begin = std::chrono::steady_clock::now();
    bool running = true;
    StepTimer(StepMeasurements& values, StepPhase selected) : measurements(values), phase(selected) {}
    void finish()
    {
        if (!running) return;
        measurements.milliseconds[(int)phase] += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin).count();
        running = false;
    }
    ~StepTimer() { finish(); }
};

StepMeasurements& researchMeasurements();

#define RESEARCH_TIME(name, phase) StepTimer name(researchMeasurements(), StepPhase::phase)
#define RESEARCH_TIME_END(name) name.finish()
#define RESEARCH_COUNT(name, value) (researchMeasurements().counts[(int)SceneCount::name] += (value))
#define RESEARCH_MAX(name, value) (researchMeasurements().counts[(int)SceneCount::name] = std::max(researchMeasurements().counts[(int)SceneCount::name], (double)(value)))
#else
#define RESEARCH_TIME(name, phase)
#define RESEARCH_TIME_END(name)
#define RESEARCH_COUNT(name, value)
#define RESEARCH_MAX(name, value)
#endif
