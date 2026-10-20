CONSUMER UTC=2026-10-20T01:23:34.4962378Z Length=69120
ASSEMBLY Consumer tools/SomeEngine.ECS.Relations.AotSmoke/bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.AotSmoke.dll
IL TARGET count=1 Consumer EcsTypeRegistration::RegisterTypes
BEGIN SELECTED METHOD Consumer EcsTypeRegistration::RegisterTypes
	.method assembly hidebysig static 
		void RegisterTypes () cil managed 
	{
		.custom instance void [System.Runtime]System.Runtime.CompilerServices.ModuleInitializerAttribute::.ctor() = (
			01 00 00 00
		)
		// Method begins at RVA 0x2050
		// Header size: 12
		// Code size: 129 (0x81)
		.maxstack 3

		IL_0000: ldc.i4.1
		IL_0001: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.DirectedRelationEndpoints`1<valuetype SomeEngine.Tools.RelationsSmoke.Wire`1<int32>>>(bool)
		IL_0006: ldc.i4.1
		IL_0007: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.UndirectedRelationEndpoints`1<valuetype SomeEngine.Tools.RelationsSmoke.Joint`1<int32>>>(bool)
		IL_000c: ldc.i4.1
		IL_000d: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype [SomeEngine.Physics]SomeEngine.Physics.CollisionBounds>(bool)
		IL_0012: ldc.i4.1
		IL_0013: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Body>(bool)
		IL_0018: ldc.i4.1
		IL_0019: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Hair>(bool)
		IL_001e: ldc.i4.1
		IL_001f: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Joint`1<int32>>(bool)
		IL_0024: ldc.i4.1
		IL_0025: ldc.i4.1
		IL_0026: ldc.i4.0
		IL_0027: newobj instance void [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationSchema::.ctor(valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationDirection, valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationCardinality, bool)
		IL_002c: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterRelationSchema<valuetype SomeEngine.Tools.RelationsSmoke.Joint`1<int32>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationSchema)
		IL_0031: ldc.i4.0
		IL_0032: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.NativeAddress`1<int32>>(bool)
		IL_0037: ldc.i4.1
		IL_0038: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>>(bool)
		IL_003d: ldc.i4.1
		IL_003e: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position1>(bool)
		IL_0043: ldc.i4.1
		IL_0044: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position2>(bool)
		IL_0049: ldc.i4.1
		IL_004a: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position3>(bool)
		IL_004f: ldc.i4.1
		IL_0050: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position4>(bool)
		IL_0055: ldc.i4.1
		IL_0056: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position5>(bool)
		IL_005b: ldc.i4.1
		IL_005c: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position6>(bool)
		IL_0061: ldc.i4.1
		IL_0062: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position7>(bool)
		IL_0067: ldc.i4.1
		IL_0068: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Position8>(bool)
		IL_006d: ldc.i4.1
		IL_006e: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterJobStorage<valuetype SomeEngine.Tools.RelationsSmoke.Wire`1<int32>>(bool)
		IL_0073: ldc.i4.0
		IL_0074: ldc.i4.0
		IL_0075: ldc.i4.1
		IL_0076: newobj instance void [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationSchema::.ctor(valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationDirection, valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationCardinality, bool)
		IL_007b: call void [SomeEngine.ECS]SomeEngine.ECS.Registry.GeneratedEcsTypes::RegisterRelationSchema<valuetype SomeEngine.Tools.RelationsSmoke.Wire`1<int32>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Relations.RelationSchema)
		IL_0080: ret
	} // end of method EcsTypeRegistration::RegisterTypes
END SELECTED METHOD Consumer EcsTypeRegistration::RegisterTypes
IL TARGET count=1 Consumer WeightedTotal8::Matches
BEGIN SELECTED METHOD Consumer WeightedTotal8::Matches
	.method public hidebysig 
		instance bool Matches (
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& first,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& second,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& third,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& fourth,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& fifth,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& sixth,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& seventh,
			[in] valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>& eighth
		) cil managed noinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [6]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [7]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [8]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x22b4
		// Header size: 12
		// Code size: 86 (0x56)
		.maxstack 3

		IL_0000: ldarg.1
		IL_0001: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_0006: ldc.i4.2
		IL_0007: ldarg.2
		IL_0008: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_000d: mul
		IL_000e: add
		IL_000f: ldc.i4.3
		IL_0010: ldarg.3
		IL_0011: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_0016: mul
		IL_0017: add
		IL_0018: ldc.i4.4
		IL_0019: ldarg.s fourth
		IL_001b: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_0020: mul
		IL_0021: add
		IL_0022: ldc.i4.5
		IL_0023: ldarg.s fifth
		IL_0025: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_002a: mul
		IL_002b: add
		IL_002c: ldc.i4.6
		IL_002d: ldarg.s sixth
		IL_002f: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_0034: mul
		IL_0035: add
		IL_0036: ldc.i4.7
		IL_0037: ldarg.s seventh
		IL_0039: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_003e: mul
		IL_003f: add
		IL_0040: ldc.i4.8
		IL_0041: ldarg.s eighth
		IL_0043: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value
		IL_0048: mul
		IL_0049: add
		IL_004a: ldarg.0
		IL_004b: ldfld int32 SomeEngine.Tools.RelationsSmoke.WeightedTotal8::'<minimum>P'
		IL_0050: clt
		IL_0052: ldc.i4.0
		IL_0053: ceq
		IL_0055: ret
	} // end of method WeightedTotal8::Matches
END SELECTED METHOD Consumer WeightedTotal8::Matches
IL TARGET count=1 Consumer EighthEntity::Add
BEGIN SELECTED METHOD Consumer EighthEntity::Add
	.method public hidebysig 
		instance bool Add (
			[in] valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>& row
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2368
		// Header size: 12
		// Code size: 46 (0x2e)
		.maxstack 4
		.locals init (
			[0] int32
		)

		IL_0000: ldarg.0
		IL_0001: ldflda valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity> SomeEngine.Tools.RelationsSmoke.EighthEntity::_result
		IL_0006: ldarg.0
		IL_0007: ldarg.0
		IL_0008: ldfld int32 SomeEngine.Tools.RelationsSmoke.EighthEntity::Count
		IL_000d: stloc.0
		IL_000e: ldloc.0
		IL_000f: ldc.i4.1
		IL_0010: add
		IL_0011: stfld int32 SomeEngine.Tools.RelationsSmoke.EighthEntity::Count
		IL_0016: ldloc.0
		IL_0017: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::get_Item(int32)
		IL_001c: ldarg.1
		IL_001d: ldflda !7 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Rest
		IL_0022: ldfld !0 valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item1
		IL_0027: stobj [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity
		IL_002c: ldc.i4.0
		IL_002d: ret
	} // end of method EighthEntity::Add
END SELECTED METHOD Consumer EighthEntity::Add
IL TARGET count=1 Consumer NumberPointValue::Execute
BEGIN SELECTED METHOD Consumer NumberPointValue::Execute
	.method public final hidebysig newslot virtual 
		instance void Execute (
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor query
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x23bc
		// Header size: 12
		// Code size: 39 (0x27)
		.maxstack 4
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>>
		)

		IL_0000: ldarg.0
		IL_0001: ldarga.s query
		IL_0003: ldarg.0
		IL_0004: ldfld valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity SomeEngine.Tools.RelationsSmoke.NumberPointValue::_entity
		IL_0009: ldloca.s 0
		IL_000b: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryReadRef<valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0010: brtrue.s IL_0015

		IL_0012: ldc.i4.m1
		IL_0013: br.s IL_0021

		IL_0015: ldloca.s 0
		IL_0017: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>>::get_Value()
		IL_001c: ldfld int32 valuetype SomeEngine.Tools.RelationsSmoke.Number`1<int32>::Value

		IL_0021: stfld int32 SomeEngine.Tools.RelationsSmoke.NumberPointValue::Value
		IL_0026: ret
	} // end of method NumberPointValue::Execute
END SELECTED METHOD Consumer NumberPointValue::Execute
IL TARGET count=2 Consumer ForwardedMutualPermission::Matches
BEGIN SELECTED METHOD Consumer ForwardedMutualPermission::Matches
	.method public hidebysig static 
		bool Matches (
			[in] valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits& first,
			[in] valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits& second
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x35e4
		// Header size: 1
		// Code size: 8 (0x8)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: call bool [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.MutualPermission::Matches(valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits&, valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits&)
		IL_0007: ret
	} // end of method ForwardedMutualPermission::Matches
END SELECTED METHOD Consumer ForwardedMutualPermission::Matches
BEGIN SELECTED METHOD Consumer ForwardedMutualPermission::Matches
	.method public hidebysig static 
		bool Matches<valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader> (
			[in] !!TRelation& relation,
			!!TReader& reader,
			[in] !!TComponent& first,
			[in] !!TComponent& second
		) cil managed 
	{
		.param constraint TReader, class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x35ed
		// Header size: 1
		// Code size: 10 (0xa)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: call bool [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.MutualPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0009: ret
	} // end of method ForwardedMutualPermission::Matches
END SELECTED METHOD Consumer ForwardedMutualPermission::Matches
IL TARGET count=1 Consumer FillContacts`1::Execute
BEGIN SELECTED METHOD Consumer FillContacts`1::Execute
		.method public final hidebysig newslot virtual 
			instance void Execute () cil managed 
		{
			// Method begins at RVA 0x4622
			// Header size: 1
			// Code size: 18 (0x12)
			.maxstack 8

			IL_0000: ldarg.0
			IL_0001: ldfld valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQuery`2<!0, valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>> valuetype SomeEngine.Tools.RelationsSmoke.RelationsSmoke/FillContacts`1<!TEnumerable>::'<query>P'
			IL_0006: ldarg.0
			IL_0007: ldfld class [SomeEngine.Physics]SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Tools.RelationsSmoke.RelationsSmoke/FillContacts`1<!TEnumerable>::'<pairs>P'
			IL_000c: call void [SomeEngine.Physics]SomeEngine.Physics.CollisionPairQueries::FillPairsParallel<!TEnumerable>(valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQuery`2<!!0, valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>, class [SomeEngine.Physics]SomeEngine.Physics.CollisionPairBuffer)
			IL_0011: ret
		} // end of method FillContacts`1::Execute
END SELECTED METHOD Consumer FillContacts`1::Execute
ASSEMBLY Relations artifacts/ecs-relations/cache/someengine.ecs.relations/0.1.0-relations.231/lib/net10.0/SomeEngine.ECS.Relations.dll
IL TARGET count=3 Relations ComponentRelation`9::Matches
BEGIN SELECTED METHOD Relations ComponentRelation`9::Matches
	.method public hidebysig 
		instance bool Matches (
			[in] valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>& row
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x6540
		// Header size: 12
		// Code size: 200 (0xc8)
		.maxstack 9
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[2] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[3] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[4] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[5] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[6] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[7] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow
		)

		IL_0000: ldarg.0
		IL_0001: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_0006: ldarg.1
		IL_0007: ldfld !0 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item1
		IL_000c: ldloca.s 0
		IL_000e: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_0013: brfalse IL_00b3

		IL_0018: ldarg.0
		IL_0019: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_001e: ldarg.1
		IL_001f: ldfld !1 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item2
		IL_0024: ldloca.s 1
		IL_0026: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_002b: brfalse IL_00b3

		IL_0030: ldarg.0
		IL_0031: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_0036: ldarg.1
		IL_0037: ldfld !2 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item3
		IL_003c: ldloca.s 2
		IL_003e: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_0043: brfalse.s IL_00b3

		IL_0045: ldarg.0
		IL_0046: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_004b: ldarg.1
		IL_004c: ldfld !3 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item4
		IL_0051: ldloca.s 3
		IL_0053: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_0058: brfalse.s IL_00b3

		IL_005a: ldarg.0
		IL_005b: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_0060: ldarg.1
		IL_0061: ldfld !4 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item5
		IL_0066: ldloca.s 4
		IL_0068: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_006d: brfalse.s IL_00b3

		IL_006f: ldarg.0
		IL_0070: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_0075: ldarg.1
		IL_0076: ldfld !5 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item6
		IL_007b: ldloca.s 5
		IL_007d: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_0082: brfalse.s IL_00b3

		IL_0084: ldarg.0
		IL_0085: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_008a: ldarg.1
		IL_008b: ldfld !6 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Item7
		IL_0090: ldloca.s 6
		IL_0092: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_0097: brfalse.s IL_00b3

		IL_0099: ldarg.0
		IL_009a: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_query
		IL_009f: ldarg.1
		IL_00a0: ldflda !7 valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Rest
		IL_00a5: ldfld !0 valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item1
		IL_00aa: ldloca.s 7
		IL_00ac: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
		IL_00b1: brtrue.s IL_00b5

		IL_00b3: ldc.i4.0
		IL_00b4: ret

		IL_00b5: ldarg.0
		IL_00b6: ldloc.0
		IL_00b7: ldloc.1
		IL_00b8: ldloc.2
		IL_00b9: ldloc.3
		IL_00ba: ldloc.s 4
		IL_00bc: ldloc.s 5
		IL_00be: ldloc.s 6
		IL_00c0: ldloc.s 7
		IL_00c2: call instance bool valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::Matches(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow)
		IL_00c7: ret
	} // end of method ComponentRelation`9::Matches
