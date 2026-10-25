// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// C# adaptation of Newton v1.7.0 rigid_vbd_kernels._compliant_alm_coefficients.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;

namespace SomeEngine.Physics;

/// <summary>Finite material stiffness and its independently selected ALM penalty metric.</summary>
public readonly struct AvbdMaterialTerms
{
    private readonly float _multiplierScale;
    public float EffectiveStiffness { get; }

    public AvbdMaterialTerms(float materialStiffness, float penalty)
    {
        if (materialStiffness <= 0 || penalty <= 0)
        {
            _multiplierScale = 0;
            EffectiveStiffness = 0;
        }
        else if (materialStiffness >= penalty)
        {
            _multiplierScale = 1 / (1 + penalty / materialStiffness);
            EffectiveStiffness = penalty * _multiplierScale;
        }
        else
        {
            float ratio = materialStiffness / penalty;
            float complement = 1 / (1 + ratio);
            _multiplierScale = ratio * complement;
            EffectiveStiffness = materialStiffness * complement;
        }
    }

    public float EvaluateForce(float error, float multiplier) => _multiplierScale * multiplier + EffectiveStiffness * error;
    public Vector3 EvaluateForce(Vector3 error, Vector3 multiplier) => _multiplierScale * multiplier + EffectiveStiffness * error;
}
