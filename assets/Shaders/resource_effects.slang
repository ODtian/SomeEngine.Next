#ifndef SOMEENGINE_RESOURCE_EFFECTS_SLANG
#define SOMEENGINE_RESOURCE_EFFECTS_SLANG

enum ResourceEffects : uint
{
    Read = 1,
    Write = 2,
    ReadWrite = 3,
};

[Flags]
enum ResourceQualifiers : uint
{
    None = 0,
    Atomic = 1,
    Append = 2,
    Consume = 4,
    RasterOrdered = 8,
    Feedback = 16,
};

[__AttributeUsage(_AttributeTargets.Var)]
[__AttributeUsage(_AttributeTargets.Param)]
struct ResourceEffectAttribute
{
    ResourceEffects effects;
    ResourceQualifiers qualifiers;
};

#endif