END SELECTED METHOD Relations ComponentRelation`9::Matches
BEGIN SELECTED METHOD Relations ComponentRelation`9::Matches
	.method public hidebysig 
		instance bool Matches (
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow first,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow second,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow third,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow fourth,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow fifth,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow sixth,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow seventh,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow eighth
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [6]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [7]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [8]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x6614
		// Header size: 12
		// Code size: 164 (0xa4)
		.maxstack 9
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T1>,
			[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T2>,
			[2] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T3>,
			[3] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T4>,
			[4] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T5>,
			[5] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T6>,
			[6] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T7>,
			[7] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T8>
		)

		IL_0000: ldarga.s first
		IL_0002: ldloca.s 0
		IL_0004: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T1>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0009: brfalse.s IL_0058

		IL_000b: ldarga.s second
		IL_000d: ldloca.s 1
		IL_000f: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T2>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0014: brfalse.s IL_0058

		IL_0016: ldarga.s third
		IL_0018: ldloca.s 2
		IL_001a: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T3>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_001f: brfalse.s IL_0058

		IL_0021: ldarga.s fourth
		IL_0023: ldloca.s 3
		IL_0025: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T4>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_002a: brfalse.s IL_0058

		IL_002c: ldarga.s fifth
		IL_002e: ldloca.s 4
		IL_0030: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T5>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0035: brfalse.s IL_0058

		IL_0037: ldarga.s sixth
		IL_0039: ldloca.s 5
		IL_003b: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T6>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0040: brfalse.s IL_0058

		IL_0042: ldarga.s seventh
		IL_0044: ldloca.s 6
		IL_0046: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T7>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_004b: brfalse.s IL_0058

		IL_004d: ldarga.s eighth
		IL_004f: ldloca.s 7
		IL_0051: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow::TryReadRef<!T8>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0056: brtrue.s IL_005a

		IL_0058: ldc.i4.0
		IL_0059: ret

		IL_005a: ldarg.0
		IL_005b: ldflda !0 valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::_relation
		IL_0060: ldloca.s 0
		IL_0062: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T1>::get_Value()
		IL_0067: ldloca.s 1
		IL_0069: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T2>::get_Value()
		IL_006e: ldloca.s 2
		IL_0070: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T3>::get_Value()
		IL_0075: ldloca.s 3
		IL_0077: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T4>::get_Value()
		IL_007c: ldloca.s 4
		IL_007e: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T5>::get_Value()
		IL_0083: ldloca.s 5
		IL_0085: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T6>::get_Value()
		IL_008a: ldloca.s 6
		IL_008c: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T7>::get_Value()
		IL_0091: ldloca.s 7
		IL_0093: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!T8>::get_Value()
		IL_0098: constrained. !TRelation
		IL_009e: callvirt instance bool class SomeEngine.ECS.Relations.IRelation`8<!T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::Matches(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !2& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !3& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !4& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !5& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !6& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !7& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_00a3: ret
	} // end of method ComponentRelation`9::Matches
END SELECTED METHOD Relations ComponentRelation`9::Matches
BEGIN SELECTED METHOD Relations ComponentRelation`9::Matches
	.method public hidebysig 
		instance bool Matches (
			[in] valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>& 'value',
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow firstRow,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow secondRow
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x66c4
		// Header size: 1
		// Code size: 8 (0x8)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: call instance bool valuetype SomeEngine.ECS.Relations.ComponentRelation`9<!TRelation, !T1, !T2, !T3, !T4, !T5, !T6, !T7, !T8>::Matches(valuetype [System.Runtime]System.ValueTuple`8<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [System.Runtime]System.ValueTuple`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>&)
		IL_0007: ret
	} // end of method ComponentRelation`9::Matches
END SELECTED METHOD Relations ComponentRelation`9::Matches
IL TARGET count=2 Relations BitmapRelation`4::Matches
BEGIN SELECTED METHOD Relations BitmapRelation`4::Matches
	.method public hidebysig 
		instance bool Matches (
			[in] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>& pair
		) cil managed 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2298
		// Header size: 12
		// Code size: 82 (0x52)
		.maxstack 4
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>,
			[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>
		)

		IL_0000: ldarg.0
		IL_0001: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_query
		IL_0006: ldarg.1
		IL_0007: ldfld !0 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item1
		IL_000c: ldloca.s 0
		IL_000e: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryReadRef<!TComponent>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0013: brfalse.s IL_002a

		IL_0015: ldarg.0
		IL_0016: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_query
		IL_001b: ldarg.1
		IL_001c: ldfld !1 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item2
		IL_0021: ldloca.s 1
		IL_0023: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryReadRef<!TComponent>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0028: brtrue.s IL_002c

		IL_002a: ldc.i4.0
		IL_002b: ret

		IL_002c: ldarg.0
		IL_002d: ldflda !0 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_relation
		IL_0032: ldarg.0
		IL_0033: ldflda !1 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_implementation
		IL_0038: ldloca.s 0
		IL_003a: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_003f: ldloca.s 1
		IL_0041: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_0046: constrained. !TPermission
		IL_004c: call bool SomeEngine.ECS.Relations.ICategoryPermission::Matches<!TRelation, !TComponent, !TImplementation>(!!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!2&, !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_0051: ret
	} // end of method BitmapRelation`4::Matches
END SELECTED METHOD Relations BitmapRelation`4::Matches
BEGIN SELECTED METHOD Relations BitmapRelation`4::Matches
	.method public hidebysig 
		instance bool Matches (
			[in] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>& pair,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow first,
			valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow second
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x22f8
		// Header size: 12
		// Code size: 55 (0x37)
		.maxstack 4
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>,
			[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>
		)

		IL_0000: ldarga.s first
		IL_0002: ldarga.s second
		IL_0004: ldloca.s 0
		IL_0006: ldloca.s 1
		IL_0008: call bool valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::TryReadComponents(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!2>&, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!2>&)
		IL_000d: brtrue.s IL_0011

		IL_000f: ldc.i4.0
		IL_0010: ret

		IL_0011: ldarg.0
		IL_0012: ldflda !0 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_relation
		IL_0017: ldarg.0
		IL_0018: ldflda !1 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_implementation
		IL_001d: ldloca.s 0
		IL_001f: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_0024: ldloca.s 1
		IL_0026: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_002b: constrained. !TPermission
		IL_0031: call bool SomeEngine.ECS.Relations.ICategoryPermission::Matches<!TRelation, !TComponent, !TImplementation>(!!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!2&, !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_0036: ret
	} // end of method BitmapRelation`4::Matches
END SELECTED METHOD Relations BitmapRelation`4::Matches
IL TARGET count=2 Relations BitmapRelation`4::Evaluate
BEGIN SELECTED METHOD Relations BitmapRelation`4::Evaluate
	.method assembly hidebysig 
		instance bool Evaluate<TOutput, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>, !!TOutput>, [System.Runtime]System.ValueType) TResult> (
			[in] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>& pair,
			!!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param type TOutput
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param constraint TResult, class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>, !!TOutput>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 01 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x233c
		// Header size: 12
		// Code size: 93 (0x5d)
		.maxstack 5
		.locals init (
			[0] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>,
			[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>
		)

		IL_0000: ldarg.0
		IL_0001: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_query
		IL_0006: ldarg.1
		IL_0007: ldfld !0 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item1
		IL_000c: ldloca.s 0
		IL_000e: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryReadRef<!TComponent>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0013: brtrue.s IL_0023

		IL_0015: ldarg.1
		IL_0016: ldarg.2
		IL_0017: constrained. !!TResult
		IL_001d: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0022: ret

		IL_0023: ldarg.0
		IL_0024: ldflda valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_query
		IL_0029: ldarg.1
		IL_002a: ldfld !1 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item2
		IL_002f: ldloca.s 1
		IL_0031: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryReadRef<!TComponent>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!!0>&)
		IL_0036: brtrue.s IL_0046

		IL_0038: ldarg.1
		IL_0039: ldarg.2
		IL_003a: constrained. !!TResult
		IL_0040: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0045: ret

		IL_0046: ldarg.0
		IL_0047: ldarg.1
		IL_0048: ldloca.s 0
		IL_004a: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_004f: ldloca.s 1
		IL_0051: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.ComponentReference`1<!TComponent>::get_Value()
		IL_0056: ldarg.2
		IL_0057: call instance bool valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::Evaluate<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>, !!TOutput, !!TResult>(!!0&, !2&, !2&, !!1&)
		IL_005c: ret
	} // end of method BitmapRelation`4::Evaluate
END SELECTED METHOD Relations BitmapRelation`4::Evaluate
BEGIN SELECTED METHOD Relations BitmapRelation`4::Evaluate
	.method assembly hidebysig 
		instance bool Evaluate<TItem, TOutput, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>, [System.Runtime]System.ValueType) TResult> (
			[in] !!TItem& item,
			[in] !TComponent& first,
			[in] !TComponent& second,
			!!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param type TItem
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param type TOutput
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param constraint TResult, class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x23c0
		// Header size: 1
		// Code size: 29 (0x1d)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldflda !0 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_relation
		IL_0006: ldarg.0
		IL_0007: ldflda !1 valuetype SomeEngine.ECS.Relations.BitmapRelation`4<!TRelation, !TImplementation, !TComponent, !TPermission>::_implementation
		IL_000c: ldarg.2
		IL_000d: ldarg.3
		IL_000e: ldarg.1
		IL_000f: ldarg.s output
		IL_0011: constrained. !TPermission
		IL_0017: call bool SomeEngine.ECS.Relations.ICategoryPermission::Evaluate<!TPermission, !TRelation, !TComponent, !TImplementation, !!TItem, !!TOutput, !!TResult>(!!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!3&, !!2& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!2& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!4& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!5&)
		IL_001c: ret
	} // end of method BitmapRelation`4::Evaluate
END SELECTED METHOD Relations BitmapRelation`4::Evaluate
IL TARGET count=1 Relations ICategoryPermission::Evaluate
BEGIN SELECTED METHOD Relations ICategoryPermission::Evaluate
	.method public hidebysig virtual static 
		bool Evaluate<valuetype .ctor (SomeEngine.ECS.Relations.ICategoryPermission, [System.Runtime]System.ValueType) TPermission, valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader, TItem, TOutput, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>, [System.Runtime]System.ValueType) TResult> (
			[in] !!TRelation& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) relation,
			!!TReader& reader,
			[in] !!TComponent& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) first,
			[in] !!TComponent& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) second,
			[in] !!TItem& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) item,
			!!TOutput& output
		) cil managed 
	{
		.param constraint TPermission, SomeEngine.ECS.Relations.ICategoryPermission
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param constraint TReader, class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param type TItem
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param type TOutput
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param constraint TResult, class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [6]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x2e39
		// Header size: 1
		// Code size: 49 (0x31)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: constrained. !!TPermission
		IL_000a: call bool SomeEngine.ECS.Relations.ICategoryPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!2&, !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !!1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_000f: brtrue.s IL_0021

		IL_0011: ldarg.s item
		IL_0013: ldarg.s output
		IL_0015: constrained. !!TResult
		IL_001b: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0020: ret

		IL_0021: ldarg.s item
		IL_0023: ldarg.s output
		IL_0025: constrained. !!TResult
		IL_002b: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Matched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0030: ret
	} // end of method ICategoryPermission::Evaluate
END SELECTED METHOD Relations ICategoryPermission::Evaluate
IL TARGET count=2 Relations OneWayPermission::Matches
BEGIN SELECTED METHOD Relations OneWayPermission::Matches
	.method public hidebysig static 
		bool Matches (
			[in] valuetype SomeEngine.ECS.Relations.CategoryBits& first,
			[in] valuetype SomeEngine.ECS.Relations.CategoryBits& second
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2e6b
		// Header size: 1
		// Code size: 18 (0x12)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::AllowedCategories
		IL_0006: ldarg.1
		IL_0007: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::Categories
		IL_000c: call bool SomeEngine.ECS.Relations.CategoryBits::Allows(uint64, uint64)
		IL_0011: ret
	} // end of method OneWayPermission::Matches
END SELECTED METHOD Relations OneWayPermission::Matches
BEGIN SELECTED METHOD Relations OneWayPermission::Matches
	.method public hidebysig static 
		bool Matches<valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader> (
			[in] !!TRelation& relation,
			!!TReader& reader,
			[in] !!TComponent& first,
			[in] !!TComponent& second
		) cil managed aggressiveinlining 
	{
		.param constraint TReader, class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2e7e
		// Header size: 1
		// Code size: 34 (0x22)
		.maxstack 8

		IL_0000: ldarg.1
		IL_0001: ldarg.0
		IL_0002: ldarg.2
		IL_0003: constrained. !!TReader
		IL_0009: callvirt instance uint64 class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>::ReadAllowedCategories(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_000e: ldarg.1
		IL_000f: ldarg.0
		IL_0010: ldarg.3
		IL_0011: constrained. !!TReader
		IL_0017: callvirt instance uint64 class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>::ReadCategories(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_001c: call bool SomeEngine.ECS.Relations.CategoryBits::Allows(uint64, uint64)
		IL_0021: ret
	} // end of method OneWayPermission::Matches
END SELECTED METHOD Relations OneWayPermission::Matches
IL TARGET count=1 Relations OneWayPermission::Evaluate
BEGIN SELECTED METHOD Relations OneWayPermission::Evaluate
	.method public hidebysig static 
		bool Evaluate<valuetype .ctor (SomeEngine.ECS.Relations.ICategoryPermission, [System.Runtime]System.ValueType) TPermission, valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader, TItem, TOutput, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>, [System.Runtime]System.ValueType) TResult> (
			[in] !!TRelation& relation,
			!!TReader& reader,
			[in] !!TComponent& first,
			[in] !!TComponent& second,
			[in] !!TItem& item,
			!!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param constraint TPermission, SomeEngine.ECS.Relations.ICategoryPermission
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param constraint TReader, class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param type TItem
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param type TOutput
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param constraint TResult, class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [6]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x2ea1
		// Header size: 1
		// Code size: 43 (0x2b)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0009: brtrue.s IL_001b

		IL_000b: ldarg.s item
		IL_000d: ldarg.s output
		IL_000f: constrained. !!TResult
		IL_0015: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_001a: ret

		IL_001b: ldarg.s item
		IL_001d: ldarg.s output
		IL_001f: constrained. !!TResult
		IL_0025: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Matched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_002a: ret
	} // end of method OneWayPermission::Evaluate
END SELECTED METHOD Relations OneWayPermission::Evaluate
IL TARGET count=2 Relations MutualPermission::Matches
BEGIN SELECTED METHOD Relations MutualPermission::Matches
	.method public hidebysig static 
		bool Matches (
			[in] valuetype SomeEngine.ECS.Relations.CategoryBits& first,
			[in] valuetype SomeEngine.ECS.Relations.CategoryBits& second
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2ef0
		// Header size: 1
		// Code size: 19 (0x13)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches(valuetype SomeEngine.ECS.Relations.CategoryBits&, valuetype SomeEngine.ECS.Relations.CategoryBits&)
		IL_0007: brfalse.s IL_0011

		IL_0009: ldarg.1
		IL_000a: ldarg.0
		IL_000b: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches(valuetype SomeEngine.ECS.Relations.CategoryBits&, valuetype SomeEngine.ECS.Relations.CategoryBits&)
		IL_0010: ret

		IL_0011: ldc.i4.0
		IL_0012: ret
	} // end of method MutualPermission::Matches
END SELECTED METHOD Relations MutualPermission::Matches
BEGIN SELECTED METHOD Relations MutualPermission::Matches
	.method public hidebysig static 
		bool Matches<valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader> (
			[in] !!TRelation& relation,
			!!TReader& reader,
			[in] !!TComponent& first,
			[in] !!TComponent& second
		) cil managed aggressiveinlining 
	{
		.param constraint TReader, class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2f04
		// Header size: 1
		// Code size: 23 (0x17)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0009: brfalse.s IL_0015

		IL_000b: ldarg.0
		IL_000c: ldarg.1
		IL_000d: ldarg.3
		IL_000e: ldarg.2
		IL_000f: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0014: ret

		IL_0015: ldc.i4.0
		IL_0016: ret
	} // end of method MutualPermission::Matches
END SELECTED METHOD Relations MutualPermission::Matches
IL TARGET count=1 Relations MutualPermission::Evaluate
BEGIN SELECTED METHOD Relations MutualPermission::Evaluate
	.method public hidebysig static 
		bool Evaluate<valuetype .ctor (SomeEngine.ECS.Relations.ICategoryPermission, [System.Runtime]System.ValueType) TPermission, valuetype .ctor ([System.Runtime]System.ValueType) TRelation, valuetype .ctor ([System.Runtime]System.ValueType) TComponent, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>, [System.Runtime]System.ValueType) TReader, TItem, TOutput, valuetype .ctor (class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>, [System.Runtime]System.ValueType) TResult> (
			[in] !!TRelation& relation,
			!!TReader& reader,
			[in] !!TComponent& first,
			[in] !!TComponent& second,
			[in] !!TItem& item,
			!!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param constraint TPermission, SomeEngine.ECS.Relations.ICategoryPermission
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param constraint TReader, class SomeEngine.ECS.Relations.ICategoryBitsReader`2<!!TRelation, !!TComponent>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 03 00 00 00 01 00 00 00 00
			)
		.param type TItem
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param type TOutput
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 02 00 00
			)
		.param constraint TResult, class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [4]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [6]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x2f1c
		// Header size: 12
		// Code size: 70 (0x46)
		.maxstack 4

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0009: brtrue.s IL_001b

		IL_000b: ldarg.s item
		IL_000d: ldarg.s output
		IL_000f: constrained. !!TResult
		IL_0015: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_001a: ret

		IL_001b: ldarg.0
		IL_001c: ldarg.1
		IL_001d: ldarg.3
		IL_001e: ldarg.2
		IL_001f: call bool SomeEngine.ECS.Relations.OneWayPermission::Matches<!!TRelation, !!TComponent, !!TReader>(!!0&, !!2&, !!1&, !!1&)
		IL_0024: brtrue.s IL_0036

		IL_0026: ldarg.s item
		IL_0028: ldarg.s output
		IL_002a: constrained. !!TResult
		IL_0030: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Unmatched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0035: ret

		IL_0036: ldarg.s item
		IL_0038: ldarg.s output
		IL_003a: constrained. !!TResult
		IL_0040: call bool class SomeEngine.ECS.Relations.ICategoryPermissionOutput`2<!!TItem, !!TOutput>::Matched(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !1&)
		IL_0045: ret
	} // end of method MutualPermission::Evaluate
END SELECTED METHOD Relations MutualPermission::Evaluate
IL TARGET count=1 Relations IdentityPermissionOutput`2::Matched
BEGIN SELECTED METHOD Relations IdentityPermissionOutput`2::Matched
	.method public hidebysig static 
		bool Matched (
			[in] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>& pair,
			!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2233
		// Header size: 1
		// Code size: 29 (0x1d)
		.maxstack 8

		IL_0000: constrained. !TSelection
		IL_0006: call bool SomeEngine.ECS.Relations.IRelationSelection::get_RequiresMatch()
		IL_000b: brfalse.s IL_001b

		IL_000d: ldarg.1
		IL_000e: ldarg.0
		IL_000f: constrained. !TOutput
		IL_0015: callvirt instance bool class SomeEngine.ECS.Relations.IRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Add(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_001a: ret

		IL_001b: ldc.i4.1
		IL_001c: ret
	} // end of method IdentityPermissionOutput`2::Matched
