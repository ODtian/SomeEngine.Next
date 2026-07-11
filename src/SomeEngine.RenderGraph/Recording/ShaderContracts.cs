namespace SomeEngine.RenderGraph;

[Flags]
internal enum ShaderEffectBits : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

internal static class ShaderContractValidator
{
    private const ShaderStage AllStages = ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute;

    public static FrozenShaderContract Freeze(
        in ShaderDesc shader,
        ReadOnlySpan<ShaderBindingAccess> mappings,
        GraphToken token,
        int pass)
    {
        if (!shader.Key.IsValid) throw new ArgumentException("Shader artifact key is invalid.", nameof(shader));
        ValidateSingleStage(shader.Stage, nameof(shader));

        ShaderBinding[] bindings = shader.Interface.Bindings.ToArray();
        PushConstantRange[] pushConstants = shader.Interface.PushConstants.ToArray();
        ValidateInterface(shader.Stage, bindings, pushConstants);
        Array.Sort(bindings, static (left, right) =>
        {
            int result = left.Group.CompareTo(right.Group);
            return result != 0 ? result : left.Binding.CompareTo(right.Binding);
        });
        Array.Sort(pushConstants, static (left, right) =>
        {
            int result = left.Offset.CompareTo(right.Offset);
            if (result != 0) return result;
            result = left.Size.CompareTo(right.Size);
            return result != 0 ? result : left.Visibility.CompareTo(right.Visibility);
        });

        FrozenShaderBindingAccess[] frozenMappings = new FrozenShaderBindingAccess[mappings.Length];
        for (int index = 0; index < mappings.Length; index++)
        {
            ShaderBindingAccess mapping = mappings[index];
            if (!mapping.IsValid || !ReferenceEquals(mapping.Owner, token) || mapping.Pass != pass)
                throw new ArgumentException("Every shader binding mapping must be created by this pass.", nameof(mappings));
            frozenMappings[index] = new FrozenShaderBindingAccess(
                mapping.Group,
                mapping.Binding,
                mapping.Element,
                mapping.Kind,
                mapping.Access,
                mapping.View);
        }

        Array.Sort(frozenMappings, static (left, right) =>
        {
            int result = left.Group.CompareTo(right.Group);
            if (result != 0) return result;
            result = left.Binding.CompareTo(right.Binding);
            if (result != 0) return result;
            return left.Element.CompareTo(right.Element);
        });

        return new FrozenShaderContract(
            shader.Key,
            shader.Stage,
            shader.Interface.LayoutHash,
            bindings,
            pushConstants,
            frozenMappings);
    }

    public static void Validate(
        FrozenShaderContract shader,
        string passName,
        FrozenAccess[] passAccesses,
        FrozenBufferView[] bufferViews,
        FrozenTextureView[] textureViews)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ValidateSingleStage(shader.Stage, nameof(shader));
        ValidateInterface(shader.Stage, shader.Bindings, shader.PushConstants);

        Dictionary<(uint Group, uint Binding), ShaderBinding> bindingBySlot = new(shader.Bindings.Length);
        int requiredMappings = 0;
        foreach (ShaderBinding binding in shader.Bindings)
        {
            if (!bindingBySlot.TryAdd((binding.Group, binding.Binding), binding))
                throw new InvalidOperationException($"Pass '{passName}' shader interface repeats binding ({binding.Group}, {binding.Binding}).");
            requiredMappings = checked(requiredMappings + checked((int)binding.Count));
        }