END SELECTED METHOD Relations IdentityPermissionOutput`2::Matched
IL TARGET count=1 Relations IdentityPermissionOutput`2::Unmatched
BEGIN SELECTED METHOD Relations IdentityPermissionOutput`2::Unmatched
	.method public hidebysig static 
		bool Unmatched (
			[in] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>& pair,
			!TOutput& output
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x2251
		// Header size: 1
		// Code size: 29 (0x1d)
		.maxstack 8

		IL_0000: constrained. !TSelection
		IL_0006: call bool SomeEngine.ECS.Relations.IRelationSelection::get_RequiresMatch()
		IL_000b: brtrue.s IL_001b

		IL_000d: ldarg.1
		IL_000e: ldarg.0
		IL_000f: constrained. !TOutput
		IL_0015: callvirt instance bool class SomeEngine.ECS.Relations.IRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Add(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_001a: ret

		IL_001b: ldc.i4.1
		IL_001c: ret
	} // end of method IdentityPermissionOutput`2::Unmatched
END SELECTED METHOD Relations IdentityPermissionOutput`2::Unmatched
ASSEMBLY ECS artifacts/ecs-relations/cache/someengine.ecs/0.1.0-relations.231/lib/net10.0/SomeEngine.ECS.dll
IL TARGET count=1 ECS GeneratedEcsTypes::RegisterJobStorage
BEGIN SELECTED METHOD ECS GeneratedEcsTypes::RegisterJobStorage
	.method public hidebysig static 
		void RegisterJobStorage<valuetype .ctor ([System.Runtime]System.ValueType) T> (
			bool isAliasFree
		) cil managed 
	{
		// Method begins at RVA 0x13814
		// Header size: 1
		// Code size: 7 (0x7)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: call void class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!!T>::Register(bool)
		IL_0006: ret
	} // end of method GeneratedEcsTypes::RegisterJobStorage
END SELECTED METHOD ECS GeneratedEcsTypes::RegisterJobStorage
IL TARGET count=1 ECS GeneratedEcsTypes::RegisterRelationSchema
BEGIN SELECTED METHOD ECS GeneratedEcsTypes::RegisterRelationSchema
	.method public hidebysig static 
		void RegisterRelationSchema<valuetype .ctor (SomeEngine.ECS.IComponent, [System.Runtime]System.ValueType) T> (
			valuetype SomeEngine.ECS.Relations.RelationSchema schema
		) cil managed 
	{
		.param constraint T, SomeEngine.ECS.IComponent
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0x1381c
		// Header size: 1
		// Code size: 7 (0x7)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: call void class SomeEngine.ECS.Relations.RelationSchemaCache`1<!!T>::Register(valuetype SomeEngine.ECS.Relations.RelationSchema)
		IL_0006: ret
	} // end of method GeneratedEcsTypes::RegisterRelationSchema
END SELECTED METHOD ECS GeneratedEcsTypes::RegisterRelationSchema
IL TARGET count=1 ECS JobStorageTypeMetadata`1::Register
BEGIN SELECTED METHOD ECS JobStorageTypeMetadata`1::Register
	.method assembly hidebysig static 
		void Register (
			bool isAliasFree
		) cil managed 
	{
		// Method begins at RVA 0x13874
		// Header size: 1
		// Code size: 18 (0x12)
		.maxstack 8

		IL_0000: ldsflda int32 class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!T>::s_aliasFree
		IL_0005: ldarg.0
		IL_0006: brtrue.s IL_000b

		IL_0008: ldc.i4.m1
		IL_0009: br.s IL_000c

		IL_000b: ldc.i4.1

		IL_000c: call void [System.Threading]System.Threading.Volatile::Write(int32&, int32)
		IL_0011: ret
	} // end of method JobStorageTypeMetadata`1::Register
END SELECTED METHOD ECS JobStorageTypeMetadata`1::Register
IL TARGET count=1 ECS JobStorageTypeMetadata`1::get_IsAliasFree
BEGIN SELECTED METHOD ECS JobStorageTypeMetadata`1::get_IsAliasFree
	.method assembly hidebysig specialname static 
		bool get_IsAliasFree () cil managed 
	{
		// Method begins at RVA 0x13824
		// Header size: 12
		// Code size: 61 (0x3d)
		.maxstack 3
		.locals init (
			[0] int32
		)

		IL_0000: ldsflda int32 class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!T>::s_aliasFree
		IL_0005: call int32 [System.Threading]System.Threading.Volatile::Read(int32&)
		IL_000a: stloc.0
		IL_000b: ldloc.0
		IL_000c: brtrue.s IL_0038

		IL_000e: call bool [System.Runtime]System.Runtime.CompilerServices.RuntimeHelpers::IsReferenceOrContainsReferences<!T>()
		IL_0013: brtrue.s IL_0026

		IL_0015: ldtoken !T
		IL_001a: call class [System.Runtime]System.Type [System.Runtime]System.Type::GetTypeFromHandle(valuetype [System.Runtime]System.RuntimeTypeHandle)
		IL_001f: call bool SomeEngine.ECS.Registry.JobStorageTypeShape::IsAliasFree(class [System.Runtime]System.Type)
		IL_0024: brtrue.s IL_0029

		IL_0026: ldc.i4.m1
		IL_0027: br.s IL_002a

		IL_0029: ldc.i4.1

		IL_002a: stloc.0
		IL_002b: ldsflda int32 class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!T>::s_aliasFree
		IL_0030: ldloc.0
		IL_0031: ldc.i4.0
		IL_0032: call int32 [System.Threading]System.Threading.Interlocked::CompareExchange(int32&, int32, int32)
		IL_0037: pop

		IL_0038: ldloc.0
		IL_0039: ldc.i4.0
		IL_003a: cgt
		IL_003c: ret
	} // end of method JobStorageTypeMetadata`1::get_IsAliasFree
END SELECTED METHOD ECS JobStorageTypeMetadata`1::get_IsAliasFree
IL TARGET count=1 ECS JobStorageTypeMetadata`1::GetIsAliasFree
BEGIN SELECTED METHOD ECS JobStorageTypeMetadata`1::GetIsAliasFree
	.method assembly hidebysig static 
		bool GetIsAliasFree () cil managed 
	{
		// Method begins at RVA 0x1386d
		// Header size: 1
		// Code size: 6 (0x6)
		.maxstack 8

		IL_0000: call bool class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!T>::get_IsAliasFree()
		IL_0005: ret
	} // end of method JobStorageTypeMetadata`1::GetIsAliasFree
END SELECTED METHOD ECS JobStorageTypeMetadata`1::GetIsAliasFree
IL TARGET count=1 ECS ComponentMetadata`1::CreateOperations
BEGIN SELECTED METHOD ECS ComponentMetadata`1::CreateOperations
	.method private hidebysig static 
		valuetype SomeEngine.ECS.Registry.ComponentOperations CreateOperations () cil managed 
	{
		// Method begins at RVA 0x13558
		// Header size: 12
		// Code size: 75 (0x4b)
		.maxstack 2
		.locals init (
			[0] valuetype SomeEngine.ECS.Registry.ComponentOperations
		)

		IL_0000: ldloca.s 0
		IL_0002: initobj SomeEngine.ECS.Registry.ComponentOperations
		IL_0008: ldloca.s 0
		IL_000a: ldftn bool class SomeEngine.ECS.Registry.JobStorageTypeMetadata`1<!T>::GetIsAliasFree()
		IL_0010: stfld method bool *() SomeEngine.ECS.Registry.ComponentOperations::GetJobAliasFree
		IL_0015: ldloca.s 0
		IL_0017: ldftn uint8& class SomeEngine.ECS.Registry.ComponentMetadata`1<!T>::DoGetReference(object, int32)
		IL_001d: stfld method uint8& *(object, int32) SomeEngine.ECS.Registry.ComponentOperations::GetReference
		IL_0022: ldloca.s 0
		IL_0024: ldftn void class SomeEngine.ECS.Registry.ComponentMetadata`1<!T>::DoCopy(uint8&, uint8&)
		IL_002a: stfld method void *(uint8&, uint8&) SomeEngine.ECS.Registry.ComponentOperations::CopyValue
		IL_002f: ldloca.s 0
		IL_0031: ldftn void class SomeEngine.ECS.Registry.ComponentMetadata`1<!T>::DoSwapRemove(object, int32, int32)
		IL_0037: stfld method void *(object, int32, int32) SomeEngine.ECS.Registry.ComponentOperations::SwapRemove
		IL_003c: ldloca.s 0
		IL_003e: ldftn object class SomeEngine.ECS.Registry.ComponentMetadata`1<!T>::DoCreateArray(int32)
		IL_0044: stfld method object *(int32) SomeEngine.ECS.Registry.ComponentOperations::CreateArray
		IL_0049: ldloc.0
		IL_004a: ret
	} // end of method ComponentMetadata`1::CreateOperations
END SELECTED METHOD ECS ComponentMetadata`1::CreateOperations
IL TARGET count=1 ECS ComponentInfo::get_IsJobAliasFree
BEGIN SELECTED METHOD ECS ComponentInfo::get_IsJobAliasFree
	.method assembly hidebysig specialname 
		instance bool get_IsJobAliasFree () cil managed 
	{
		// Method begins at RVA 0x1312e
		// Header size: 1
		// Code size: 17 (0x11)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldflda valuetype SomeEngine.ECS.Registry.ComponentOperations SomeEngine.ECS.Registry.ComponentInfo::Operations
		IL_0006: ldfld method bool *() SomeEngine.ECS.Registry.ComponentOperations::GetJobAliasFree
		IL_000b: calli bool()
		IL_0010: ret
	} // end of method ComponentInfo::get_IsJobAliasFree
END SELECTED METHOD ECS ComponentInfo::get_IsJobAliasFree
IL TARGET count=1 ECS RelationSchema::For
BEGIN SELECTED METHOD ECS RelationSchema::For
	.method public hidebysig static 
		valuetype SomeEngine.ECS.Relations.RelationSchema For<valuetype .ctor (SomeEngine.ECS.IComponent, [System.Runtime]System.ValueType) T> () cil managed 
	{
		.param constraint T, SomeEngine.ECS.IComponent
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		// Method begins at RVA 0xe6bc
		// Header size: 1
		// Code size: 6 (0x6)
		.maxstack 8

		IL_0000: call valuetype SomeEngine.ECS.Relations.RelationSchema class SomeEngine.ECS.Relations.RelationSchemaCache`1<!!T>::get_Value()
		IL_0005: ret
	} // end of method RelationSchema::For
END SELECTED METHOD ECS RelationSchema::For
IL TARGET count=1 ECS RelationSchemaCache`1::Register
BEGIN SELECTED METHOD ECS RelationSchemaCache`1::Register
	.method assembly hidebysig static 
		void Register (
			valuetype SomeEngine.ECS.Relations.RelationSchema schema
		) cil managed 
	{
		// Method begins at RVA 0xe9d3
		// Header size: 1
		// Code size: 34 (0x22)
		.maxstack 8

		IL_0000: newobj instance void class SomeEngine.ECS.Relations.RelationSchemaCache`1/'<>c__DisplayClass3_0'<!T>::.ctor()
		IL_0005: dup
		IL_0006: ldarg.0
		IL_0007: stfld valuetype SomeEngine.ECS.Relations.RelationSchema class SomeEngine.ECS.Relations.RelationSchemaCache`1/'<>c__DisplayClass3_0'<!T>::schema
		IL_000c: ldftn instance valuetype SomeEngine.ECS.Relations.RelationSchema class SomeEngine.ECS.Relations.RelationSchemaCache`1/'<>c__DisplayClass3_0'<!T>::'<Register>b__0'()
		IL_0012: newobj instance void class [System.Runtime]System.Func`1<valuetype SomeEngine.ECS.Relations.RelationSchema>::.ctor(object, native int)
		IL_0017: newobj instance void class [System.Runtime]System.Lazy`1<valuetype SomeEngine.ECS.Relations.RelationSchema>::.ctor(class [System.Runtime]System.Func`1<!0>)
		IL_001c: stsfld class [System.Runtime]System.Lazy`1<valuetype SomeEngine.ECS.Relations.RelationSchema> class SomeEngine.ECS.Relations.RelationSchemaCache`1<!T>::s_value
		IL_0021: ret
	} // end of method RelationSchemaCache`1::Register
END SELECTED METHOD ECS RelationSchemaCache`1::Register
IL TARGET count=1 ECS RelationSchemaCache`1::get_Value
BEGIN SELECTED METHOD ECS RelationSchemaCache`1::get_Value
	.method assembly hidebysig specialname static 
		valuetype SomeEngine.ECS.Relations.RelationSchema get_Value () cil managed 
	{
		// Method begins at RVA 0xe9c7
		// Header size: 1
		// Code size: 11 (0xb)
		.maxstack 8

		IL_0000: ldsfld class [System.Runtime]System.Lazy`1<valuetype SomeEngine.ECS.Relations.RelationSchema> class SomeEngine.ECS.Relations.RelationSchemaCache`1<!T>::s_value
		IL_0005: callvirt instance !0 class [System.Runtime]System.Lazy`1<valuetype SomeEngine.ECS.Relations.RelationSchema>::get_Value()
		IL_000a: ret
	} // end of method RelationSchemaCache`1::get_Value