        if (shader.Accesses.Length != requiredMappings)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' shader requires {requiredMappings} descriptor element mappings but declares {shader.Accesses.Length}; every array element must be explicit.");
        }

        HashSet<(uint Group, uint Binding, uint Element)> mappedElements = new(shader.Accesses.Length);
        foreach (FrozenShaderBindingAccess mapping in shader.Accesses)
        {
            if (!bindingBySlot.TryGetValue((mapping.Group, mapping.Binding), out ShaderBinding binding))
            {
                throw new InvalidOperationException(
                    $"Pass '{passName}' maps descriptor ({mapping.Group}, {mapping.Binding}) which is absent from the shader interface.");
            }
            if (mapping.Element >= binding.Count)
            {
                throw new InvalidOperationException(
                    $"Pass '{passName}' maps descriptor ({mapping.Group}, {mapping.Binding}) element {mapping.Element}, outside count {binding.Count}.");
            }
            if (!mappedElements.Add((mapping.Group, mapping.Binding, mapping.Element)))
            {
                throw new InvalidOperationException(
                    $"Pass '{passName}' maps descriptor ({mapping.Group}, {mapping.Binding}) element {mapping.Element} more than once.");
            }

            ShaderEffectBits mayEffect = ResolveMayEffect(binding, passName);
            if (mapping.Kind == ShaderBindingAccessKind.ExternallyManaged)
            {
                if (mayEffect != ShaderEffectBits.Read)
                {
                    throw new InvalidOperationException(
                        $"Pass '{passName}' may mark only resolved read-only shader bindings as externally managed; ({binding.Group}, {binding.Binding}) resolves to {Format(mayEffect)}.");
                }
                continue;
            }

            if ((uint)mapping.Access >= (uint)passAccesses.Length)
                throw new InvalidOperationException($"Pass '{passName}' shader mapping references an invalid pass access ordinal.");
            FrozenAccess access = passAccesses[mapping.Access];
            if (access.View != mapping.View)
                throw new InvalidOperationException($"Pass '{passName}' shader mapping does not match the exact declared view access token.");

            switch (mapping.Kind)
            {
                case ShaderBindingAccessKind.BufferView:
                    ValidateBufferMapping(binding, mapping, access, bufferViews, passName);
                    break;
                case ShaderBindingAccessKind.TextureView:
                    ValidateTextureMapping(binding, mapping, access, textureViews, passName);
                    break;
                default:
                    throw new InvalidOperationException($"Pass '{passName}' shader mapping has an invalid resource kind.");
            }

            ShaderEffectBits graphEffect = ToBits(access.Effect);
            if ((mayEffect & ~graphEffect) != 0)
            {
                throw new InvalidOperationException(
                    $"Pass '{passName}' graph access {access.Effect} does not conservatively cover shader binding ({binding.Group}, {binding.Binding}) effect {Format(mayEffect)}.");
            }
        }

        foreach (ShaderBinding binding in shader.Bindings)
        {
            for (uint element = 0; element < binding.Count; element++)
            {
                if (!mappedElements.Contains((binding.Group, binding.Binding, element)))
                {
                    throw new InvalidOperationException(
                        $"Pass '{passName}' does not map shader binding ({binding.Group}, {binding.Binding}) element {element}.");
                }
            }
        }
    }

    private static void ValidateInterface(
        ShaderStage shaderStage,
        ReadOnlySpan<ShaderBinding> bindings,
        ReadOnlySpan<PushConstantRange> pushConstants)
    {
        HashSet<(uint Group, uint Binding)> slots = new();
        foreach (ShaderBinding binding in bindings)
        {
            if (!Enum.IsDefined(binding.Kind)) throw new ArgumentException("Shader interface contains an invalid binding kind.", nameof(bindings));
            if (binding.Count == 0) throw new ArgumentException("Shader descriptor binding count must be greater than zero.", nameof(bindings));
            ValidateVisibility(binding.Visibility, shaderStage, nameof(bindings));
            if (!Enum.IsDefined(binding.ReflectedAccess)) throw new ArgumentException("Shader interface contains invalid reflected access.", nameof(bindings));
            if (!Enum.IsDefined(binding.DeclaredEffect)) throw new ArgumentException("Shader interface contains an invalid declared effect.", nameof(bindings));
            const DeclaredOperations allDeclaredOperations =
                DeclaredOperations.Atomic |
                DeclaredOperations.Append |
                DeclaredOperations.Consume |
                DeclaredOperations.RasterOrdered |
                DeclaredOperations.Feedback;
            if ((binding.DeclaredOperations & ~allDeclaredOperations) != 0)
                throw new ArgumentException("Shader interface contains invalid declared operation flags.", nameof(bindings));
            const ReflectedOperations allReflectedOperations =
                ReflectedOperations.Atomic |
                ReflectedOperations.Append |
                ReflectedOperations.Consume |
                ReflectedOperations.RasterOrdered |
                ReflectedOperations.Feedback;
            if ((binding.ReflectedOperations & ~allReflectedOperations) != 0)
                throw new ArgumentException("Shader interface contains invalid reflected operation flags.", nameof(bindings));
            DeclaredOperations unsupportedOperations = binding.DeclaredOperations &
                (DeclaredOperations.Append |
                 DeclaredOperations.Consume |
                 DeclaredOperations.RasterOrdered |
                 DeclaredOperations.Feedback);
            if (unsupportedOperations != DeclaredOperations.None)
                throw new NotSupportedException(
                    $"Shader binding ({binding.Group}, {binding.Binding}) declares operation qualifiers {unsupportedOperations} that RenderGraph cannot safely lower yet.");
            ReflectedOperations unsupportedReflectedOperations = binding.ReflectedOperations &
                (ReflectedOperations.Append |
                 ReflectedOperations.Consume |
                 ReflectedOperations.RasterOrdered |
                 ReflectedOperations.Feedback);
            if (unsupportedReflectedOperations != ReflectedOperations.None)
                throw new NotSupportedException(
                    $"Shader binding ({binding.Group}, {binding.Binding}) reflects operation qualifiers {unsupportedReflectedOperations} that RenderGraph cannot safely lower yet.");
            bool atomic = (binding.DeclaredOperations & DeclaredOperations.Atomic) != 0 ||
                          (binding.ReflectedOperations & ReflectedOperations.Atomic) != 0;
            if (atomic &&
                (binding.Kind is not (BindingKind.StorageTexture or BindingKind.StorageBuffer) ||
                 binding.DeclaredEffect != DeclaredEffect.ReadWrite ||
                 binding.ReflectedAccess is ReflectedAccess.ReadOnly or ReflectedAccess.WriteOnly))
            {
                throw new ArgumentException(
                    "Atomic shader bindings must be storage bindings with a ReadWrite declaration and read-write-capable reflection.",
                    nameof(bindings));
            }
            if (!Enum.IsDefined(binding.TextureDimension)) throw new ArgumentException("Shader interface contains an invalid texture dimension.", nameof(bindings));
            if (!Enum.IsDefined(binding.TextureSampleType)) throw new ArgumentException("Shader interface contains an invalid texture sample type.", nameof(bindings));
            if (!Enum.IsDefined(binding.StorageFormat)) throw new ArgumentException("Shader interface contains an invalid storage texture format.", nameof(bindings));
            bool texture = binding.Kind is BindingKind.SampledTexture or BindingKind.StorageTexture;
            if (!texture &&
                (binding.TextureDimension != ShaderTextureDimension.Unknown ||
                 binding.TextureSampleType != TextureSampleType.Unknown ||
                 binding.StorageFormat != global::SomeEngine.Graphics.Format.Unknown))
            {
                throw new ArgumentException("Non-texture shader bindings cannot carry texture shape fields.", nameof(bindings));
            }
            if (binding.Kind == BindingKind.SampledTexture && binding.StorageFormat != global::SomeEngine.Graphics.Format.Unknown)
                throw new ArgumentException("A sampled-texture binding cannot declare a storage format.", nameof(bindings));
            if (!slots.Add((binding.Group, binding.Binding)))
                throw new ArgumentException($"Shader interface repeats binding ({binding.Group}, {binding.Binding}).", nameof(bindings));
            _ = ResolveMayEffect(binding, "authoring");
        }

        HashSet<(uint Register, uint Space)> pushSlots = [];
        for (int index = 0; index < pushConstants.Length; index++)
        {
            PushConstantRange range = pushConstants[index];
            if (range.Size == 0 || (range.Offset & 3) != 0 || (range.Size & 3) != 0)
                throw new ArgumentException("Push-constant ranges must be non-zero and four-byte aligned.", nameof(pushConstants));
            uint end = checked(range.Offset + range.Size);
            ValidateVisibility(range.Visibility, shaderStage, nameof(pushConstants));
            if (!pushSlots.Add((range.Register, range.Space)))
                throw new ArgumentException($"Shader interface repeats push-constant register b{range.Register}, space{range.Space}.", nameof(pushConstants));
            for (int priorIndex = 0; priorIndex < index; priorIndex++)
            {
                PushConstantRange prior = pushConstants[priorIndex];
                uint priorEnd = checked(prior.Offset + prior.Size);
                if (range.Offset < priorEnd && prior.Offset < end)
                    throw new ArgumentException("Shader push-constant byte ranges overlap.", nameof(pushConstants));
            }
        }
    }

    private static void ValidateBufferMapping(
        in ShaderBinding binding,
        in FrozenShaderBindingAccess mapping,
        in FrozenAccess access,
        FrozenBufferView[] views,
        string passName)
    {
        if (access.Kind != ResourceNodeKind.Buffer || (uint)mapping.View >= (uint)views.Length)
            throw new InvalidOperationException($"Pass '{passName}' maps a non-buffer view token to buffer shader binding ({binding.Group}, {binding.Binding}).");
        FrozenBufferView view = views[mapping.View];
        if (access.Resource != view.Resource || view.Kind != binding.Kind)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' buffer view kind {view.Kind} does not match shader binding ({binding.Group}, {binding.Binding}) kind {binding.Kind}.");
        }
        BufferUse expectedUse = binding.Kind switch
        {
            BindingKind.ConstantBuffer => BufferUse.VertexOrConstant,
            BindingKind.ReadOnlyBuffer => BufferUse.ShaderRead,
            BindingKind.StorageBuffer => BufferUse.ShaderWrite,
            _ => throw new InvalidOperationException(
                $"Pass '{passName}' shader binding ({binding.Group}, {binding.Binding}) kind {binding.Kind} cannot map a buffer view."),
        };
        if (access.BufferUse != expectedUse)
            throw new InvalidOperationException($"Pass '{passName}' buffer shader mapping does not preserve its declared view use.");
    }

    private static void ValidateTextureMapping(
        in ShaderBinding binding,
        in FrozenShaderBindingAccess mapping,
        in FrozenAccess access,
        FrozenTextureView[] views,
        string passName)
    {
        if (access.Kind != ResourceNodeKind.Texture || (uint)mapping.View >= (uint)views.Length)
            throw new InvalidOperationException($"Pass '{passName}' maps a non-texture view token to texture shader binding ({binding.Group}, {binding.Binding}).");
        FrozenTextureView view = views[mapping.View];
        if (access.Resource != view.Resource)
            throw new InvalidOperationException($"Pass '{passName}' texture shader mapping does not match the declared graph view.");

        TextureUse expectedUse;
        TextureViewUsage requiredUsage;
        switch (binding.Kind)
        {
            case BindingKind.SampledTexture:
                expectedUse = TextureUse.Sampled;
                requiredUsage = TextureViewUsage.ShaderResource;
                break;
            case BindingKind.StorageTexture:
                expectedUse = TextureUse.Storage;
                requiredUsage = TextureViewUsage.Storage;
                break;
            default:
                throw new InvalidOperationException(
                    $"Pass '{passName}' shader binding ({binding.Group}, {binding.Binding}) kind {binding.Kind} cannot map a texture view.");
        }
        if (access.TextureUse != expectedUse || (view.Usage & requiredUsage) == 0)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' texture view does not match shader binding ({binding.Group}, {binding.Binding}) kind {binding.Kind}.");
        }

        ShaderTextureDimension actualDimension = ShaderDimension(view.Dimension);
        if (binding.TextureDimension != ShaderTextureDimension.Unknown &&
            binding.TextureDimension != actualDimension)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' texture view dimension {view.Dimension} does not match shader binding ({binding.Group}, {binding.Binding}) dimension {binding.TextureDimension}.");
        }

        TextureSampleType actualSampleType = SampleType(view.Format, view.Range.Aspect);
        if (binding.TextureSampleType != TextureSampleType.Unknown &&
            !SampleTypeMatches(binding.TextureSampleType, actualSampleType))
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' texture view format/aspect {view.Format}/{view.Range.Aspect} resolves to sample type {actualSampleType}, not shader binding ({binding.Group}, {binding.Binding}) sample type {binding.TextureSampleType}.");
        }

        if (binding.Kind == BindingKind.StorageTexture &&
            binding.StorageFormat != global::SomeEngine.Graphics.Format.Unknown &&
            binding.StorageFormat != view.Format)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' storage texture view format {view.Format} does not match shader binding ({binding.Group}, {binding.Binding}) storage format {binding.StorageFormat}.");
        }
    }

    private static ShaderTextureDimension ShaderDimension(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => ShaderTextureDimension.Texture1D,
        TextureViewDimension.Texture1DArray => ShaderTextureDimension.Texture1DArray,
        TextureViewDimension.Texture2D => ShaderTextureDimension.Texture2D,
        TextureViewDimension.Texture2DArray => ShaderTextureDimension.Texture2DArray,
        TextureViewDimension.Texture2DMS => ShaderTextureDimension.Texture2DMS,
        TextureViewDimension.Texture2DMSArray => ShaderTextureDimension.Texture2DMSArray,
        TextureViewDimension.Cube => ShaderTextureDimension.Cube,
        TextureViewDimension.CubeArray => ShaderTextureDimension.CubeArray,
        TextureViewDimension.Texture3D => ShaderTextureDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static TextureSampleType SampleType(global::SomeEngine.Graphics.Format format, TextureAspect aspect) =>
        (format, aspect) switch
        {
            (global::SomeEngine.Graphics.Format.D24UNormS8UInt, TextureAspect.Stencil) => TextureSampleType.UInt,
            (global::SomeEngine.Graphics.Format.D24UNormS8UInt or global::SomeEngine.Graphics.Format.D32Float, TextureAspect.Depth) =>
                TextureSampleType.Depth,
            (global::SomeEngine.Graphics.Format.R16UInt or global::SomeEngine.Graphics.Format.R32UInt, TextureAspect.Color) =>
                TextureSampleType.UInt,
            (_, TextureAspect.Color) => TextureSampleType.Float,
            _ => TextureSampleType.Unknown,
        };

    // Slang reflects Texture2D<float> as Float even when the bound SRV exposes a depth format.
    // Depth is reserved for comparison/shadow texture declarations, so Float accepts either
    // ordinary color-float sampling or a non-comparison depth load.
    private static bool SampleTypeMatches(TextureSampleType expected, TextureSampleType actual) =>
        expected == actual || (expected == TextureSampleType.Float && actual == TextureSampleType.Depth);

    private static ShaderEffectBits ResolveMayEffect(in ShaderBinding binding, string passName)
    {
        ShaderEffectBits typeCapability = binding.Kind switch
        {
            BindingKind.ConstantBuffer or BindingKind.SampledTexture or BindingKind.ReadOnlyBuffer or BindingKind.Sampler => ShaderEffectBits.Read,
            BindingKind.StorageTexture or BindingKind.StorageBuffer => ShaderEffectBits.ReadWrite,
            _ => throw new InvalidOperationException($"Pass '{passName}' shader binding has an invalid kind."),
        };
        ShaderEffectBits reflectedCapability = binding.ReflectedAccess switch
        {
            ReflectedAccess.Unknown => typeCapability,
            ReflectedAccess.ReadOnly => ShaderEffectBits.Read,
            ReflectedAccess.WriteOnly => ShaderEffectBits.Write,
            ReflectedAccess.ReadWrite => ShaderEffectBits.ReadWrite,
            _ => throw new InvalidOperationException($"Pass '{passName}' shader binding has invalid reflected access."),
        };
        if ((reflectedCapability & ~typeCapability) != 0)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' reflected access {binding.ReflectedAccess} exceeds binding kind {binding.Kind} capability.");
        }

        if (binding.DeclaredEffect == DeclaredEffect.Unspecified) return reflectedCapability;
        ShaderEffectBits declared = binding.DeclaredEffect switch
        {
            DeclaredEffect.Read => ShaderEffectBits.Read,
            DeclaredEffect.Write => ShaderEffectBits.Write,
            DeclaredEffect.ReadWrite => ShaderEffectBits.ReadWrite,
            _ => throw new InvalidOperationException($"Pass '{passName}' shader binding has an invalid declared effect."),
        };
        if ((declared & ~reflectedCapability) != 0)
        {
            throw new InvalidOperationException(
                $"Pass '{passName}' declared effect {binding.DeclaredEffect} exceeds reflected/type capability {Format(reflectedCapability)} for binding ({binding.Group}, {binding.Binding}).");
        }
        return declared;
    }

    private static ShaderEffectBits ToBits(ResourceEffect effect) => effect switch
    {
        ResourceEffect.Read => ShaderEffectBits.Read,
        ResourceEffect.Write => ShaderEffectBits.Write,
        ResourceEffect.ReadWrite => ShaderEffectBits.ReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    private static void ValidateSingleStage(ShaderStage stage, string parameterName)
    {
        byte value = (byte)stage;
        if ((stage & ~AllStages) != 0 || value == 0 || (value & (value - 1)) != 0)
            throw new ArgumentException("A render-graph shader contract must describe exactly one shader stage.", parameterName);
    }

    private static void ValidateVisibility(ShaderStage visibility, ShaderStage shaderStage, string parameterName)
    {
        if (visibility == 0 || (visibility & ~AllStages) != 0 || (visibility & shaderStage) == 0)
            throw new ArgumentException("Shader interface visibility must be valid and include the shader's stage.", parameterName);
    }

    private static string Format(ShaderEffectBits effect) => effect switch
    {
        ShaderEffectBits.Read => "Read",
        ShaderEffectBits.Write => "Write",
        ShaderEffectBits.ReadWrite => "ReadWrite",
        _ => effect.ToString(),
    };
}