END SELECTED METHOD ECS RelationSchemaCache`1::get_Value
IL TARGET count=1 ECS QueryCursor::TryReadRef
BEGIN SELECTED METHOD ECS QueryCursor::TryReadRef
	.method public hidebysig 
		instance bool TryReadRef<valuetype .ctor ([System.Runtime]System.ValueType) T> (
			valuetype SomeEngine.ECS.Entities.Entity entity,
			[out] valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>& component
		) cil managed aggressiveinlining 
	{
		// Method begins at RVA 0x14eee
		// Header size: 1
		// Code size: 25 (0x19)
		.maxstack 8

		IL_0000: ldarg.2
		IL_0001: ldarg.0
		IL_0002: ldfld valuetype SomeEngine.ECS.Queries.QueryData& SomeEngine.ECS.Queries.QueryCursor::_data
		IL_0007: ldarg.1
		IL_0008: call valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!0> SomeEngine.ECS.World::ReadEntityQueryRef<!!T>(valuetype SomeEngine.ECS.Queries.QueryData&, valuetype SomeEngine.ECS.Entities.Entity)
		IL_000d: stobj valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>
		IL_0012: ldarg.2
		IL_0013: call instance bool valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>::get_HasValue()
		IL_0018: ret
	} // end of method QueryCursor::TryReadRef
END SELECTED METHOD ECS QueryCursor::TryReadRef
IL TARGET count=1 ECS World::ReadEntityQueryRef
BEGIN SELECTED METHOD ECS World::ReadEntityQueryRef
	.method assembly hidebysig static 
		valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T> ReadEntityQueryRef<valuetype .ctor ([System.Runtime]System.ValueType) T> (
			[in] valuetype SomeEngine.ECS.Queries.QueryData& data,
			valuetype SomeEngine.ECS.Entities.Entity entity
		) cil managed noinlining 
	{
		.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableContextAttribute::.ctor(uint8) = (
			01 00 00 00 00
		)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x58d4
		// Header size: 12
		// Code size: 83 (0x53)
		.maxstack 6
		.locals init (
			[0] valuetype SomeEngine.ECS.Entities.EntityRecord,
			[1] class SomeEngine.ECS.Queries.QueryArchetypeMatch,
			[2] int32,
			[3] valuetype [System.Runtime]System.Span`1<!!T>,
			[4] valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>
		)

		IL_0000: ldarg.0
		IL_0001: call instance class SomeEngine.ECS.World SomeEngine.ECS.Queries.QueryData::get_World()
		IL_0006: ldarg.0
		IL_0007: call instance class SomeEngine.ECS.Queries.QueryState SomeEngine.ECS.Queries.QueryData::get_State()
		IL_000c: ldarg.1
		IL_000d: ldarg.0
		IL_000e: call instance uint32 SomeEngine.ECS.Queries.QueryData::get_LastSystemVersion()
		IL_0013: ldloca.s 0
		IL_0015: ldloca.s 1
		IL_0017: callvirt instance bool SomeEngine.ECS.World::TryGetEntityQueryRecord(class SomeEngine.ECS.Queries.QueryState, valuetype SomeEngine.ECS.Entities.Entity, uint32, valuetype SomeEngine.ECS.Entities.EntityRecord&, class SomeEngine.ECS.Queries.QueryArchetypeMatch&)
		IL_001c: brfalse.s IL_0048

		IL_001e: ldloc.1
		IL_001f: ldloca.s 2
		IL_0021: call bool SomeEngine.ECS.Queries.QueryAccessGuards::TryRequireReadAccess<!!T>(class SomeEngine.ECS.Queries.QueryArchetypeMatch, int32&)
		IL_0026: brfalse.s IL_0048

		IL_0028: ldloc.0
		IL_0029: ldfld class SomeEngine.ECS.Archetypes.Chunk SomeEngine.ECS.Entities.EntityRecord::Chunk
		IL_002e: ldloc.2
		IL_002f: callvirt instance valuetype [System.Runtime]System.Span`1<!!0> SomeEngine.ECS.Archetypes.Chunk::ComponentRows<!!T>(int32)
		IL_0034: stloc.3
		IL_0035: ldloca.s 3
		IL_0037: ldloc.0
		IL_0038: ldfld int32 SomeEngine.ECS.Entities.EntityRecord::RowInChunk
		IL_003d: call instance !0& valuetype [System.Runtime]System.Span`1<!!T>::get_Item(int32)
		IL_0042: newobj instance void valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>::.ctor(!0&)
		IL_0047: ret

		IL_0048: ldloca.s 4
		IL_004a: initobj valuetype SomeEngine.ECS.Queries.ComponentReference`1<!!T>
		IL_0050: ldloc.s 4
		IL_0052: ret
	} // end of method World::ReadEntityQueryRef
END SELECTED METHOD ECS World::ReadEntityQueryRef
ASSEMBLY Systems artifacts/ecs-relations/cache/someengine.ecs.systems/0.1.0-relations.231/lib/net10.0/SomeEngine.ECS.Systems.dll
IL TARGET count=1 Systems QueryJobAccess::CopyAccesses
BEGIN SELECTED METHOD Systems QueryJobAccess::CopyAccesses
	.method public hidebysig static 
		int32 CopyAccesses (
			class [SomeEngine.ECS]SomeEngine.ECS.World world,
			class [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryDefinition definition,
			valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess> destination
		) cil managed 
	{
		.param [3]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 00 00 00
			)
		// Method begins at RVA 0x63bc
		// Header size: 12
		// Code size: 233 (0xe9)
		.maxstack 4
		.locals init (
			[0] int32,
			[1] valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess>,
			[2] int32,
			[3] valuetype [System.Runtime]System.ReadOnlyMemory`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess>,
			[4] valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess,
			[5] valuetype SomeEngine.ECS.Systems.WorldStorageResourceKey,
			[6] valuetype [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo&,
			[7] valuetype [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo
		)

		IL_0000: ldarga.s destination
		IL_0002: ldc.i4.0
		IL_0003: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>::get_Item(int32)
		IL_0008: ldarg.1
		IL_0009: callvirt instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryDefinition::get_HasRelationshipWrite()
		IL_000e: brtrue.s IL_0018

		IL_0010: ldarg.0
		IL_0011: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess SomeEngine.ECS.Systems.RelationshipJobAccess::TopologyRead(class [SomeEngine.ECS]SomeEngine.ECS.World)
		IL_0016: br.s IL_001e

		IL_0018: ldarg.0
		IL_0019: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess SomeEngine.ECS.Systems.RelationshipJobAccess::TopologyWrite(class [SomeEngine.ECS]SomeEngine.ECS.World)

		IL_001e: stobj [SomeEngine.Job]SomeEngine.Job.JobResourceAccess
		IL_0023: ldc.i4.1
		IL_0024: stloc.0
		IL_0025: ldarg.1
		IL_0026: callvirt instance valuetype [System.Runtime]System.ReadOnlyMemory`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess> [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryDefinition::get_JobStorageAccesses()
		IL_002b: stloc.3
		IL_002c: ldloca.s 3
		IL_002e: call instance valuetype [System.Runtime]System.ReadOnlySpan`1<!0> valuetype [System.Runtime]System.ReadOnlyMemory`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess>::get_Span()
		IL_0033: stloc.1
		IL_0034: ldc.i4.0
		IL_0035: stloc.2
		IL_0036: br IL_00da
		// loop start (head: IL_00da)
			IL_003b: ldloca.s 1
			IL_003d: ldloc.2
			IL_003e: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess>::get_Item(int32)
			IL_0043: ldobj [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess
			IL_0048: stloc.s 4
			IL_004a: ldloca.s 4
			IL_004c: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldStorageKind [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess::get_Kind()
			IL_0051: brtrue.s IL_0094

			IL_0053: ldloca.s 4
			IL_0055: call instance int32 [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess::get_ComponentId()
			IL_005a: call valuetype [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentRegistry::Get(int32)
			IL_005f: stloc.s 6
			IL_0061: ldloc.s 6
			IL_0063: ldobj [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo
			IL_0068: stloc.s 7
			IL_006a: ldloca.s 7
			IL_006c: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo::get_IsJobAliasFree()
			IL_0071: brtrue.s IL_0094

			IL_0073: ldstr "Query Job access requires alias-free "
			IL_0078: ldloc.s 6
			IL_007a: ldfld class [System.Runtime]System.Type [SomeEngine.ECS]SomeEngine.ECS.Registry.ComponentInfo::Type
			IL_007f: callvirt instance string [System.Runtime]System.Reflection.MemberInfo::get_Name()
			IL_0084: ldstr " storage."
			IL_0089: call string [System.Runtime]System.String::Concat(string, string, string)
			IL_008e: newobj instance void [System.Runtime]System.InvalidOperationException::.ctor(string)
			IL_0093: throw

			IL_0094: ldloca.s 5
			IL_0096: ldloca.s 4
			IL_0098: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldStorageKind [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess::get_Kind()
			IL_009d: ldloca.s 4
			IL_009f: call instance int32 [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess::get_ComponentId()
			IL_00a4: call instance void SomeEngine.ECS.Systems.WorldStorageResourceKey::.ctor(valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldStorageKind, int32)
			IL_00a9: ldarga.s destination
			IL_00ab: ldloc.0
			IL_00ac: dup
			IL_00ad: ldc.i4.1
			IL_00ae: add
			IL_00af: stloc.0
			IL_00b0: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>::get_Item(int32)
			IL_00b5: ldloca.s 4
			IL_00b7: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldStorageAccess [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess::get_Access()
			IL_00bc: ldc.i4.2
			IL_00bd: beq.s IL_00c9

			IL_00bf: ldarg.0
			IL_00c0: ldloc.s 5
			IL_00c2: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess SomeEngine.ECS.Systems.WorldStorageJobResources::Read(class [SomeEngine.ECS]SomeEngine.ECS.World, valuetype SomeEngine.ECS.Systems.WorldStorageResourceKey)
			IL_00c7: br.s IL_00d1

			IL_00c9: ldarg.0
			IL_00ca: ldloc.s 5
			IL_00cc: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess SomeEngine.ECS.Systems.WorldStorageJobResources::Write(class [SomeEngine.ECS]SomeEngine.ECS.World, valuetype SomeEngine.ECS.Systems.WorldStorageResourceKey)

			IL_00d1: stobj [SomeEngine.Job]SomeEngine.Job.JobResourceAccess
			IL_00d6: ldloc.2
			IL_00d7: ldc.i4.1
			IL_00d8: add
			IL_00d9: stloc.2

			IL_00da: ldloc.2
			IL_00db: ldloca.s 1
			IL_00dd: call instance int32 valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype [SomeEngine.ECS]SomeEngine.ECS.WorldJobStorageAccess>::get_Length()
			IL_00e2: blt IL_003b
		// end loop

		IL_00e7: ldloc.0
		IL_00e8: ret
	} // end of method QueryJobAccess::CopyAccesses
END SELECTED METHOD Systems QueryJobAccess::CopyAccesses
ASSEMBLY Physics artifacts/ecs-relations/cache/someengine.physics/0.1.0-relations.231/lib/net10.0/SomeEngine.Physics.dll
IL TARGET count=2 Physics SpatialTraversal::Enumerate
BEGIN SELECTED METHOD Physics SpatialTraversal::Enumerate
	.method assembly hidebysig static 
		bool Enumerate<valuetype .ctor (class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>, [System.Runtime]System.ValueType) TFilter, valuetype .ctor (class [System.Runtime]System.ValueType modreq([System.Runtime]System.Runtime.InteropServices.UnmanagedType)) TGroupResults, valuetype .ctor (class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.IQueryRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>, [System.Runtime]System.ValueType) TOutput> (
			class SomeEngine.Physics.CollisionTree tree,
			valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews queries,
			!!TFilter 'filter',
			bool checkMembers,
			!!TOutput& output
		) cil managed 
	{
		.param constraint TFilter, class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 02 00 00 00 01 00 00 00
			)
		.param type TGroupResults
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (
				01 00 00 00
			)
		.param constraint TOutput, class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.IQueryRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 02 00 00 00 01 00 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x465e
		// Header size: 1
		// Code size: 29 (0x1d)
		.maxstack 8

		IL_0000: ldarg.0
		IL_0001: ldarg.1
		IL_0002: ldarg.2
		IL_0003: ldarg.3
		IL_0004: ldarg.0
		IL_0005: callvirt instance int32 SomeEngine.Physics.CollisionTree::get_Root()
		IL_000a: ldarg.0
		IL_000b: callvirt instance int32 SomeEngine.Physics.CollisionTree::get_Root()
		IL_0010: newobj instance void SomeEngine.Physics.CollisionNodePair::.ctor(int32, int32)
		IL_0015: ldarg.s output
		IL_0017: call bool SomeEngine.Physics.SpatialTraversal::Enumerate<!!TFilter, !!TGroupResults, !!TOutput>(class SomeEngine.Physics.CollisionTree, valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews, !!0, bool, valuetype SomeEngine.Physics.CollisionNodePair, !!2&)
		IL_001c: ret
	} // end of method SpatialTraversal::Enumerate
END SELECTED METHOD Physics SpatialTraversal::Enumerate
BEGIN SELECTED METHOD Physics SpatialTraversal::Enumerate
	.method assembly hidebysig static 
		bool Enumerate<valuetype .ctor (class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>, [System.Runtime]System.ValueType) TFilter, valuetype .ctor (class [System.Runtime]System.ValueType modreq([System.Runtime]System.Runtime.InteropServices.UnmanagedType)) TGroupResults, valuetype .ctor (class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.IQueryRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>, [System.Runtime]System.ValueType) TOutput> (
			class SomeEngine.Physics.CollisionTree tree,
			valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews queries,
			!!TFilter 'filter',
			bool checkMembers,
			valuetype SomeEngine.Physics.CollisionNodePair partition,
			!!TOutput& output
		) cil managed 
	{
		.param constraint TFilter, class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 02 00 00 00 01 00 00 00
			)
		.param type TGroupResults
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (
				01 00 00 00
			)
		.param constraint TOutput, class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.IQueryRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8[]) = (
				01 00 02 00 00 00 01 00 00 00
			)
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x467c
		// Header size: 12
		// Code size: 967 (0x3c7)
		.maxstack 7
		.locals init (
			[0] valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode>,
			[1] valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>,
			[2] int32,
			[3] int32,
			[4] !!TGroupResults,
			[5] valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>,
			[6] valuetype SomeEngine.Physics.CollisionNode&,
			[7] valuetype SomeEngine.Physics.CollisionNode&,
			[8] valuetype SomeEngine.Physics.GroupMatch,
			[9] !!TGroupResults,
			[10] valuetype SomeEngine.Physics.GroupMatch,
			[11] !!TGroupResults,
			[12] int32,
			[13] int32,
			[14] valuetype SomeEngine.Physics.GroupMatch,
			[15] !!TGroupResults,
			[16] valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>,
			[17] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[18] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow,
			[19] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor,
			[20] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor
		)

		IL_0000: ldarg.0
		IL_0001: callvirt instance int32 SomeEngine.Physics.CollisionTree::get_Root()
		IL_0006: ldc.i4.0
		IL_0007: bge.s IL_000b

		IL_0009: ldc.i4.1
		IL_000a: ret

		IL_000b: ldarg.0
		IL_000c: callvirt instance valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode> SomeEngine.Physics.CollisionTree::get_Nodes()
		IL_0011: stloc.0
		IL_0012: ldc.i4.2
		IL_0013: ldarg.0
		IL_0014: callvirt instance int32 SomeEngine.Physics.CollisionTree::get_Height()
		IL_0019: mul
		IL_001a: ldc.i4.1
		IL_001b: add
		IL_001c: stloc.3
		IL_001d: ldloc.3
		IL_001e: conv.u
		IL_001f: sizeof valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
		IL_0025: mul.ovf.un
		IL_0026: localloc
		IL_0028: ldloc.3
		IL_0029: newobj instance void valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::.ctor(void*, int32)
		IL_002e: stloc.1
		IL_002f: ldc.i4.0
		IL_0030: stloc.2
		IL_0031: ldloca.s 1
		IL_0033: ldloc.2
		IL_0034: dup
		IL_0035: ldc.i4.1
		IL_0036: add
		IL_0037: stloc.2
		IL_0038: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
		IL_003d: ldarga.s partition
		IL_003f: call instance int32 SomeEngine.Physics.CollisionNodePair::get_First()
		IL_0044: ldarga.s partition
		IL_0046: call instance int32 SomeEngine.Physics.CollisionNodePair::get_Second()
		IL_004b: ldloca.s 4
		IL_004d: initobj !!TGroupResults
		IL_0053: ldloc.s 4
		IL_0055: ldloca.s 4
		IL_0057: initobj !!TGroupResults
		IL_005d: ldloc.s 4
		IL_005f: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
		IL_0064: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
		IL_0069: br IL_03be
		// loop start (head: IL_03be)
			IL_006e: ldloca.s 1
			IL_0070: ldloc.2
			IL_0071: ldc.i4.1
			IL_0072: sub
			IL_0073: dup
			IL_0074: stloc.2
			IL_0075: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_007a: ldobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_007f: stloc.s 5
			IL_0081: ldloca.s 0
			IL_0083: ldloc.s 5
			IL_0085: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_008a: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode>::get_Item(int32)
			IL_008f: stloc.s 6
			IL_0091: ldloca.s 0
			IL_0093: ldloc.s 5
			IL_0095: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_009a: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode>::get_Item(int32)
			IL_009f: stloc.s 7
			IL_00a1: ldloc.s 5
			IL_00a3: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_00a8: ldloc.s 5
			IL_00aa: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_00af: bne.un.s IL_00bd

			IL_00b1: ldloc.s 6
			IL_00b3: call instance bool SomeEngine.Physics.CollisionNode::get_IsLeaf()
			IL_00b8: brtrue IL_03be

			IL_00bd: ldloc.s 5
			IL_00bf: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_00c4: ldloc.s 5
			IL_00c6: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_00cb: beq.s IL_00e5

			IL_00cd: ldloc.s 6
			IL_00cf: ldflda valuetype SomeEngine.Physics.CollisionBounds SomeEngine.Physics.CollisionNode::Bounds
			IL_00d4: ldloc.s 7
			IL_00d6: ldflda valuetype SomeEngine.Physics.CollisionBounds SomeEngine.Physics.CollisionNode::Bounds
			IL_00db: call instance bool SomeEngine.Physics.CollisionBounds::Overlaps(valuetype SomeEngine.Physics.CollisionBounds&)
			IL_00e0: brfalse IL_03be

			IL_00e5: ldarga.s 'filter'
			IL_00e7: ldloc.s 5
			IL_00e9: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_00ee: ldloc.s 5
			IL_00f0: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_00f5: ldloc.s 6
			IL_00f7: ldloc.s 7
			IL_00f9: ldloca.s 5
			IL_00fb: ldflda !0 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Forward
			IL_0100: ldloca.s 9
			IL_0102: constrained. !!TFilter
			IL_0108: callvirt instance valuetype SomeEngine.Physics.GroupMatch class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>::MatchGroups(int32, int32, valuetype SomeEngine.Physics.CollisionNode& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), valuetype SomeEngine.Physics.CollisionNode& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !0&)
			IL_010d: stloc.s 8
			IL_010f: ldarga.s 'filter'
			IL_0111: ldloc.s 5
			IL_0113: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_0118: ldloc.s 5
			IL_011a: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_011f: ldloc.s 7
			IL_0121: ldloc.s 6
			IL_0123: ldloca.s 5
			IL_0125: ldflda !0 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Reverse
			IL_012a: ldloca.s 11
			IL_012c: constrained. !!TFilter
			IL_0132: callvirt instance valuetype SomeEngine.Physics.GroupMatch class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>::MatchGroups(int32, int32, valuetype SomeEngine.Physics.CollisionNode& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), valuetype SomeEngine.Physics.CollisionNode& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), !0&)
			IL_0137: stloc.s 10
			IL_0139: ldloc.s 8
			IL_013b: ldc.i4.1
			IL_013c: bne.un.s IL_0146

			IL_013e: ldloc.s 10
			IL_0140: ldc.i4.1
			IL_0141: beq IL_03be

			IL_0146: ldloc.s 5
			IL_0148: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_014d: ldloc.s 5
			IL_014f: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_0154: bne.un.s IL_01d3

			IL_0156: ldloca.s 1
			IL_0158: ldloc.2
			IL_0159: dup
			IL_015a: ldc.i4.1
			IL_015b: add
			IL_015c: stloc.2
			IL_015d: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_0162: ldloc.s 6
			IL_0164: call instance int32 SomeEngine.Physics.CollisionNode::get_SecondChild()
			IL_0169: ldloc.s 6
			IL_016b: call instance int32 SomeEngine.Physics.CollisionNode::get_SecondChild()
			IL_0170: ldloc.s 9
			IL_0172: ldloc.s 11
			IL_0174: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_0179: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_017e: ldloca.s 1
			IL_0180: ldloc.2
			IL_0181: dup
			IL_0182: ldc.i4.1
			IL_0183: add
			IL_0184: stloc.2
			IL_0185: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_018a: ldloc.s 6
			IL_018c: call instance int32 SomeEngine.Physics.CollisionNode::get_FirstChild()
			IL_0191: ldloc.s 6
			IL_0193: call instance int32 SomeEngine.Physics.CollisionNode::get_SecondChild()
			IL_0198: ldloc.s 9
			IL_019a: ldloc.s 11
			IL_019c: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_01a1: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_01a6: ldloca.s 1
			IL_01a8: ldloc.2
			IL_01a9: dup
			IL_01aa: ldc.i4.1
			IL_01ab: add
			IL_01ac: stloc.2
			IL_01ad: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_01b2: ldloc.s 6
			IL_01b4: call instance int32 SomeEngine.Physics.CollisionNode::get_FirstChild()
			IL_01b9: ldloc.s 6
			IL_01bb: call instance int32 SomeEngine.Physics.CollisionNode::get_FirstChild()
			IL_01c0: ldloc.s 9
			IL_01c2: ldloc.s 11
			IL_01c4: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_01c9: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_01ce: br IL_03be

			IL_01d3: ldloc.s 6
			IL_01d5: call instance bool SomeEngine.Physics.CollisionNode::get_IsLeaf()
			IL_01da: brfalse IL_02fa

			IL_01df: ldloc.s 7
			IL_01e1: call instance bool SomeEngine.Physics.CollisionNode::get_IsLeaf()
			IL_01e6: brfalse IL_02fa

			IL_01eb: ldloc.s 6
			IL_01ed: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity SomeEngine.Physics.CollisionNode::get_Entity()
			IL_01f2: ldloc.s 7
			IL_01f4: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity SomeEngine.Physics.CollisionNode::get_Entity()
			IL_01f9: call bool [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.EntityOrder::Precedes(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity)
			IL_01fe: dup
			IL_01ff: brtrue.s IL_020a

			IL_0201: ldloc.s 5
			IL_0203: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_0208: br.s IL_0211

			IL_020a: ldloc.s 5
			IL_020c: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First

			IL_0211: stloc.s 12
			IL_0213: dup
			IL_0214: brtrue.s IL_021f

			IL_0216: ldloc.s 5
			IL_0218: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_021d: br.s IL_0226

			IL_021f: ldloc.s 5
			IL_0221: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second

			IL_0226: stloc.s 13
			IL_0228: dup
			IL_0229: brtrue.s IL_022f

			IL_022b: ldloc.s 10
			IL_022d: br.s IL_0231

			IL_022f: ldloc.s 8

			IL_0231: stloc.s 14
			IL_0233: brtrue.s IL_0239

			IL_0235: ldloc.s 11
			IL_0237: br.s IL_023b

			IL_0239: ldloc.s 9

			IL_023b: stloc.s 15
			IL_023d: ldloc.s 14
			IL_023f: ldc.i4.1
			IL_0240: beq IL_03be

			IL_0245: ldloca.s 16
			IL_0247: ldloca.s 0
			IL_0249: ldloc.s 12
			IL_024b: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode>::get_Item(int32)
			IL_0250: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity SomeEngine.Physics.CollisionNode::get_Entity()
			IL_0255: ldloca.s 0
			IL_0257: ldloc.s 13
			IL_0259: call instance !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype SomeEngine.Physics.CollisionNode>::get_Item(int32)
			IL_025e: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity SomeEngine.Physics.CollisionNode::get_Entity()
			IL_0263: call instance void valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::.ctor(!0, !1)
			IL_0268: ldloca.s 17
			IL_026a: initobj [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow
			IL_0270: ldloca.s 18
			IL_0272: initobj [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow
			IL_0278: ldarg.3
			IL_0279: brtrue.s IL_0288

			IL_027b: constrained. !!TFilter
			IL_0281: call bool class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>::get_ReadsComponentRows()
			IL_0286: brfalse.s IL_02c4

			IL_0288: ldarga.s queries
			IL_028a: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
			IL_028f: stloc.s 19
			IL_0291: ldloca.s 19
			IL_0293: ldloc.s 16
			IL_0295: ldfld !0 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item1
			IL_029a: ldloca.s 17
			IL_029c: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
			IL_02a1: brfalse IL_03be

			IL_02a6: ldarga.s queries
			IL_02a8: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
			IL_02ad: stloc.s 20
			IL_02af: ldloca.s 20
			IL_02b1: ldloc.s 16
			IL_02b3: ldfld !1 valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>::Item2
			IL_02b8: ldloca.s 18
			IL_02ba: call instance bool [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor::TryGetRow(valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow&)
			IL_02bf: brfalse IL_03be

			IL_02c4: ldloc.s 14
			IL_02c6: brtrue.s IL_02e4

			IL_02c8: ldarga.s 'filter'
			IL_02ca: ldloca.s 17
			IL_02cc: ldloca.s 18
			IL_02ce: ldloc.s 12
			IL_02d0: ldloc.s 13
			IL_02d2: ldloca.s 15
			IL_02d4: constrained. !!TFilter
			IL_02da: callvirt instance bool class SomeEngine.Physics.ISpatialFilter`1<!!TGroupResults>::MatchesLeaves(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), int32, int32, !0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
			IL_02df: brfalse IL_03be

			IL_02e4: ldarg.s output
			IL_02e6: ldloca.s 16
			IL_02e8: constrained. !!TOutput
			IL_02ee: callvirt instance bool class [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.IRelationOutput`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>::Add(!0& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
			IL_02f3: brtrue IL_03be

			IL_02f8: ldc.i4.0
			IL_02f9: ret

			IL_02fa: ldloc.s 7
			IL_02fc: call instance bool SomeEngine.Physics.CollisionNode::get_IsLeaf()
			IL_0301: brtrue.s IL_031c

			IL_0303: ldloc.s 6
			IL_0305: call instance bool SomeEngine.Physics.CollisionNode::get_IsLeaf()
			IL_030a: brtrue.s IL_036e

			IL_030c: ldloc.s 6
			IL_030e: call instance int32 SomeEngine.Physics.CollisionNode::get_Height()
			IL_0313: ldloc.s 7
			IL_0315: call instance int32 SomeEngine.Physics.CollisionNode::get_Height()
			IL_031a: blt.s IL_036e

			IL_031c: ldloca.s 1
			IL_031e: ldloc.2
			IL_031f: dup
			IL_0320: ldc.i4.1
			IL_0321: add
			IL_0322: stloc.2
			IL_0323: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_0328: ldloc.s 6
			IL_032a: call instance int32 SomeEngine.Physics.CollisionNode::get_SecondChild()
			IL_032f: ldloc.s 5
			IL_0331: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_0336: ldloc.s 9
			IL_0338: ldloc.s 11
			IL_033a: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_033f: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_0344: ldloca.s 1
			IL_0346: ldloc.2
			IL_0347: dup
			IL_0348: ldc.i4.1
			IL_0349: add
			IL_034a: stloc.2
			IL_034b: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_0350: ldloc.s 6
			IL_0352: call instance int32 SomeEngine.Physics.CollisionNode::get_FirstChild()
			IL_0357: ldloc.s 5
			IL_0359: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::Second
			IL_035e: ldloc.s 9
			IL_0360: ldloc.s 11
			IL_0362: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_0367: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_036c: br.s IL_03be

			IL_036e: ldloca.s 1
			IL_0370: ldloc.2
			IL_0371: dup
			IL_0372: ldc.i4.1
			IL_0373: add
			IL_0374: stloc.2
			IL_0375: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_037a: ldloc.s 5
			IL_037c: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_0381: ldloc.s 7
			IL_0383: call instance int32 SomeEngine.Physics.CollisionNode::get_SecondChild()
			IL_0388: ldloc.s 9
			IL_038a: ldloc.s 11
			IL_038c: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_0391: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>
			IL_0396: ldloca.s 1
			IL_0398: ldloc.2
			IL_0399: dup
			IL_039a: ldc.i4.1
			IL_039b: add
			IL_039c: stloc.2
			IL_039d: call instance !0& valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>>::get_Item(int32)
			IL_03a2: ldloc.s 5
			IL_03a4: ldfld int32 valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::First
			IL_03a9: ldloc.s 7
			IL_03ab: call instance int32 SomeEngine.Physics.CollisionNode::get_FirstChild()
			IL_03b0: ldloc.s 9
			IL_03b2: ldloc.s 11
			IL_03b4: newobj instance void valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>::.ctor(int32, int32, !0, !0)
			IL_03b9: stobj valuetype SomeEngine.Physics.SpatialNodePair`1<!!TGroupResults>

			IL_03be: ldloc.2
			IL_03bf: ldc.i4.0
			IL_03c0: bgt IL_006e
		// end loop

		IL_03c5: ldc.i4.1
		IL_03c6: ret
	} // end of method SpatialTraversal::Enumerate
END SELECTED METHOD Physics SpatialTraversal::Enumerate
IL TARGET count=1 Physics SpatialBitmapFilter`5::MatchesLeaves
BEGIN SELECTED METHOD Physics SpatialBitmapFilter`5::MatchesLeaves
	.method public hidebysig 
		instance bool MatchesLeaves (
			[in] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow& firstRow,
			[in] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryRow& secondRow,
			int32 firstNode,
			int32 secondNode,
			[in] valuetype SomeEngine.Physics.GroupMatch& results
		) cil managed aggressiveinlining 
	{
		.param [1]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		.param [5]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.IsReadOnlyAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x43dc
		// Header size: 12
		// Code size: 111 (0x6f)
		.maxstack 3
		.locals init (
			[0] valuetype SomeEngine.Physics.CollisionBitmapData&,
			[1] valuetype SomeEngine.Physics.CollisionBitmapData&,
			[2] valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits,
			[3] valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits
		)

		IL_0000: ldarg.s results
		IL_0002: ldind.u1
		IL_0003: brfalse.s IL_000c

		IL_0005: ldarg.s results
		IL_0007: ldind.u1
		IL_0008: ldc.i4.2
		IL_0009: ceq
		IL_000b: ret

		IL_000c: ldarg.0
		IL_000d: call instance class SomeEngine.Physics.CollisionBitmapSummary`4<!0, !1, !2, !3> valuetype SomeEngine.Physics.SpatialBitmapFilter`5<!TRelation, !TImplementation, !TComponent, !TPermission, !TGroupPermission>::get_Summary()
		IL_0012: ldarg.3
		IL_0013: callvirt instance valuetype SomeEngine.Physics.CollisionBitmapData& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) class SomeEngine.Physics.CollisionBitmapSummary`4<!TRelation, !TImplementation, !TComponent, !TPermission>::get_Item(int32)
		IL_0018: stloc.0
		IL_0019: ldarg.0
		IL_001a: call instance class SomeEngine.Physics.CollisionBitmapSummary`4<!0, !1, !2, !3> valuetype SomeEngine.Physics.SpatialBitmapFilter`5<!TRelation, !TImplementation, !TComponent, !TPermission, !TGroupPermission>::get_Summary()
		IL_001f: ldarg.s secondNode
		IL_0021: callvirt instance valuetype SomeEngine.Physics.CollisionBitmapData& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute) class SomeEngine.Physics.CollisionBitmapSummary`4<!TRelation, !TImplementation, !TComponent, !TPermission>::get_Item(int32)
		IL_0026: stloc.1
		IL_0027: ldloc.0
		IL_0028: ldfld int32 SomeEngine.Physics.CollisionBitmapData::ComponentCount
		IL_002d: brfalse.s IL_0037

		IL_002f: ldloc.1
		IL_0030: ldfld int32 SomeEngine.Physics.CollisionBitmapData::ComponentCount
		IL_0035: brtrue.s IL_0039

		IL_0037: ldc.i4.0
		IL_0038: ret

		IL_0039: ldloca.s 2
		IL_003b: ldloc.0
		IL_003c: ldfld uint64 SomeEngine.Physics.CollisionBitmapData::CategoriesAny
		IL_0041: ldloc.0
		IL_0042: ldfld uint64 SomeEngine.Physics.CollisionBitmapData::AllowedAny
		IL_0047: call instance void [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits::.ctor(uint64, uint64)
		IL_004c: ldloca.s 3
		IL_004e: ldloc.1
		IL_004f: ldfld uint64 SomeEngine.Physics.CollisionBitmapData::CategoriesAny
		IL_0054: ldloc.1
		IL_0055: ldfld uint64 SomeEngine.Physics.CollisionBitmapData::AllowedAny
		IL_005a: call instance void [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits::.ctor(uint64, uint64)
		IL_005f: ldloca.s 2
		IL_0061: ldloca.s 3
		IL_0063: constrained. !TPermission
		IL_0069: call bool [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.ICategoryPermission::Matches(valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute), valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.CategoryBits& modreq([System.Runtime]System.Runtime.InteropServices.InAttribute))
		IL_006e: ret
	} // end of method SpatialBitmapFilter`5::MatchesLeaves
END SELECTED METHOD Physics SpatialBitmapFilter`5::MatchesLeaves
IL TARGET count=1 Physics CountJob`1::Execute
BEGIN SELECTED METHOD Physics CountJob`1::Execute
		.method public final hidebysig newslot virtual 
			instance void Execute (
				int32 index,
				valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor access,
				valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor members
			) cil managed 
		{
			.param [2]
				.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
					01 00 00 00
				)
			.param [3]
				.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
					01 00 00 00
				)
			// Method begins at RVA 0x555c
			// Header size: 12
			// Code size: 81 (0x51)
			.maxstack 5
			.locals init (
				[0] valuetype SomeEngine.Physics.CollisionPairQueries/PairCount,
				[1] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor
			)

			IL_0000: ldloca.s 0
			IL_0002: initobj SomeEngine.Physics.CollisionPairQueries/PairCount
			IL_0008: ldarg.0
			IL_0009: ldflda !0 valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!TEnumerable>::'<source>P'
			IL_000e: ldarg.0
			IL_000f: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!TEnumerable>::'<buffer>P'
			IL_0014: ldfld valuetype SomeEngine.Physics.CollisionNodePair[] SomeEngine.Physics.CollisionPairBuffer::_partitions
			IL_0019: ldarg.1
			IL_001a: ldelem SomeEngine.Physics.CollisionNodePair
			IL_001f: ldarg.2
			IL_0020: ldarg.3
			IL_0021: ldloca.s 1
			IL_0023: initobj [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor
			IL_0029: ldloc.1
			IL_002a: newobj instance void [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::.ctor(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor)
			IL_002f: ldloca.s 0
			IL_0031: constrained. !TEnumerable
			IL_0037: callvirt instance bool SomeEngine.Physics.ICollisionPartitionEnumerable::Enumerate<valuetype SomeEngine.Physics.CollisionPairQueries/PairCount>(valuetype SomeEngine.Physics.CollisionNodePair, valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews, !!0&)
			IL_003c: pop
			IL_003d: ldarg.0
			IL_003e: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!TEnumerable>::'<buffer>P'
			IL_0043: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_counts
			IL_0048: ldarg.1
			IL_0049: ldloc.0
			IL_004a: ldfld int32 SomeEngine.Physics.CollisionPairQueries/PairCount::Count
			IL_004f: stelem.i4
			IL_0050: ret
		} // end of method CountJob`1::Execute
END SELECTED METHOD Physics CountJob`1::Execute
IL TARGET count=1 Physics FillJob`1::Execute
BEGIN SELECTED METHOD Physics FillJob`1::Execute
		.method public final hidebysig newslot virtual 
			instance void Execute (
				int32 index,
				valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor access,
				valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor members
			) cil managed 
		{
			.param [2]
				.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
					01 00 00 00
				)
			.param [3]
				.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
					01 00 00 00
				)
			// Method begins at RVA 0x55cc
			// Header size: 12
			// Code size: 126 (0x7e)
			.maxstack 5
			.locals init (
				[0] int32,
				[1] int32,
				[2] int32,
				[3] valuetype SomeEngine.Physics.CollisionPairBuffer/PairArrayOutput,
				[4] valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor
			)

			IL_0000: ldarg.0
			IL_0001: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<buffer>P'
			IL_0006: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_fillPartitions
			IL_000b: ldarg.1
			IL_000c: ldelem.i4
			IL_000d: stloc.0
			IL_000e: ldarg.0
			IL_000f: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<buffer>P'
			IL_0014: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_offsets
			IL_0019: ldloc.0
			IL_001a: ldelem.i4
			IL_001b: stloc.1
			IL_001c: ldarg.0
			IL_001d: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<buffer>P'
			IL_0022: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_offsets
			IL_0027: ldloc.0
			IL_0028: ldc.i4.1
			IL_0029: add
			IL_002a: ldelem.i4
			IL_002b: ldloc.1
			IL_002c: sub
			IL_002d: stloc.2
			IL_002e: ldloca.s 3
			IL_0030: ldarg.0
			IL_0031: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<buffer>P'
			IL_0036: ldfld valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>[] SomeEngine.Physics.CollisionPairBuffer::_pairs
			IL_003b: ldloc.1
			IL_003c: ldloc.2
			IL_003d: call valuetype [System.Runtime]System.Span`1<!!0> [System.Memory]System.MemoryExtensions::AsSpan<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>(!!0[], int32, int32)
			IL_0042: call instance void SomeEngine.Physics.CollisionPairBuffer/PairArrayOutput::.ctor(valuetype [System.Runtime]System.Span`1<valuetype [System.Runtime]System.ValueTuple`2<valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity, valuetype [SomeEngine.ECS]SomeEngine.ECS.Entities.Entity>>)
			IL_0047: ldarg.0
			IL_0048: ldflda !0 valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<source>P'
			IL_004d: ldarg.0
			IL_004e: ldfld class SomeEngine.Physics.CollisionPairBuffer valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!TEnumerable>::'<buffer>P'
			IL_0053: ldfld valuetype SomeEngine.Physics.CollisionNodePair[] SomeEngine.Physics.CollisionPairBuffer::_partitions
			IL_0058: ldloc.0
			IL_0059: ldelem SomeEngine.Physics.CollisionNodePair
			IL_005e: ldarg.2
			IL_005f: ldarg.3
			IL_0060: ldloca.s 4
			IL_0062: initobj [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor
			IL_0068: ldloc.s 4
			IL_006a: newobj instance void [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::.ctor(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor)
			IL_006f: ldloca.s 3
			IL_0071: constrained. !TEnumerable
			IL_0077: callvirt instance bool SomeEngine.Physics.ICollisionPartitionEnumerable::Enumerate<valuetype SomeEngine.Physics.CollisionPairBuffer/PairArrayOutput>(valuetype SomeEngine.Physics.CollisionNodePair, valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews, !!0&)
			IL_007c: pop
			IL_007d: ret
		} // end of method FillJob`1::Execute
END SELECTED METHOD Physics FillJob`1::Execute
IL TARGET count=1 Physics CollisionPairBuffer::Fill
BEGIN SELECTED METHOD Physics CollisionPairBuffer::Fill
	.method assembly hidebysig 
		instance void Fill<valuetype .ctor (SomeEngine.Physics.ICollisionPartitionEnumerable, [System.Runtime]System.ValueType) TEnumerable> (
			!!TEnumerable source,
			valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews queries,
			bool parallel
		) cil managed 
	{
		.param constraint TEnumerable, SomeEngine.Physics.ICollisionPartitionEnumerable
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 01 00 00
			)
		.param [2]
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.ScopedRefAttribute::.ctor() = (
				01 00 00 00
			)
		// Method begins at RVA 0x3df8
		// Header size: 12
		// Code size: 417 (0x1a1)
		.maxstack 8
		.locals init (
			[0] int32,
			[1] valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!!TEnumerable>,
			[2] int32,
			[3] valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!!TEnumerable>,
			[4] int32,
			[5] valuetype [SomeEngine.Job]SomeEngine.Job.JobHandle,
			[6] int32,
			[7] int32
		)

		IL_0000: ldarga.s source
		IL_0002: ldarg.2
		IL_0003: constrained. !!TEnumerable
		IL_0009: callvirt instance void SomeEngine.Physics.ICollisionPartitionEnumerable::Prepare(valuetype [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews)
		IL_000e: ldarga.s source
		IL_0010: constrained. !!TEnumerable
		IL_0016: callvirt instance class SomeEngine.Physics.CollisionTree SomeEngine.Physics.ICollisionPartitionEnumerable::get_Tree()
		IL_001b: ldarg.0
		IL_001c: ldfld valuetype SomeEngine.Physics.CollisionNodePair[] SomeEngine.Physics.CollisionPairBuffer::_partitions
		IL_0021: call valuetype [System.Runtime]System.Span`1<!0> valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.CollisionNodePair>::op_Implicit(!0[])
		IL_0026: callvirt instance int32 SomeEngine.Physics.CollisionTree::CreatePartitions(valuetype [System.Runtime]System.Span`1<valuetype SomeEngine.Physics.CollisionNodePair>)
		IL_002b: stloc.0
		IL_002c: ldarg.0
		IL_002d: ldloc.0
		IL_002e: call instance void SomeEngine.Physics.CollisionPairBuffer::set_PartitionCount(int32)
		IL_0033: ldc.i4.0
		IL_0034: stloc.s 4
		IL_0036: br.s IL_005b
		// loop start (head: IL_005b)
			IL_0038: ldarg.0
			IL_0039: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess[] SomeEngine.Physics.CollisionPairBuffer::_countAccesses
			IL_003e: ldloc.s 4
			IL_0040: ldarg.0
			IL_0041: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken SomeEngine.Physics.CollisionPairBuffer::_countResource
			IL_0046: ldloc.s 4
			IL_0048: conv.i8
			IL_0049: ldc.i4.1
			IL_004a: conv.i8
			IL_004b: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess [SomeEngine.Job]SomeEngine.Job.JobResourceAccess::Write(valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken, int64, int64)
			IL_0050: stelem [SomeEngine.Job]SomeEngine.Job.JobResourceAccess
			IL_0055: ldloc.s 4
			IL_0057: ldc.i4.1
			IL_0058: add
			IL_0059: stloc.s 4

			IL_005b: ldloc.s 4
			IL_005d: ldloc.0
			IL_005e: blt.s IL_0038
		// end loop

		IL_0060: ldloca.s 1
		IL_0062: ldarg.1
		IL_0063: ldarg.0
		IL_0064: call instance void valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!!TEnumerable>::.ctor(!0, class SomeEngine.Physics.CollisionPairBuffer)
		IL_0069: ldarg.3
		IL_006a: brfalse.s IL_00a2

		IL_006c: ldarga.s queries
		IL_006e: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_Access()
		IL_0073: ldarga.s queries
		IL_0075: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
		IL_007a: ldloca.s 1
		IL_007c: ldloc.0
		IL_007d: ldarg.0
		IL_007e: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess[] SomeEngine.Physics.CollisionPairBuffer::_countAccesses
		IL_0083: ldc.i4.0
		IL_0084: ldloc.0
		IL_0085: call valuetype [System.Runtime]System.Span`1<!!0> [System.Memory]System.MemoryExtensions::AsSpan<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>(!!0[], int32, int32)
		IL_008a: call valuetype [System.Runtime]System.ReadOnlySpan`1<!0> valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>::op_Implicit(valuetype [System.Runtime]System.Span`1<!0>)
		IL_008f: ldc.i4.1
		IL_0090: ldloca.s 5
		IL_0092: initobj [SomeEngine.Job]SomeEngine.Job.JobHandle
		IL_0098: ldloc.s 5
		IL_009a: call valuetype [SomeEngine.Job]SomeEngine.Job.JobHandle [SomeEngine.ECS.Systems]SomeEngine.ECS.Systems.ReadOnlyQueryPairJobs::ExecuteParallel<valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!!TEnumerable>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, !!0&, int32, valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>, int32, valuetype [SomeEngine.Job]SomeEngine.Job.JobHandle)
		IL_009f: pop
		IL_00a0: br.s IL_00b8

		IL_00a2: ldarga.s queries
		IL_00a4: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_Access()
		IL_00a9: ldarga.s queries
		IL_00ab: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
		IL_00b0: ldloca.s 1
		IL_00b2: ldloc.0
		IL_00b3: call void [SomeEngine.ECS.Systems]SomeEngine.ECS.Systems.ReadOnlyQueryPairJobs::ExecuteInline<valuetype SomeEngine.Physics.CollisionPairBuffer/CountJob`1<!!TEnumerable>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, !!0&, int32)

		IL_00b8: ldarg.0
		IL_00b9: ldloc.0
		IL_00ba: call instance void SomeEngine.Physics.CollisionPairBuffer::BuildOffsets(int32)
		IL_00bf: ldc.i4.0
		IL_00c0: stloc.2
		IL_00c1: ldc.i4.0
		IL_00c2: stloc.s 6
		IL_00c4: br.s IL_013b
		// loop start (head: IL_013b)
			IL_00c6: ldarg.0
			IL_00c7: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_offsets
			IL_00cc: ldloc.s 6
			IL_00ce: ldc.i4.1
			IL_00cf: add
			IL_00d0: ldelem.i4
			IL_00d1: ldarg.0
			IL_00d2: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_offsets
			IL_00d7: ldloc.s 6
			IL_00d9: ldelem.i4
			IL_00da: sub
			IL_00db: stloc.s 7
			IL_00dd: ldloc.s 7
			IL_00df: brfalse.s IL_0135

			IL_00e1: ldarg.0
			IL_00e2: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_fillPartitions
			IL_00e7: ldloc.2
			IL_00e8: ldloc.s 6
			IL_00ea: stelem.i4
			IL_00eb: ldarg.0
			IL_00ec: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess[] SomeEngine.Physics.CollisionPairBuffer::_fillAccesses
			IL_00f1: ldc.i4.2
			IL_00f2: ldloc.2
			IL_00f3: mul
			IL_00f4: ldarg.0
			IL_00f5: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken SomeEngine.Physics.CollisionPairBuffer::_offsetResource
			IL_00fa: ldloc.s 6
			IL_00fc: conv.i8
			IL_00fd: ldc.i4.2
			IL_00fe: conv.i8
			IL_00ff: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess [SomeEngine.Job]SomeEngine.Job.JobResourceAccess::Read(valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken, int64, int64)
			IL_0104: stelem [SomeEngine.Job]SomeEngine.Job.JobResourceAccess
			IL_0109: ldarg.0
			IL_010a: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess[] SomeEngine.Physics.CollisionPairBuffer::_fillAccesses
			IL_010f: ldc.i4.2
			IL_0110: ldloc.2
			IL_0111: mul
			IL_0112: ldc.i4.1
			IL_0113: add
			IL_0114: ldarg.0
			IL_0115: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken SomeEngine.Physics.CollisionPairBuffer::_pairRangesResource
			IL_011a: ldarg.0
			IL_011b: ldfld int32[] SomeEngine.Physics.CollisionPairBuffer::_offsets
			IL_0120: ldloc.s 6
			IL_0122: ldelem.i4
			IL_0123: conv.i8
			IL_0124: ldloc.s 7
			IL_0126: conv.i8
			IL_0127: call valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess [SomeEngine.Job]SomeEngine.Job.JobResourceAccess::Write(valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceToken, int64, int64)
			IL_012c: stelem [SomeEngine.Job]SomeEngine.Job.JobResourceAccess
			IL_0131: ldloc.2
			IL_0132: ldc.i4.1
			IL_0133: add
			IL_0134: stloc.2

			IL_0135: ldloc.s 6
			IL_0137: ldc.i4.1
			IL_0138: add
			IL_0139: stloc.s 6

			IL_013b: ldloc.s 6
			IL_013d: ldloc.0
			IL_013e: blt.s IL_00c6
		// end loop

		IL_0140: ldloca.s 3
		IL_0142: ldarg.1
		IL_0143: ldarg.0
		IL_0144: call instance void valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!!TEnumerable>::.ctor(!0, class SomeEngine.Physics.CollisionPairBuffer)
		IL_0149: ldarg.0
		IL_014a: ldloc.2
		IL_014b: call instance void SomeEngine.Physics.CollisionPairBuffer::set_FilledPartitionCount(int32)
		IL_0150: ldarg.3
		IL_0151: brfalse.s IL_018a

		IL_0153: ldarga.s queries
		IL_0155: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_Access()
		IL_015a: ldarga.s queries
		IL_015c: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
		IL_0161: ldloca.s 3
		IL_0163: ldloc.2
		IL_0164: ldarg.0
		IL_0165: ldfld valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess[] SomeEngine.Physics.CollisionPairBuffer::_fillAccesses
		IL_016a: ldc.i4.0
		IL_016b: ldc.i4.2
		IL_016c: ldloc.2
		IL_016d: mul
		IL_016e: call valuetype [System.Runtime]System.Span`1<!!0> [System.Memory]System.MemoryExtensions::AsSpan<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>(!!0[], int32, int32)
		IL_0173: call valuetype [System.Runtime]System.ReadOnlySpan`1<!0> valuetype [System.Runtime]System.Span`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>::op_Implicit(valuetype [System.Runtime]System.Span`1<!0>)
		IL_0178: ldc.i4.2
		IL_0179: ldloca.s 5
		IL_017b: initobj [SomeEngine.Job]SomeEngine.Job.JobHandle
		IL_0181: ldloc.s 5
		IL_0183: call valuetype [SomeEngine.Job]SomeEngine.Job.JobHandle [SomeEngine.ECS.Systems]SomeEngine.ECS.Systems.ReadOnlyQueryPairJobs::ExecuteParallel<valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!!TEnumerable>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, !!0&, int32, valuetype [System.Runtime]System.ReadOnlySpan`1<valuetype [SomeEngine.Job]SomeEngine.Job.JobResourceAccess>, int32, valuetype [SomeEngine.Job]SomeEngine.Job.JobHandle)
		IL_0188: pop
		IL_0189: ret

		IL_018a: ldarga.s queries
		IL_018c: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_Access()
		IL_0191: ldarga.s queries
		IL_0193: call instance valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor [SomeEngine.ECS.Relations]SomeEngine.ECS.Relations.RelationQueryViews::get_First()
		IL_0198: ldloca.s 3
		IL_019a: ldloc.2
		IL_019b: call void [SomeEngine.ECS.Systems]SomeEngine.ECS.Systems.ReadOnlyQueryPairJobs::ExecuteInline<valuetype SomeEngine.Physics.CollisionPairBuffer/FillJob`1<!!TEnumerable>>(valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, valuetype [SomeEngine.ECS]SomeEngine.ECS.Queries.QueryCursor, !!0&, int32)
		IL_01a0: ret
	} // end of method CollisionPairBuffer::Fill
END SELECTED METHOD Physics CollisionPairBuffer::Fill
