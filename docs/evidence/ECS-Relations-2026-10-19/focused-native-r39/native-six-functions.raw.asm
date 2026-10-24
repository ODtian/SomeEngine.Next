
benchmarks/SomeEngine.ECS.Relations.Benchmarks/obj/Release/net10.0/win-x64/native/SomeEngine.ECS.Relations.Benchmarks.obj:	file format coff-x86-64

Disassembly of section .managedcode$I:

000000000015e280 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  15e280:      	pushq	%r15
  15e282:      	pushq	%r14
  15e284:      	pushq	%r13
  15e286:      	pushq	%rdi
  15e287:      	pushq	%rsi
  15e288:      	pushq	%rbp
  15e289:      	pushq	%rbx
  15e28a:      	subq	$0x180, %rsp            # imm = 0x180
  15e291:      	xorps	%xmm4, %xmm4
  15e294:      	movaps	%xmm4, 0x20(%rsp)
  15e299:      	movabsq	$-0x150, %rax           # imm = 0xFEB0
  15e2a3:      	movaps	%xmm4, 0x180(%rsp,%rax)
  15e2ab:      	movaps	%xmm4, 0x190(%rsp,%rax)
  15e2b3:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  15e2bb:      	addq	$0x30, %rax
  15e2bf:      	jne	0x15e2a3 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x23>
  15e2c1:      	movq	%rdx, 0x1c8(%rsp)
  15e2c9:      	movq	%rcx, %rbx
  15e2cc:      	leaq	0x1c8(%rsp), %rcx
  15e2d4:      	leaq	0x20(%rsp), %rdx
  15e2d9:      	callq	0xb1d0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryCursor__get_Rows>
  15e2de:      	movups	0x20(%rsp), %xmm0
  15e2e3:      	movups	%xmm0, 0x60(%rsp)
  15e2e8:      	movups	0x30(%rsp), %xmm0
  15e2ed:      	movups	%xmm0, 0x70(%rsp)
  15e2f2:      	movups	0x40(%rsp), %xmm0
  15e2f7:      	movups	%xmm0, 0x80(%rsp)
  15e2ff:      	movups	0x50(%rsp), %xmm0
  15e304:      	movups	%xmm0, 0x90(%rsp)
  15e30c:      	xorps	%xmm0, %xmm0
  15e30f:      	movups	%xmm0, 0xa0(%rsp)
  15e317:      	xorps	%xmm0, %xmm0
  15e31a:      	movups	%xmm0, 0xb0(%rsp)
  15e322:      	leaq	0x120(%rsp), %rcx
  15e32a:      	leaq	0x60(%rsp), %rdx
  15e32f:      	movl	$0x60, %r8d
  15e335:      	callq	0x87e30 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  15e33a:      	movq	0x140(%rsp), %rdx
  15e342:      	movq	%rdx, %rsi
  15e345:      	leaq	0x120(%rsp), %r9
  15e34d:      	movq	0x138(%rsp), %rcx
  15e355:      	movl	0x148(%rsp), %r8d
  15e35d:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e362:      	testl	%eax, %eax
  15e364:      	jne	0x15e3af <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x12f>
  15e366:      	leaq	0x128(%rsp), %rcx
  15e36e:      	callq	0x154ae0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  15e373:      	testl	%eax, %eax
  15e375:      	je	0x15e5bb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x33b>
  15e37b:      	movl	$0xffffffff, 0x120(%rsp) # imm = 0xFFFFFFFF
  15e386:      	leaq	0x120(%rsp), %r9
  15e38e:      	movq	0x138(%rsp), %rcx
  15e396:      	movq	0x140(%rsp), %rdx
  15e39e:      	movl	0x148(%rsp), %r8d
  15e3a6:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e3ab:      	testl	%eax, %eax
  15e3ad:      	je	0x15e366 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0xe6>
  15e3af:      	movl	0x120(%rsp), %edi
  15e3b6:      	movq	0x140(%rsp), %rbp
  15e3be:      	cmpq	0x140(%rsp), %rsi
  15e3c6:      	je	0x15e3e5 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x165>
  15e3c8:      	leaq	0x120(%rsp), %rcx
  15e3d0:      	movq	0x138(%rsp), %rdx
  15e3d8:      	movq	0x140(%rsp), %r8
  15e3e0:      	callq	0x161240 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  15e3e5:      	cmpl	$0x0, 0x168(%rsp)
  15e3ed:      	jne	0x15e3f3 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x173>
  15e3ef:      	xorl	%esi, %esi
  15e3f1:      	jmp	0x15e40e <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x18e>
  15e3f3:      	cmpl	0x168(%rsp), %edi
  15e3fa:      	jae	0x15e5d1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x351>
  15e400:      	movl	%edi, %ecx
  15e402:      	imulq	$0x30, %rcx, %rsi
  15e406:      	addq	0x160(%rsp), %rsi
  15e40e:      	movq	0x8(%rbp), %rcx
  15e412:      	movq	0x8(%rcx), %rcx
  15e416:      	testq	%rcx, %rcx
  15e419:      	je	0x15e424 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1a4>
  15e41b:      	leaq	0x10(%rcx), %rdx
  15e41f:      	movl	0x8(%rcx), %ecx
  15e422:      	jmp	0x15e428 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1a8>
  15e424:      	xorl	%edx, %edx
  15e426:      	xorl	%ecx, %ecx
  15e428:      	cmpl	%ecx, %edi
  15e42a:      	jae	0x15e5d1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x351>
  15e430:      	movl	%edi, %ecx
  15e432:      	leaq	(%rdx,%rcx,8), %rcx
  15e436:      	movl	(%rcx), %edi
  15e438:      	movl	0x4(%rcx), %ebp
  15e43b:      	leaq	0xc0(%rsp), %rcx
  15e443:      	leaq	0x120(%rsp), %rdx
  15e44b:      	movl	$0x60, %r8d
  15e451:      	callq	0x87e30 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  15e456:      	jmp	0x15e4f6 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x276>
  15e45b:      	cmpl	0x108(%rsp), %r15d
  15e463:      	jae	0x15e5d1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x351>
  15e469:      	movl	%r15d, %eax
  15e46c:      	imulq	$0x30, %rax, %rax
  15e470:      	addq	0x100(%rsp), %rax
  15e478:      	cmpl	$0x0, (%rsi)
  15e47b:      	je	0x15e482 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x202>
  15e47d:      	cmpl	$0x0, (%rax)
  15e480:      	jne	0x15e4f6 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x276>
  15e482:      	movq	0x8(%r13), %rax
  15e486:      	movq	0x8(%rax), %rax
  15e48a:      	testq	%rax, %rax
  15e48d:      	je	0x15e498 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x218>
  15e48f:      	leaq	0x10(%rax), %rcx
  15e493:      	movl	0x8(%rax), %eax
  15e496:      	jmp	0x15e49c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x21c>
  15e498:      	xorl	%ecx, %ecx
  15e49a:      	xorl	%eax, %eax
  15e49c:      	cmpl	%eax, %r15d
  15e49f:      	jae	0x15e5d1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x351>
  15e4a5:      	movl	%r15d, %eax
  15e4a8:      	leaq	(%rcx,%rax,8), %rax
  15e4ac:      	movl	(%rax), %ecx
  15e4ae:      	movl	0x4(%rax), %eax
  15e4b1:      	cmpl	%ecx, %edi
  15e4b3:      	jl	0x15e4c5 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x245>
  15e4b5:      	cmpl	%ecx, %edi
  15e4b7:      	jne	0x15e5ad <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x32d>
  15e4bd:      	cmpl	%eax, %ebp
  15e4bf:      	jge	0x15e5ad <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x32d>
  15e4c5:      	movl	%edi, %edx
  15e4c7:      	movl	%ebp, %r8d
  15e4ca:      	movl	0x10(%rbx), %r10d
  15e4ce:      	leal	0x1(%r10), %r9d
  15e4d2:      	movl	%r9d, 0x10(%rbx)
  15e4d6:      	cmpl	0x8(%rbx), %r10d
  15e4da:      	jae	0x15e5d1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x351>
  15e4e0:      	shlq	$0x4, %r10
  15e4e4:      	addq	(%rbx), %r10
  15e4e7:      	movl	%edx, (%r10)
  15e4ea:      	movl	%r8d, 0x4(%r10)
  15e4ee:      	movl	%ecx, 0x8(%r10)
  15e4f2:      	movl	%eax, 0xc(%r10)
  15e4f6:      	movq	0xe0(%rsp), %rdx
  15e4fe:      	movq	%rdx, %r14
  15e501:      	leaq	0xc0(%rsp), %r9
  15e509:      	movq	0xd8(%rsp), %rcx
  15e511:      	movl	0xe8(%rsp), %r8d
  15e519:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e51e:      	testl	%eax, %eax
  15e520:      	jne	0x15e56b <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2eb>
  15e522:      	leaq	0xc8(%rsp), %rcx
  15e52a:      	callq	0x154ae0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  15e52f:      	testl	%eax, %eax
  15e531:      	je	0x15e33a <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0xba>
  15e537:      	movl	$0xffffffff, 0xc0(%rsp) # imm = 0xFFFFFFFF
  15e542:      	leaq	0xc0(%rsp), %r9
  15e54a:      	movq	0xd8(%rsp), %rcx
  15e552:      	movq	0xe0(%rsp), %rdx
  15e55a:      	movl	0xe8(%rsp), %r8d
  15e562:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e567:      	testl	%eax, %eax
  15e569:      	je	0x15e522 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2a2>
  15e56b:      	movl	0xc0(%rsp), %r15d
  15e573:      	movq	0xe0(%rsp), %r8
  15e57b:      	movq	%r8, %r13
  15e57e:      	cmpq	%r8, %r14
  15e581:      	je	0x15e598 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x318>
  15e583:      	leaq	0xc0(%rsp), %rcx
  15e58b:      	movq	0xd8(%rsp), %rdx
  15e593:      	callq	0x161240 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  15e598:      	cmpl	$0x0, 0x108(%rsp)
  15e5a0:      	jne	0x15e45b <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1db>
  15e5a6:      	xorl	%eax, %eax
  15e5a8:      	jmp	0x15e478 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1f8>
  15e5ad:      	movl	%ecx, %edx
  15e5af:      	movl	%eax, %r8d
  15e5b2:      	movl	%edi, %ecx
  15e5b4:      	movl	%ebp, %eax
  15e5b6:      	jmp	0x15e4ca <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x24a>
  15e5bb:      	movb	$0x1, 0x18(%rbx)
  15e5bf:      	addq	$0x180, %rsp            # imm = 0x180
  15e5c6:      	popq	%rbx
  15e5c7:      	popq	%rbp
  15e5c8:      	popq	%rsi
  15e5c9:      	popq	%rdi
  15e5ca:      	popq	%r13
  15e5cc:      	popq	%r14
  15e5ce:      	popq	%r15
  15e5d0:      	retq
  15e5d1:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  15e5d6:      	int3
  15e5d7:      	nop
  15e5d8:      	nop
  15e5d9:      	nop
  15e5da:      	nop
  15e5db:      	nop
  15e5dc:      	nop
  15e5dd:      	nop
  15e5de:      	nop
  15e5df:      	nop

00000000001604a0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  1604a0:      	pushq	%r15
  1604a2:      	pushq	%r14
  1604a4:      	pushq	%rdi
  1604a5:      	pushq	%rsi
  1604a6:      	pushq	%rbp
  1604a7:      	pushq	%rbx
  1604a8:      	subq	$0x38, %rsp
  1604ac:      	movq	%rcx, %rbx
  1604af:      	movq	%rdx, %rsi
  1604b2:      	movq	(%rbx), %rdi
  1604b5:      	movl	0x8(%rbx), %ebp
  1604b8:      	testl	%ebp, %ebp
  1604ba:      	jle	0x16052c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x8c>
  1604bc:      	xorl	%r14d, %r14d
  1604bf:      	movups	(%rdi,%r14), %xmm0
  1604c4:      	movups	%xmm0, 0x28(%rsp)
  1604c9:      	movq	%rsi, %rcx
  1604cc:      	movq	0x28(%rsp), %rdx
  1604d1:      	callq	0x1d95c0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1604d6:      	movq	%rax, %r15
  1604d9:      	testq	%r15, %r15
  1604dc:      	je	0x160524 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1604de:      	movq	%rsi, %rcx
  1604e1:      	movq	0x30(%rsp), %rdx
  1604e6:      	callq	0x1d95c0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1604eb:      	testq	%rax, %rax
  1604ee:      	je	0x160524 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1604f0:      	movq	0x8(%r15), %rcx
  1604f4:      	testq	%rcx, (%rax)
  1604f7:      	je	0x160524 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1604f9:      	movq	0x8(%rax), %rax
  1604fd:      	testq	%rax, (%r15)
  160500:      	je	0x160524 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  160502:      	leaq	0x10(%rbx), %rax
  160506:      	movl	0x10(%rax), %ecx
  160509:      	leal	0x1(%rcx), %edx
  16050c:      	movl	%edx, 0x10(%rax)
  16050f:      	cmpl	0x8(%rax), %ecx
  160512:      	jae	0x16053d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x9d>
  160514:      	movq	(%rax), %rax
  160517:      	shlq	$0x4, %rcx
  16051b:      	movups	0x28(%rsp), %xmm0
  160520:      	movups	%xmm0, (%rax,%rcx)
  160524:      	addq	$0x10, %r14
  160528:      	decl	%ebp
  16052a:      	jne	0x1604bf <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1f>
  16052c:      	movb	$0x1, 0x28(%rbx)
  160530:      	addq	$0x38, %rsp
  160534:      	popq	%rbx
  160535:      	popq	%rbp
  160536:      	popq	%rsi
  160537:      	popq	%rdi
  160538:      	popq	%r14
  16053a:      	popq	%r15
  16053c:      	retq
  16053d:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  160542:      	int3
  160543:      	nop
  160544:      	nop
  160545:      	nop
  160546:      	nop
  160547:      	nop
  160548:      	nop
  160549:      	nop
  16054a:      	nop
  16054b:      	nop
  16054c:      	nop
  16054d:      	nop
  16054e:      	nop
  16054f:      	nop

0000000000164bc0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  164bc0:      	pushq	%r15
  164bc2:      	pushq	%r14
  164bc4:      	pushq	%r13
  164bc6:      	pushq	%r12
  164bc8:      	pushq	%rdi
  164bc9:      	pushq	%rsi
  164bca:      	pushq	%rbp
  164bcb:      	pushq	%rbx
  164bcc:      	subq	$0x168, %rsp            # imm = 0x168
  164bd3:      	xorl	%eax, %eax
  164bd5:      	movq	%rax, 0x78(%rsp)
  164bda:      	xorps	%xmm4, %xmm4
  164bdd:      	movaps	%xmm4, 0x80(%rsp)
  164be5:      	movaps	%xmm4, 0x90(%rsp)
  164bed:      	movabsq	$-0xc0, %rax
  164bf7:      	movaps	%xmm4, 0x160(%rsp,%rax)
  164bff:      	movaps	%xmm4, 0x170(%rsp,%rax)
  164c07:      	movaps	%xmm4, 0x180(%rsp,%rax)
  164c0f:      	addq	$0x30, %rax
  164c13:      	jne	0x164bf7 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x37>
  164c15:      	movq	%rax, 0x160(%rsp)
  164c1d:      	movq	%rdx, 0x1b8(%rsp)
  164c25:      	movq	%rcx, %rbx
  164c28:      	leaq	(%rip), %rcx            # 0x164c2f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6f>
		0000000000164c2b:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_HashSet_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  164c2f:      	callq	0x164c34 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x74>
		0000000000164c30:  IMAGE_REL_AMD64_REL32	RhpNewFast
  164c34:      	movq	%rax, %rsi
  164c37:      	movq	%rsi, 0x38(%rsp)
  164c3c:      	leaq	(%rip), %rcx            # 0x164c43 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x83>
		0000000000164c3f:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  164c43:      	callq	0x164c48 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x88>
		0000000000164c44:  IMAGE_REL_AMD64_REL32	RhpNewFast
  164c48:      	movq	%rax, %rdi
  164c4b:      	leaq	(%rip), %rcx            # 0x164c52 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x92>
		0000000000164c4e:  IMAGE_REL_AMD64_REL32	__FrozenObj_S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>2
  164c52:      	movq	%rcx, 0x8(%rdi)
  164c56:      	leaq	0x1b8(%rsp), %rcx
  164c5e:      	leaq	0xc8(%rsp), %rdx
  164c66:      	callq	0xb1d0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryCursor__get_Rows>
  164c6b:      	movups	0xc8(%rsp), %xmm0
  164c73:      	movups	%xmm0, 0x108(%rsp)
  164c7b:      	movups	0xd8(%rsp), %xmm0
  164c83:      	movups	%xmm0, 0x118(%rsp)
  164c8b:      	movups	0xe8(%rsp), %xmm0
  164c93:      	movups	%xmm0, 0x128(%rsp)
  164c9b:      	movups	0xf8(%rsp), %xmm0
  164ca3:      	movups	%xmm0, 0x138(%rsp)
  164cab:      	jmp	0x164ccd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x10d>
  164cad:      	leaq	0x110(%rsp), %rcx
  164cb5:      	callq	0x154ae0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  164cba:      	testl	%eax, %eax
  164cbc:      	je	0x164ecb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x30b>
  164cc2:      	movl	$0xffffffff, 0x108(%rsp) # imm = 0xFFFFFFFF
  164ccd:      	leaq	0x108(%rsp), %r9
  164cd5:      	movq	0x120(%rsp), %rcx
  164cdd:      	movq	0x128(%rsp), %rdx
  164ce5:      	movl	0x130(%rsp), %r8d
  164ced:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  164cf2:      	testl	%eax, %eax
  164cf4:      	je	0x164cad <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0xed>
  164cf6:      	movl	0x108(%rsp), %ebp
  164cfd:      	movq	0x120(%rsp), %r14
  164d05:      	movq	0x128(%rsp), %r15
  164d0d:      	movq	%r15, %r13
  164d10:      	movl	%ebp, %r12d
  164d13:      	movq	0x30(%r14), %rax
  164d17:      	movq	%rax, 0x30(%rsp)
  164d1c:      	leaq	(%rip), %rcx            # 0x164d23 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x163>
		0000000000164d1f:  IMAGE_REL_AMD64_REL32	?__NONGCSTATICS@SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair>@@
  164d23:      	movq	%rcx, 0x40(%rsp)
  164d28:      	cmpq	$0x0, -0x8(%rcx)
  164d2d:      	jne	0x1652c0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x700>
  164d33:      	movq	0x40(%rsp), %rcx
  164d38:      	movq	%rcx, %r10
  164d3b:      	movq	%r10, 0x20(%rsp)
  164d40:      	movl	(%r10), %r8d
  164d43:      	movq	0x30(%rsp), %rax
  164d48:      	movq	0x8(%rax), %rcx
  164d4c:      	testq	%rcx, %rcx
  164d4f:      	je	0x164d5a <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x19a>
  164d51:      	leaq	0x10(%rcx), %rdx
  164d55:      	movl	0x8(%rcx), %eax
  164d58:      	jmp	0x164d5e <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x19e>
  164d5a:      	xorl	%edx, %edx
  164d5c:      	xorl	%eax, %eax
  164d5e:      	movq	%rdx, %rcx
  164d61:      	movl	%eax, %edx
  164d63:      	callq	0x1f8180 <S_P_CoreLib_System_SpanHelpers__BinarySearch_0<Int32__Int32>>
  164d68:      	testl	%eax, %eax
  164d6a:      	jge	0x164ddf <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x21f>
  164d6c:      	movq	0x8(%r15), %rdx
  164d70:      	movq	0x8(%rdx), %rdx
  164d74:      	testq	%rdx, %rdx
  164d77:      	je	0x164d82 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1c2>
  164d79:      	leaq	0x10(%rdx), %rcx
  164d7d:      	movl	0x8(%rdx), %edx
  164d80:      	jmp	0x164d86 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1c6>
  164d82:      	xorl	%ecx, %ecx
  164d84:      	xorl	%edx, %edx
  164d86:      	cmpl	%edx, %ebp
  164d88:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  164d8e:      	movl	%ebp, %edx
  164d90:      	leaq	(%rcx,%rdx,8), %rdx
  164d94:      	movl	(%rdx), %r14d
  164d97:      	movl	0x4(%rdx), %r13d
  164d9b:      	incl	0x14(%rdi)
  164d9e:      	movq	0x8(%rdi), %r12
  164da2:      	movl	0x10(%rdi), %ebp
  164da5:      	cmpl	%ebp, 0x8(%r12)
  164daa:      	ja	0x164dc6 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x206>
  164dac:      	movl	%r13d, %edx
  164daf:      	shlq	$0x20, %rdx
  164db3:      	movl	%r14d, %ecx
  164db6:      	orq	%rcx, %rdx
  164db9:      	movq	%rdi, %rcx
  164dbc:      	callq	0x159e80 <S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__AddWithResize>
  164dc1:      	jmp	0x164ccd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x10d>
  164dc6:      	leal	0x1(%rbp), %edx
  164dc9:      	movl	%edx, 0x10(%rdi)
  164dcc:      	movl	%ebp, %edx
  164dce:      	leaq	0x10(%r12,%rdx,8), %rdx
  164dd3:      	movl	%r14d, (%rdx)
  164dd6:      	movl	%r13d, 0x4(%rdx)
  164dda:      	jmp	0x164ccd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x10d>
  164ddf:      	movq	0x20(%rsp), %rbp
  164de4:      	movl	(%rbp), %edx
  164de7:      	movq	0x28(%r14), %r8
  164deb:      	xorl	%ecx, %ecx
  164ded:      	jmp	0x164df1 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x231>
  164def:      	incl	%ecx
  164df1:      	movq	%r8, %rax
  164df4:      	testq	%rax, %rax
  164df7:      	je	0x164dfe <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x23e>
  164df9:      	movl	0x8(%rax), %eax
  164dfc:      	jmp	0x164e00 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x240>
  164dfe:      	xorl	%eax, %eax
  164e00:      	cmpl	%eax, %ecx
  164e02:      	jge	0x1652ca <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x70a>
  164e08:      	movq	%r8, %rax
  164e0b:      	testq	%rax, %rax
  164e0e:      	je	0x164e19 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x259>
  164e10:      	leaq	0x10(%rax), %r10
  164e14:      	movl	0x8(%rax), %eax
  164e17:      	jmp	0x164e1e <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x25e>
  164e19:      	xorl	%r10d, %r10d
  164e1c:      	xorl	%eax, %eax
  164e1e:      	cmpl	%eax, %ecx
  164e20:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  164e26:      	leaq	(%rcx,%rcx,2), %rax
  164e2a:      	cmpl	%edx, (%r10,%rax,4)
  164e2e:      	jne	0x164def <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x22f>
  164e30:      	movl	%ecx, %edx
  164e32:      	testq	%r8, %r8
  164e35:      	je	0x164e41 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x281>
  164e37:      	leaq	0x10(%r8), %rcx
  164e3b:      	movl	0x8(%r8), %r8d
  164e3f:      	jmp	0x164e46 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x286>
  164e41:      	xorl	%ecx, %ecx
  164e43:      	xorl	%r8d, %r8d
  164e46:      	cmpl	%r8d, %edx
  164e49:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  164e4f:      	movl	%edx, %edx
  164e51:      	leaq	(%rdx,%rdx,2), %rdx
  164e55:      	leaq	(%rcx,%rdx,4), %rdx
  164e59:      	movl	0x4(%rdx), %r8d
  164e5d:      	movzbl	0x8(%rdx), %edx
  164e61:      	cmpl	$0x1, %edx
  164e64:      	je	0x164e6f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2af>
  164e66:      	cmpl	$0x3, %edx
  164e69:      	jne	0x1652d8 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x718>
  164e6f:      	movq	0x8(%r13), %rdx
  164e73:      	movq	0x10(%rdx), %rdx
  164e77:      	cmpl	0x8(%rdx), %r8d
  164e7b:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  164e81:      	movl	%r8d, %r8d
  164e84:      	movq	0x10(%rdx,%r8,8), %rdx
  164e89:      	testq	%rdx, %rdx
  164e8c:      	je	0x164e97 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2d7>
  164e8e:      	leaq	0x10(%rdx), %r8
  164e92:      	movl	0x8(%rdx), %edx
  164e95:      	jmp	0x164e9c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2dc>
  164e97:      	xorl	%r8d, %r8d
  164e9a:      	xorl	%edx, %edx
  164e9c:      	cmpl	%edx, %r12d
  164e9f:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  164ea5:      	movl	%r12d, %edx
  164ea8:      	leaq	(%r8,%rdx,8), %rdx
  164eac:      	movl	(%rdx), %r8d
  164eaf:      	movl	0x4(%rdx), %edx
  164eb2:      	shlq	$0x20, %rdx
  164eb6:      	orq	%r8, %rdx
  164eb9:      	leaq	0x78(%rsp), %r8
  164ebe:      	movq	%rsi, %rcx
  164ec1:      	callq	0x159ba0 <S_P_CoreLib_System_Collections_Generic_HashSet_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__AddIfNotPresent>
  164ec6:      	jmp	0x164ccd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x10d>
  164ecb:      	movl	0x34(%rsi), %ecx
  164ece:      	movq	%rsi, 0x150(%rsp)
  164ed6:      	movl	%ecx, 0x158(%rsp)
  164edd:      	xorl	%ecx, %ecx
  164edf:      	movl	%ecx, 0x15c(%rsp)
  164ee6:      	movq	%rcx, 0x160(%rsp)
  164eee:      	xorl	%ebp, %ebp
  164ef0:      	leaq	0x150(%rsp), %rcx
  164ef8:      	callq	0x159f60 <S_P_CoreLib_System_Collections_Generic_HashSet_1_Enumerator<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__MoveNext>
  164efd:      	movl	%eax, %r14d
  164f00:      	testl	%r14d, %r14d
  164f03:      	jne	0x164f17 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x357>
  164f05:      	testl	%ebp, %ebp
  164f07:      	jne	0x1651cb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x60b>
  164f0d:      	cmpl	$0x0, 0x10(%rdi)
  164f11:      	je	0x1651cb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x60b>
  164f17:      	testl	%r14d, %r14d
  164f1a:      	sete	%bpl
  164f1e:      	movzbl	%bpl, %ebp
  164f22:      	movl	%ebp, 0x14c(%rsp)
  164f29:      	testl	%r14d, %r14d
  164f2c:      	je	0x164f40 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x380>
  164f2e:      	movl	0x160(%rsp), %r15d
  164f36:      	movl	0x164(%rsp), %r13d
  164f3e:      	jmp	0x164f46 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x386>
  164f40:      	xorl	%r15d, %r15d
  164f43:      	xorl	%r13d, %r13d
  164f46:      	testl	%r14d, %r14d
  164f49:      	je	0x164f7b <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x3bb>
  164f4b:      	movq	(%rbx), %rcx
  164f4e:      	movl	%r13d, %r8d
  164f51:      	shlq	$0x20, %r8
  164f55:      	movl	%r15d, %edx
  164f58:      	orq	%rdx, %r8
  164f5b:      	leaq	0x90(%rsp), %rdx
  164f63:      	cmpl	%ecx, (%rcx)
  164f65:      	callq	0x1d8dd0 <SomeEngine_ECS_SomeEngine_ECS_World__GetByIndex<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>
  164f6a:      	movq	0x90(%rsp), %r12
  164f72:      	movl	0x98(%rsp), %eax
  164f79:      	jmp	0x164f91 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x3d1>
  164f7b:      	movl	0x10(%rdi), %eax
  164f7e:      	movq	0x8(%rdi), %r12
  164f82:      	cmpl	%eax, 0x8(%r12)
  164f87:      	jb	0x165316 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x756>
  164f8d:      	addq	$0x10, %r12
  164f91:      	movl	%eax, 0x6c(%rsp)
  164f95:      	movl	0x34(%rsi), %ecx
  164f98:      	movq	%rsi, 0xa8(%rsp)
  164fa0:      	movl	%ecx, 0xb0(%rsp)
  164fa7:      	xorl	%ecx, %ecx
  164fa9:      	movl	%ecx, 0xb4(%rsp)
  164fb0:      	movq	%rcx, 0xb8(%rsp)
  164fb8:      	movl	%ecx, 0xa4(%rsp)
  164fbf:      	jmp	0x16514c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x58c>
  164fc4:      	xorl	%r11d, %r11d
  164fc7:      	xorl	%edx, %edx
  164fc9:      	movl	%r11d, 0x68(%rsp)
  164fce:      	movl	%edx, 0x64(%rsp)
  164fd2:      	movl	%eax, 0xa0(%rsp)
  164fd9:      	testl	%eax, %eax
  164fdb:      	je	0x16500d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x44d>
  164fdd:      	movq	(%rbx), %rcx
  164fe0:      	movl	%edx, %r8d
  164fe3:      	shlq	$0x20, %r8
  164fe7:      	movl	%r11d, %ebp
  164fea:      	orq	%rbp, %r8
  164fed:      	leaq	0x80(%rsp), %rdx
  164ff5:      	cmpl	%ecx, (%rcx)
  164ff7:      	callq	0x1d8dd0 <SomeEngine_ECS_SomeEngine_ECS_World__GetByIndex<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>
  164ffc:      	movq	0x80(%rsp), %rbp
  165004:      	movl	0x88(%rsp), %eax
  16500b:      	jmp	0x165029 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x469>
  16500d:      	movl	0x10(%rdi), %r8d
  165011:      	movq	0x8(%rdi), %rcx
  165015:      	cmpl	%r8d, 0x8(%rcx)
  165019:      	jb	0x165316 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x756>
  16501f:      	addq	$0x10, %rcx
  165023:      	movl	%r8d, %eax
  165026:      	movq	%rcx, %rbp
  165029:      	movl	%r14d, 0xc4(%rsp)
  165031:      	movl	0xa0(%rsp), %r8d
  165039:      	testl	%r14d, %r8d
  16503c:      	jne	0x165043 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x483>
  16503e:      	xorl	%r8d, %r8d
  165041:      	jmp	0x16507a <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x4ba>
  165043:      	movl	%r15d, 0x74(%rsp)
  165048:      	movl	0x68(%rsp), %r11d
  16504d:      	cmpl	%r11d, %r15d
  165050:      	je	0x16505c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x49c>
  165052:      	xorl	%r8d, %r8d
  165055:      	movl	%r13d, 0x70(%rsp)
  16505a:      	jmp	0x165070 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x4b0>
  16505c:      	movl	%r13d, 0x70(%rsp)
  165061:      	movl	0x64(%rsp), %edx
  165065:      	cmpl	%edx, %r13d
  165068:      	sete	%r8b
  16506c:      	movzbl	%r8b, %r8d
  165070:      	movl	0x70(%rsp), %r13d
  165075:      	movl	0x74(%rsp), %r15d
  16507a:      	movzbl	0x8(%rbx), %ecx
  16507e:      	cmpl	%ecx, %r8d
  165081:      	movl	%eax, 0x60(%rsp)
  165085:      	movq	%rbp, 0x28(%rsp)
  16508a:      	movl	0xc4(%rsp), %r14d
  165092:      	je	0x16514c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x58c>
  165098:      	xorl	%ecx, %ecx
  16509a:      	movl	0x6c(%rsp), %eax
  16509e:      	cmpl	%eax, %ecx
  1650a0:      	jge	0x16514c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x58c>
  1650a6:      	movl	%r15d, 0x74(%rsp)
  1650ab:      	movl	%r13d, 0x70(%rsp)
  1650b0:      	movq	%rcx, 0x48(%rsp)
  1650b5:      	leaq	(%r12,%rcx,8), %r8
  1650b9:      	movl	(%r8), %r9d
  1650bc:      	movl	%r9d, 0x5c(%rsp)
  1650c1:      	movl	0x4(%r8), %r8d
  1650c5:      	movl	%r8d, 0x58(%rsp)
  1650ca:      	movq	0x1b8(%rsp), %rbp
  1650d2:      	movq	(%rbp), %rbp
  1650d6:      	movq	0x1b8(%rsp), %r13
  1650de:      	movq	0x8(%r13), %r13
  1650e2:      	movq	0x1b8(%rsp), %r15
  1650ea:      	movl	%r8d, %esi
  1650ed:      	shlq	$0x20, %rsi
  1650f1:      	movl	%r9d, %edx
  1650f4:      	movq	%rdx, %r8
  1650f7:      	orq	%rsi, %r8
  1650fa:      	movl	0x10(%r15), %r9d
  1650fe:      	movq	%rbp, %rcx
  165101:      	movq	%r13, %rdx
  165104:      	cmpl	%ecx, (%rcx)
  165106:      	callq	0x6a20 <SomeEngine_ECS_SomeEngine_ECS_World__ContainsEntityQuery>
  16510b:      	testl	%eax, %eax
  16510d:      	jne	0x1651b3 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x5f3>
  165113:      	movl	0x60(%rsp), %ebp
  165117:      	movq	0x28(%rsp), %r15
  16511c:      	movq	0x48(%rsp), %rsi
  165121:      	incl	%esi
  165123:      	movl	0x6c(%rsp), %r13d
  165128:      	cmpl	%r13d, %esi
  16512b:      	movl	%ebp, 0x60(%rsp)
  16512f:      	movq	%r15, 0x28(%rsp)
  165134:      	movl	%r13d, 0x6c(%rsp)
  165139:      	movq	%rsi, %rcx
  16513c:      	jl	0x1650b0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x4f0>
  165142:      	movl	0x70(%rsp), %r13d
  165147:      	movl	0x74(%rsp), %r15d
  16514c:      	leaq	0xa8(%rsp), %rcx
  165154:      	callq	0x159f60 <S_P_CoreLib_System_Collections_Generic_HashSet_1_Enumerator<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__MoveNext>
  165159:      	testl	%eax, %eax
  16515b:      	jne	0x165188 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x5c8>
  16515d:      	cmpl	$0x0, 0xa4(%rsp)
  165165:      	movl	0x14c(%rsp), %ebp
  16516c:      	movq	0x38(%rsp), %rsi
  165171:      	jne	0x164ef0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x330>
  165177:      	cmpl	$0x0, 0x10(%rdi)
  16517b:      	je	0x164ef0 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x330>
  165181:      	movl	%ebp, 0x14c(%rsp)
  165188:      	testl	%eax, %eax
  16518a:      	sete	%dl
  16518d:      	movzbl	%dl, %edx
  165190:      	movl	%edx, 0xa4(%rsp)
  165197:      	testl	%eax, %eax
  165199:      	je	0x164fc4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x404>
  16519f:      	movl	0xb8(%rsp), %r11d
  1651a7:      	movl	0xbc(%rsp), %edx
  1651ae:      	jmp	0x164fc9 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x409>
  1651b3:      	xorl	%esi, %esi
  1651b5:      	movl	0x60(%rsp), %ebp
  1651b9:      	cmpl	%ebp, %esi
  1651bb:      	jl	0x16527c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6bc>
  1651c1:      	movq	0x28(%rsp), %r15
  1651c6:      	jmp	0x16511c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x55c>
  1651cb:      	movb	$0x1, 0x9(%rbx)
  1651cf:      	addq	$0x168, %rsp            # imm = 0x168
  1651d6:      	popq	%rbx
  1651d7:      	popq	%rbp
  1651d8:      	popq	%rsi
  1651d9:      	popq	%rdi
  1651da:      	popq	%r12
  1651dc:      	popq	%r13
  1651de:      	popq	%r14
  1651e0:      	popq	%r15
  1651e2:      	retq
  1651e3:      	movl	%r14d, 0xc4(%rsp)
  1651eb:      	movq	0x1b8(%rsp), %r8
  1651f3:      	movq	(%r8), %rcx
  1651f6:      	movq	0x1b8(%rsp), %r8
  1651fe:      	movq	0x8(%r8), %rdx
  165202:      	movq	0x1b8(%rsp), %r9
  16520a:      	movl	%eax, %r8d
  16520d:      	shlq	$0x20, %r8
  165211:      	movl	%r13d, %r14d
  165214:      	orq	%r14, %r8
  165217:      	movl	0x10(%r9), %r9d
  16521b:      	cmpl	%ecx, (%rcx)
  16521d:      	callq	0x6a20 <SomeEngine_ECS_SomeEngine_ECS_World__ContainsEntityQuery>
  165222:      	testl	%eax, %eax
  165224:      	je	0x1652b6 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6f6>
  16522a:      	leaq	0x10(%rbx), %rax
  16522e:      	movl	0x10(%rax), %ecx
  165231:      	leal	0x1(%rcx), %edx
  165234:      	movl	%edx, 0x10(%rax)
  165237:      	cmpl	0x8(%rax), %ecx
  16523a:      	jae	0x16531c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x75c>
  165240:      	shlq	$0x4, %rcx
  165244:      	addq	(%rax), %rcx
  165247:      	movl	0x5c(%rsp), %r14d
  16524c:      	movl	%r14d, (%rcx)
  16524f:      	movl	0x58(%rsp), %r11d
  165254:      	movl	%r11d, 0x4(%rcx)
  165258:      	movl	%r13d, 0x8(%rcx)
  16525c:      	movl	0x54(%rsp), %r13d
  165261:      	movl	%r13d, 0xc(%rcx)
  165265:      	movl	0xc4(%rsp), %r14d
  16526d:      	incl	%esi
  16526f:      	cmpl	%ebp, %esi
  165271:      	jge	0x16511c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x55c>
  165277:      	movq	%r15, 0x28(%rsp)
  16527c:      	movq	0x28(%rsp), %r15
  165281:      	leaq	(%r15,%rsi,8), %r8
  165285:      	movl	(%r8), %r13d
  165288:      	movl	0x4(%r8), %eax
  16528c:      	movl	%eax, 0x54(%rsp)
  165290:      	movl	0x5c(%rsp), %r10d
  165295:      	cmpl	%r13d, %r10d
  165298:      	jl	0x1651e3 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x623>
  16529e:      	cmpl	%r13d, %r10d
  1652a1:      	jne	0x16526d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6ad>
  1652a3:      	cmpl	%eax, 0x58(%rsp)
  1652a7:      	jge	0x16526d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6ad>
  1652a9:      	movl	%r14d, 0xc4(%rsp)
  1652b1:      	jmp	0x1651eb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x62b>
  1652b6:      	movl	0xc4(%rsp), %r14d
  1652be:      	jmp	0x16526d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x6ad>
  1652c0:      	callq	0x1652c5 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x705>
		00000000001652c1:  IMAGE_REL_AMD64_REL32	__GetNonGCStaticBase_SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair>
  1652c5:      	jmp	0x164d33 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x173>
  1652ca:      	callq	0x1d7ad0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryAccessGuards__UndeclaredAccess<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair>>
  1652cf:      	movq	%rax, %rcx
  1652d2:      	callq	0x1652d7 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x717>
		00000000001652d3:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  1652d7:      	int3
  1652d8:      	leaq	(%rip), %rcx            # 0x1652df <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x71f>
		00000000001652db:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  1652df:      	callq	0x1652e4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x724>
		00000000001652e0:  IMAGE_REL_AMD64_REL32	RhpNewFast
  1652e4:      	movq	%rax, %rbx
  1652e7:      	leaq	(%rip), %rcx            # 0x1652ee <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x72e>
		00000000001652ea:  IMAGE_REL_AMD64_REL32	__RuntimeType_SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair
  1652ee:      	callq	0x8d220 <S_P_CoreLib_System_RuntimeType__get_Name>
  1652f3:      	movq	%rax, %rcx
  1652f6:      	leaq	(%rip), %rdx            # 0x1652fd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x73d>
		00000000001652f9:  IMAGE_REL_AMD64_REL32	__Str__was_not_declared_for_query_re_3DAC58C3D425A8E368C52F9A8DF72803BD0D416C884BD906DC32246D69AA8921
  1652fd:      	callq	0x8ecb0 <String__Concat_6>
  165302:      	movq	%rax, %rdx
  165305:      	movq	%rbx, %rcx
  165308:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  16530d:      	movq	%rbx, %rcx
  165310:      	callq	0x165315 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_HandwrittenKeyPairs_2_MemberExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x755>
		0000000000165311:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  165315:      	int3
  165316:      	callq	0xa5940 <S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException_ConcurrentOperationsNotSupported>
  16531b:      	int3
  16531c:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  165321:      	int3
  165322:      	nop
  165323:      	nop
  165324:      	nop
  165325:      	nop
  165326:      	nop
  165327:      	nop
  165328:      	nop
  165329:      	nop
  16532a:      	nop
  16532b:      	nop
  16532c:      	nop
  16532d:      	nop
  16532e:      	nop
  16532f:      	nop
  165330:      	nop
  165331:      	nop
  165332:      	nop
  165333:      	nop
  165334:      	nop
  165335:      	nop
  165336:      	nop
  165337:      	nop
  165338:      	nop
  165339:      	nop
  16533a:      	nop
  16533b:      	nop
  16533c:      	nop
  16533d:      	nop
  16533e:      	nop
  16533f:      	nop

00000000001d1660 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>:
  1d1660:      	pushq	%r15
  1d1662:      	pushq	%r14
  1d1664:      	pushq	%r13
  1d1666:      	pushq	%rdi
  1d1667:      	pushq	%rsi
  1d1668:      	pushq	%rbp
  1d1669:      	pushq	%rbx
  1d166a:      	subq	$0x180, %rsp            # imm = 0x180
  1d1671:      	xorps	%xmm4, %xmm4
  1d1674:      	movaps	%xmm4, 0x20(%rsp)
  1d1679:      	movabsq	$-0x150, %rax           # imm = 0xFEB0
  1d1683:      	movaps	%xmm4, 0x180(%rsp,%rax)
  1d168b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  1d1693:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  1d169b:      	addq	$0x30, %rax
  1d169f:      	jne	0x1d1683 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x23>
  1d16a1:      	movq	%rcx, 0x1c0(%rsp)
  1d16a9:      	movq	%r8, %rbx
  1d16ac:      	leaq	0x1c0(%rsp), %rcx
  1d16b4:      	leaq	0x20(%rsp), %rdx
  1d16b9:      	callq	0xb1d0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryCursor__get_Rows>
  1d16be:      	movups	0x20(%rsp), %xmm0
  1d16c3:      	movups	%xmm0, 0x60(%rsp)
  1d16c8:      	movups	0x30(%rsp), %xmm0
  1d16cd:      	movups	%xmm0, 0x70(%rsp)
  1d16d2:      	movups	0x40(%rsp), %xmm0
  1d16d7:      	movups	%xmm0, 0x80(%rsp)
  1d16df:      	movups	0x50(%rsp), %xmm0
  1d16e4:      	movups	%xmm0, 0x90(%rsp)
  1d16ec:      	xorps	%xmm0, %xmm0
  1d16ef:      	movups	%xmm0, 0xa0(%rsp)
  1d16f7:      	xorps	%xmm0, %xmm0
  1d16fa:      	movups	%xmm0, 0xb0(%rsp)
  1d1702:      	leaq	0x120(%rsp), %rcx
  1d170a:      	leaq	0x60(%rsp), %rdx
  1d170f:      	movl	$0x60, %r8d
  1d1715:      	callq	0x87e30 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  1d171a:      	movq	0x140(%rsp), %rsi
  1d1722:      	leaq	0x120(%rsp), %r9
  1d172a:      	movq	0x138(%rsp), %rcx
  1d1732:      	movq	0x140(%rsp), %rdx
  1d173a:      	movl	0x148(%rsp), %r8d
  1d1742:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d1747:      	testl	%eax, %eax
  1d1749:      	jne	0x1d1794 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x134>
  1d174b:      	leaq	0x128(%rsp), %rcx
  1d1753:      	callq	0x154ae0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  1d1758:      	testl	%eax, %eax
  1d175a:      	je	0x1d19a0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x340>
  1d1760:      	movl	$0xffffffff, 0x120(%rsp) # imm = 0xFFFFFFFF
  1d176b:      	leaq	0x120(%rsp), %r9
  1d1773:      	movq	0x138(%rsp), %rcx
  1d177b:      	movq	0x140(%rsp), %rdx
  1d1783:      	movl	0x148(%rsp), %r8d
  1d178b:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d1790:      	testl	%eax, %eax
  1d1792:      	je	0x1d174b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0xeb>
  1d1794:      	movl	0x120(%rsp), %edi
  1d179b:      	movq	0x140(%rsp), %rbp
  1d17a3:      	cmpq	0x140(%rsp), %rsi
  1d17ab:      	je	0x1d17ca <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x16a>
  1d17ad:      	leaq	0x120(%rsp), %rcx
  1d17b5:      	movq	0x138(%rsp), %rdx
  1d17bd:      	movq	0x140(%rsp), %r8
  1d17c5:      	callq	0x161240 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  1d17ca:      	cmpl	$0x0, 0x168(%rsp)
  1d17d2:      	jne	0x1d17d8 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x178>
  1d17d4:      	xorl	%esi, %esi
  1d17d6:      	jmp	0x1d17f3 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x193>
  1d17d8:      	cmpl	0x168(%rsp), %edi
  1d17df:      	jae	0x1d19b7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x357>
  1d17e5:      	movl	%edi, %ecx
  1d17e7:      	imulq	$0x30, %rcx, %rsi
  1d17eb:      	addq	0x160(%rsp), %rsi
  1d17f3:      	movq	0x8(%rbp), %rcx
  1d17f7:      	movq	0x8(%rcx), %rcx
  1d17fb:      	testq	%rcx, %rcx
  1d17fe:      	je	0x1d1809 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1a9>
  1d1800:      	leaq	0x10(%rcx), %rdx
  1d1804:      	movl	0x8(%rcx), %ecx
  1d1807:      	jmp	0x1d180d <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1ad>
  1d1809:      	xorl	%edx, %edx
  1d180b:      	xorl	%ecx, %ecx
  1d180d:      	cmpl	%ecx, %edi
  1d180f:      	jae	0x1d19b7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x357>
  1d1815:      	movl	%edi, %ecx
  1d1817:      	leaq	(%rdx,%rcx,8), %rcx
  1d181b:      	movl	(%rcx), %edi
  1d181d:      	movl	0x4(%rcx), %ebp
  1d1820:      	leaq	0xc0(%rsp), %rcx
  1d1828:      	leaq	0x120(%rsp), %rdx
  1d1830:      	movl	$0x60, %r8d
  1d1836:      	callq	0x87e30 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  1d183b:      	jmp	0x1d18db <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x27b>
  1d1840:      	cmpl	0x108(%rsp), %r15d
  1d1848:      	jae	0x1d19b7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x357>
  1d184e:      	movl	%r15d, %eax
  1d1851:      	imulq	$0x30, %rax, %rax
  1d1855:      	addq	0x100(%rsp), %rax
  1d185d:      	cmpl	$0x0, (%rsi)
  1d1860:      	je	0x1d1867 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x207>
  1d1862:      	cmpl	$0x0, (%rax)
  1d1865:      	jne	0x1d18db <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x27b>
  1d1867:      	movq	0x8(%r13), %rax
  1d186b:      	movq	0x8(%rax), %rax
  1d186f:      	testq	%rax, %rax
  1d1872:      	je	0x1d187d <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x21d>
  1d1874:      	leaq	0x10(%rax), %rcx
  1d1878:      	movl	0x8(%rax), %eax
  1d187b:      	jmp	0x1d1881 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x221>
  1d187d:      	xorl	%ecx, %ecx
  1d187f:      	xorl	%eax, %eax
  1d1881:      	cmpl	%eax, %r15d
  1d1884:      	jae	0x1d19b7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x357>
  1d188a:      	movl	%r15d, %eax
  1d188d:      	leaq	(%rcx,%rax,8), %rax
  1d1891:      	movl	(%rax), %ecx
  1d1893:      	movl	0x4(%rax), %eax
  1d1896:      	cmpl	%ecx, %edi
  1d1898:      	jl	0x1d18aa <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x24a>
  1d189a:      	cmpl	%ecx, %edi
  1d189c:      	jne	0x1d1992 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x332>
  1d18a2:      	cmpl	%eax, %ebp
  1d18a4:      	jge	0x1d1992 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x332>
  1d18aa:      	movl	%edi, %edx
  1d18ac:      	movl	%ebp, %r8d
  1d18af:      	movl	0x10(%rbx), %r10d
  1d18b3:      	leal	0x1(%r10), %r9d
  1d18b7:      	movl	%r9d, 0x10(%rbx)
  1d18bb:      	cmpl	0x8(%rbx), %r10d
  1d18bf:      	jae	0x1d19b7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x357>
  1d18c5:      	shlq	$0x4, %r10
  1d18c9:      	addq	(%rbx), %r10
  1d18cc:      	movl	%edx, (%r10)
  1d18cf:      	movl	%r8d, 0x4(%r10)
  1d18d3:      	movl	%ecx, 0x8(%r10)
  1d18d7:      	movl	%eax, 0xc(%r10)
  1d18db:      	movq	0xe0(%rsp), %rdx
  1d18e3:      	movq	%rdx, %r14
  1d18e6:      	leaq	0xc0(%rsp), %r9
  1d18ee:      	movq	0xd8(%rsp), %rcx
  1d18f6:      	movl	0xe8(%rsp), %r8d
  1d18fe:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d1903:      	testl	%eax, %eax
  1d1905:      	jne	0x1d1950 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2f0>
  1d1907:      	leaq	0xc8(%rsp), %rcx
  1d190f:      	callq	0x154ae0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  1d1914:      	testl	%eax, %eax
  1d1916:      	je	0x1d171a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0xba>
  1d191c:      	movl	$0xffffffff, 0xc0(%rsp) # imm = 0xFFFFFFFF
  1d1927:      	leaq	0xc0(%rsp), %r9
  1d192f:      	movq	0xd8(%rsp), %rcx
  1d1937:      	movq	0xe0(%rsp), %rdx
  1d193f:      	movl	0xe8(%rsp), %r8d
  1d1947:      	callq	0xd7e0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d194c:      	testl	%eax, %eax
  1d194e:      	je	0x1d1907 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2a7>
  1d1950:      	movl	0xc0(%rsp), %r15d
  1d1958:      	movq	0xe0(%rsp), %r8
  1d1960:      	movq	%r8, %r13
  1d1963:      	cmpq	%r8, %r14
  1d1966:      	je	0x1d197d <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x31d>
  1d1968:      	leaq	0xc0(%rsp), %rcx
  1d1970:      	movq	0xd8(%rsp), %rdx
  1d1978:      	callq	0x161240 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  1d197d:      	cmpl	$0x0, 0x108(%rsp)
  1d1985:      	jne	0x1d1840 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1e0>
  1d198b:      	xorl	%eax, %eax
  1d198d:      	jmp	0x1d185d <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1fd>
  1d1992:      	movl	%ecx, %edx
  1d1994:      	movl	%eax, %r8d
  1d1997:      	movl	%edi, %ecx
  1d1999:      	movl	%ebp, %eax
  1d199b:      	jmp	0x1d18af <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x24f>
  1d19a0:      	movl	$0x1, %eax
  1d19a5:      	addq	$0x180, %rsp            # imm = 0x180
  1d19ac:      	popq	%rbx
  1d19ad:      	popq	%rbp
  1d19ae:      	popq	%rsi
  1d19af:      	popq	%rdi
  1d19b0:      	popq	%r13
  1d19b2:      	popq	%r14
  1d19b4:      	popq	%r15
  1d19b6:      	retq
  1d19b7:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  1d19bc:      	int3
  1d19bd:      	nop
  1d19be:      	nop
  1d19bf:      	nop

0000000000207e20 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>:
  207e20:      	pushq	%r15
  207e22:      	pushq	%r14
  207e24:      	pushq	%r13
  207e26:      	pushq	%r12
  207e28:      	pushq	%rdi
  207e29:      	pushq	%rsi
  207e2a:      	pushq	%rbp
  207e2b:      	pushq	%rbx
  207e2c:      	subq	$0xf8, %rsp
  207e33:      	xorl	%eax, %eax
  207e35:      	movq	%rax, 0x78(%rsp)
  207e3a:      	xorps	%xmm4, %xmm4
  207e3d:      	movaps	%xmm4, 0x80(%rsp)
  207e45:      	movabsq	$-0x60, %rax
  207e4f:      	movaps	%xmm4, 0xf0(%rsp,%rax)
  207e57:      	movaps	%xmm4, 0x100(%rsp,%rax)
  207e5f:      	movaps	%xmm4, 0x110(%rsp,%rax)
  207e67:      	addq	$0x30, %rax
  207e6b:      	jne	0x207e4f <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2f>
  207e6d:      	movq	%rax, 0xf0(%rsp)
  207e75:      	movq	%rcx, %rbp
  207e78:      	movl	%edx, %esi
  207e7a:      	movq	%r9, %rdi
  207e7d:      	movq	0x178(%rsp), %rbx
  207e85:      	leaq	(%rip), %rcx            # 0x207e8c <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x6c>
		0000000000207e88:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_HashSet_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  207e8c:      	callq	0x207e91 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x71>
		0000000000207e8d:  IMAGE_REL_AMD64_REL32	RhpNewFast
  207e91:      	movq	%rax, %r14
  207e94:      	leaq	(%rip), %rcx            # 0x207e9b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x7b>
		0000000000207e97:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  207e9b:      	callq	0x207ea0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x80>
		0000000000207e9c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  207ea0:      	movq	%rax, %r15
  207ea3:      	leaq	(%rip), %rcx            # 0x207eaa <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x8a>
		0000000000207ea6:  IMAGE_REL_AMD64_REL32	__FrozenObj_S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>2
  207eaa:      	movq	%rcx, 0x8(%r15)
  207eae:      	leaq	0x160(%rsp), %rcx
  207eb6:      	movq	%r14, %rdx
  207eb9:      	movq	%r15, %r8
  207ebc:      	callq	0x1643e0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyGroups_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__CollectKeys>
  207ec1:      	movq	%rbp, 0x48(%rsp)
  207ec6:      	movq	%r14, 0x40(%rsp)
  207ecb:      	movq	%r15, 0x38(%rsp)
  207ed0:      	movl	0x34(%r14), %ecx
  207ed4:      	movl	0x10(%r15), %eax
  207ed8:      	movq	0x8(%r15), %rdx
  207edc:      	cmpl	%eax, 0x8(%rdx)
  207edf:      	jb	0x20830a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4ea>
  207ee5:      	addq	$0x10, %rdx
  207ee9:      	movq	%rbp, 0xc8(%rsp)
  207ef1:      	movq	%r14, 0xd0(%rsp)
  207ef9:      	movl	%ecx, 0xd8(%rsp)
  207f00:      	xorl	%ecx, %ecx
  207f02:      	movl	%ecx, 0xdc(%rsp)
  207f09:      	movq	%rdx, 0xe8(%rsp)
  207f11:      	movl	%eax, 0xf0(%rsp)
  207f18:      	movq	%rcx, 0xe0(%rsp)
  207f20:      	jmp	0x207f27 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x107>
  207f22:      	movq	0x48(%rsp), %rbp
  207f27:      	leaq	0xd0(%rsp), %rcx
  207f2f:      	callq	0x159f60 <S_P_CoreLib_System_Collections_Generic_HashSet_1_Enumerator<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__MoveNext>
  207f34:      	testl	%eax, %eax
  207f36:      	jne	0x207f80 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x160>
  207f38:      	cmpl	$0x0, 0xf0(%rsp)
  207f40:      	je	0x208245 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x425>
  207f46:      	movq	0xe8(%rsp), %r13
  207f4e:      	movl	0xf0(%rsp), %r12d
  207f56:      	xorl	%eax, %eax
  207f58:      	xorl	%r10d, %r10d
  207f5b:      	xorl	%r9d, %r9d
  207f5e:      	xorps	%xmm0, %xmm0
  207f61:      	movups	%xmm0, 0xe8(%rsp)
  207f69:      	movl	%r12d, 0x60(%rsp)
  207f6e:      	movq	%r13, 0x30(%rsp)
  207f73:      	movl	%r9d, 0x5c(%rsp)
  207f78:      	movl	%r10d, %r12d
  207f7b:      	movl	%eax, %r13d
  207f7e:      	jmp	0x207fd4 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1b4>
  207f80:      	movl	0xe0(%rsp), %r13d
  207f88:      	movl	0xe4(%rsp), %r12d
  207f90:      	movl	%r12d, %r8d
  207f93:      	shlq	$0x20, %r8
  207f97:      	movl	%r13d, %edx
  207f9a:      	orq	%rdx, %r8
  207f9d:      	leaq	0x88(%rsp), %rdx
  207fa5:      	movq	0xc8(%rsp), %rcx
  207fad:      	cmpl	%ecx, (%rcx)
  207faf:      	callq	0x1d8dd0 <SomeEngine_ECS_SomeEngine_ECS_World__GetByIndex<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>
  207fb4:      	movq	0x88(%rsp), %rcx
  207fbc:      	movl	0x90(%rsp), %eax
  207fc3:      	movq	%rcx, 0x30(%rsp)
  207fc8:      	movl	%eax, 0x60(%rsp)
  207fcc:      	movl	$0x1, 0x5c(%rsp)
  207fd4:      	movl	0x34(%r14), %ecx
  207fd8:      	movl	0x10(%r15), %r10d
  207fdc:      	movq	0x8(%r15), %r9
  207fe0:      	cmpl	%r10d, 0x8(%r9)
  207fe4:      	jb	0x20830a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4ea>
  207fea:      	addq	$0x10, %r9
  207fee:      	movq	%rbp, 0x98(%rsp)
  207ff6:      	movq	%r14, 0xa0(%rsp)
  207ffe:      	movl	%ecx, 0xa8(%rsp)
  208005:      	xorl	%ecx, %ecx
  208007:      	movl	%ecx, 0xac(%rsp)
  20800e:      	movq	%r9, 0xb8(%rsp)
  208016:      	movl	%r10d, 0xc0(%rsp)
  20801e:      	movq	%rcx, 0xb0(%rsp)
  208026:      	jmp	0x2081cf <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x3af>
  20802b:      	movl	0xb0(%rsp), %r11d
  208033:      	movl	%r11d, %edx
  208036:      	movl	%edx, 0x70(%rsp)
  20803a:      	movl	0xb4(%rsp), %r11d
  208042:      	movl	%r11d, %r8d
  208045:      	movl	%r8d, 0x6c(%rsp)
  20804a:      	movl	%r8d, %ecx
  20804d:      	shlq	$0x20, %rcx
  208051:      	movl	%edx, %r11d
  208054:      	movq	%r11, %r8
  208057:      	orq	%rcx, %r8
  20805a:      	leaq	0x78(%rsp), %rdx
  20805f:      	movq	0x98(%rsp), %rcx
  208067:      	cmpl	%ecx, (%rcx)
  208069:      	callq	0x1d8dd0 <SomeEngine_ECS_SomeEngine_ECS_World__GetByIndex<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>
  20806e:      	movq	0x78(%rsp), %r11
  208073:      	movl	0x80(%rsp), %ecx
  20807a:      	movl	0x70(%rsp), %edx
  20807e:      	movl	0x6c(%rsp), %r8d
  208083:      	movq	%r11, %rax
  208086:      	movl	%ecx, %r10d
  208089:      	movl	$0x1, %r9d
  20808f:      	movl	0x5c(%rsp), %r11d
  208094:      	testl	%r11d, %r11d
  208097:      	je	0x20809e <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x27e>
  208099:      	testb	%r9b, %r9b
  20809c:      	jne	0x2080a3 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x283>
  20809e:      	xorl	%r9d, %r9d
  2080a1:      	jmp	0x2080e4 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2c4>
  2080a3:      	movq	%rdi, 0x158(%rsp)
  2080ab:      	cmpb	%dil, (%rdi)
  2080ae:      	movl	%r13d, 0x68(%rsp)
  2080b3:      	cmpl	%edx, %r13d
  2080b6:      	je	0x2080c2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2a2>
  2080b8:      	xorl	%r9d, %r9d
  2080bb:      	movl	%r12d, 0x64(%rsp)
  2080c0:      	jmp	0x2080d2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2b2>
  2080c2:      	movl	%r12d, 0x64(%rsp)
  2080c7:      	cmpl	%r8d, %r12d
  2080ca:      	sete	%r9b
  2080ce:      	movzbl	%r9b, %r9d
  2080d2:      	movq	0x158(%rsp), %rdi
  2080da:      	movl	0x64(%rsp), %r12d
  2080df:      	movl	0x68(%rsp), %r13d
  2080e4:      	movzbl	%r9b, %r8d
  2080e8:      	movzbl	%sil, %r9d
  2080ec:      	cmpl	%r9d, %r8d
  2080ef:      	setne	%r8b
  2080f3:      	movzbl	%r8b, %r8d
  2080f7:      	testl	%r8d, %r8d
  2080fa:      	movl	%r10d, 0x58(%rsp)
  2080ff:      	movq	%rax, 0x28(%rsp)
  208104:      	je	0x2081cf <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x3af>
  20810a:      	xorl	%ecx, %ecx
  20810c:      	movl	0x60(%rsp), %eax
  208110:      	cmpl	%eax, %ecx
  208112:      	jge	0x2081cf <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x3af>
  208118:      	movq	%rdi, 0x158(%rsp)
  208120:      	movl	%r13d, 0x68(%rsp)
  208125:      	movl	%r12d, 0x64(%rsp)
  20812a:      	movq	0x30(%rsp), %rdx
  20812f:      	movq	%rcx, 0x50(%rsp)
  208134:      	leaq	(%rdx,%rcx,8), %r9
  208138:      	movl	(%r9), %ebp
  20813b:      	movl	0x4(%r9), %r9d
  20813f:      	movl	%r9d, 0x74(%rsp)
  208144:      	movq	0x160(%rsp), %r15
  20814c:      	movq	(%r15), %r15
  20814f:      	movq	0x160(%rsp), %r14
  208157:      	movq	0x8(%r14), %r14
  20815b:      	movq	0x160(%rsp), %rdi
  208163:      	movl	%r9d, %r12d
  208166:      	shlq	$0x20, %r12
  20816a:      	movl	%ebp, %r13d
  20816d:      	movq	%r13, %r8
  208170:      	orq	%r12, %r8
  208173:      	movl	0x10(%rdi), %r9d
  208177:      	movq	%r15, %rcx
  20817a:      	movq	%r14, %rdx
  20817d:      	cmpl	%ecx, (%rcx)
  20817f:      	callq	0x6a20 <SomeEngine_ECS_SomeEngine_ECS_World__ContainsEntityQuery>
  208184:      	testl	%eax, %eax
  208186:      	jne	0x20822b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x40b>
  20818c:      	movl	0x58(%rsp), %r14d
  208191:      	movq	0x28(%rsp), %r15
  208196:      	movq	0x50(%rsp), %rdi
  20819b:      	incl	%edi
  20819d:      	movl	0x60(%rsp), %r12d
  2081a2:      	cmpl	%r12d, %edi
  2081a5:      	movl	%r14d, 0x58(%rsp)
  2081aa:      	movq	%r15, 0x28(%rsp)
  2081af:      	movl	%r12d, 0x60(%rsp)
  2081b4:      	movq	%rdi, %rcx
  2081b7:      	jl	0x20812a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x30a>
  2081bd:      	movq	0x158(%rsp), %rdi
  2081c5:      	movl	0x64(%rsp), %r12d
  2081ca:      	movl	0x68(%rsp), %r13d
  2081cf:      	leaq	0xa0(%rsp), %rcx
  2081d7:      	callq	0x159f60 <S_P_CoreLib_System_Collections_Generic_HashSet_1_Enumerator<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__MoveNext>
  2081dc:      	testl	%eax, %eax
  2081de:      	jne	0x20802b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x20b>
  2081e4:      	cmpl	$0x0, 0xc0(%rsp)
  2081ec:      	movq	0x40(%rsp), %r14
  2081f1:      	movq	0x38(%rsp), %r15
  2081f6:      	je	0x207f22 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x102>
  2081fc:      	movq	0xb8(%rsp), %r11
  208204:      	movl	0xc0(%rsp), %ecx
  20820b:      	xorl	%edx, %edx
  20820d:      	xorl	%r8d, %r8d
  208210:      	xorl	%ebp, %ebp
  208212:      	xorps	%xmm0, %xmm0
  208215:      	movups	%xmm0, 0xb8(%rsp)
  20821d:      	movq	%r11, %rax
  208220:      	movl	%ebp, %r9d
  208223:      	movl	%ecx, %r10d
  208226:      	jmp	0x20808f <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x26f>
  20822b:      	xorl	%edi, %edi
  20822d:      	movl	0x58(%rsp), %r14d
  208232:      	cmpl	%r14d, %edi
  208235:      	jl	0x2082e2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4c2>
  20823b:      	movq	0x28(%rsp), %r15
  208240:      	jmp	0x208196 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x376>
  208245:      	movl	$0x1, %eax
  20824a:      	addq	$0xf8, %rsp
  208251:      	popq	%rbx
  208252:      	popq	%rbp
  208253:      	popq	%rsi
  208254:      	popq	%rdi
  208255:      	popq	%r12
  208257:      	popq	%r13
  208259:      	popq	%r14
  20825b:      	popq	%r15
  20825d:      	retq
  20825e:      	cmpl	%r12d, 0x74(%rsp)
  208263:      	jl	0x20826b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x44b>
  208265:      	movl	0x74(%rsp), %ecx
  208269:      	jmp	0x2082d2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4b2>
  20826b:      	movq	0x160(%rsp), %r8
  208273:      	movq	(%r8), %rcx
  208276:      	movq	0x160(%rsp), %r8
  20827e:      	movq	0x8(%r8), %rdx
  208282:      	movq	0x160(%rsp), %r9
  20828a:      	movl	%r12d, %r8d
  20828d:      	shlq	$0x20, %r8
  208291:      	movl	%r13d, %r10d
  208294:      	orq	%r10, %r8
  208297:      	movl	0x10(%r9), %r9d
  20829b:      	cmpl	%ecx, (%rcx)
  20829d:      	callq	0x6a20 <SomeEngine_ECS_SomeEngine_ECS_World__ContainsEntityQuery>
  2082a2:      	testl	%eax, %eax
  2082a4:      	jne	0x2082ac <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x48c>
  2082a6:      	movl	0x74(%rsp), %ecx
  2082aa:      	jmp	0x2082d2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4b2>
  2082ac:      	movl	0x10(%rbx), %eax
  2082af:      	leal	0x1(%rax), %ecx
  2082b2:      	movl	%ecx, 0x10(%rbx)
  2082b5:      	cmpl	0x8(%rbx), %eax
  2082b8:      	jae	0x208310 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4f0>
  2082ba:      	shlq	$0x4, %rax
  2082be:      	addq	(%rbx), %rax
  2082c1:      	movl	%ebp, (%rax)
  2082c3:      	movl	0x74(%rsp), %ecx
  2082c7:      	movl	%ecx, 0x4(%rax)
  2082ca:      	movl	%r13d, 0x8(%rax)
  2082ce:      	movl	%r12d, 0xc(%rax)
  2082d2:      	incl	%edi
  2082d4:      	cmpl	%r14d, %edi
  2082d7:      	jge	0x208196 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x376>
  2082dd:      	movq	%r15, 0x28(%rsp)
  2082e2:      	movq	0x28(%rsp), %r15
  2082e7:      	leaq	(%r15,%rdi,8), %r8
  2082eb:      	movl	(%r8), %r13d
  2082ee:      	movl	0x4(%r8), %r12d
  2082f2:      	cmpl	%r13d, %ebp
  2082f5:      	jl	0x20826b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x44b>
  2082fb:      	cmpl	%r13d, %ebp
  2082fe:      	je	0x20825e <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x43e>
  208304:      	movl	0x74(%rsp), %ecx
  208308:      	jmp	0x2082d2 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairs_5<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_KeyPairsWithin__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x4b2>
  20830a:      	callq	0xa5940 <S_P_CoreLib_System_ThrowHelper__ThrowInvalidOperationException_ConcurrentOperationsNotSupported>
  20830f:      	int3
  208310:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  208315:      	int3
  208316:      	nop
  208317:      	nop
  208318:      	nop
  208319:      	nop
  20831a:      	nop
  20831b:      	nop
  20831c:      	nop
  20831d:      	nop
  20831e:      	nop
  20831f:      	nop

0000000000221cc0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>>:
  221cc0:      	pushq	%rbp
  221cc1:      	pushq	%r15
  221cc3:      	pushq	%r14
  221cc5:      	pushq	%r13
  221cc7:      	pushq	%rdi
  221cc8:      	pushq	%rsi
  221cc9:      	pushq	%rbx
  221cca:      	subq	$0xa0, %rsp
  221cd1:      	leaq	0xd0(%rsp), %rbp
  221cd9:      	xorps	%xmm4, %xmm4
  221cdc:      	movabsq	$-0x60, %rax
  221ce6:      	movaps	%xmm4, -0x30(%rbp,%rax)
  221ceb:      	movaps	%xmm4, -0x20(%rbp,%rax)
  221cf0:      	movaps	%xmm4, -0x10(%rbp,%rax)
  221cf5:      	addq	$0x30, %rax
  221cf9:      	jne	0x221ce6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x26>
  221cfb:      	movq	%rcx, 0x10(%rbp)
  221cff:      	movq	%rdx, 0x18(%rbp)
  221d03:      	movl	%r8d, %ebx
  221d06:      	movl	%r9d, %esi
  221d09:      	movl	0x30(%rbp), %eax
  221d0c:      	movq	0x38(%rbp), %rdi
  221d10:      	testb	%al, %al
  221d12:      	jne	0x221d35 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x75>
  221d14:      	movq	0x38(%rcx), %rdx
  221d18:      	cmpl	$0x0, 0x10(%rdx)
  221d1c:      	jne	0x221f7b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2bb>
  221d22:      	leaq	0x14(%rdx), %r8
  221d26:      	lock
  221d27:      	incl	(%r8)
  221d2a:      	cmpl	$0x0, 0x10(%rdx)
  221d2e:      	je	0x221d97 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xd7>
  221d30:      	jmp	0x221fa2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2e2>
  221d35:      	movq	0x38(%rcx), %r14
  221d39:      	cmpb	%r14b, (%r14)
  221d3c:      	callq	0xc7a80 <S_P_CoreLib_System_Threading_ManagedThreadId__get_Current>
  221d41:      	movl	%eax, %ecx
  221d43:      	movl	0x10(%r14), %edx
  221d47:      	testl	%edx, %edx
  221d49:      	jne	0x221d5b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x9b>
  221d4b:      	leaq	0x10(%r14), %rdx
  221d4f:      	xorl	%eax, %eax
  221d51:      	lock
  221d52:      	cmpxchgl	%ecx, (%rdx)
  221d55:      	movl	%eax, %edx
  221d57:      	testl	%edx, %edx
  221d59:      	je	0x221d63 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xa3>
  221d5b:      	cmpl	%ecx, %edx
  221d5d:      	jne	0x221f27 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x267>
  221d63:      	cmpl	$0x0, 0x8(%r14)
  221d68:      	jne	0x221d80 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xc0>
  221d6a:      	cmpl	$0x0, 0x14(%r14)
  221d6f:      	jne	0x221f4e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x28e>
  221d75:      	cmpl	$0x0, 0x18(%r14)
  221d7a:      	jne	0x221f4e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x28e>
  221d80:      	movl	0x8(%r14), %ecx
  221d84:      	testl	%ecx, %ecx
  221d86:      	jne	0x221d8d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xcd>
  221d88:      	movb	$0x0, 0x1c(%r14)
  221d8d:      	incl	%ecx
  221d8f:      	movl	%ecx, 0x8(%r14)
  221d93:      	incl	0xc(%r14)
  221d97:      	movq	%gs:0x58, %rcx
  221da0:      	movl	(%rip), %eax            # 0x221da6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xe6>
		0000000000221da2:  IMAGE_REL_AMD64_REL32	_tls_index
  221da6:      	movl	$0x0, %edx
		0000000000221da7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  221dab:      	addq	(%rcx,%rax,8), %rdx
  221daf:      	movq	(%rdx), %r14
  221db2:      	testq	%r14, %r14
  221db5:      	je	0x221ec8 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x208>
  221dbb:      	movq	0x18(%r14), %r14
  221dbf:      	testq	%r14, %r14
  221dc2:      	je	0x221ddf <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x11f>
  221dc4:      	movq	0x10(%rbp), %rcx
  221dc8:      	cmpq	%rcx, 0x8(%r14)
  221dcc:      	je	0x221ded <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  221dce:      	movq	0x18(%r14), %rdx
  221dd2:      	callq	0x63c0 <SomeEngine_ECS_SomeEngine_ECS_World__FindStructuralCandidate>
  221dd7:      	movq	%rax, %r14
  221dda:      	testq	%r14, %r14
  221ddd:      	jne	0x221ded <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  221ddf:      	movq	0x10(%rbp), %rcx
  221de3:      	movq	0x30(%rcx), %rdx
  221de7:      	movq	0x8(%rdx), %rdx
  221deb:      	jmp	0x221df5 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x135>
  221ded:      	movq	0x10(%rbp), %rcx
  221df1:      	movq	0x10(%r14), %rdx
  221df5:      	movq	0x40(%rdx), %rcx
  221df9:      	movq	0x18(%rbp), %rdx
  221dfd:      	cmpl	%ecx, (%rcx)
  221dff:      	callq	0xce60 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRegistry__Get>
  221e04:      	movq	0x10(%rax), %rcx
  221e08:      	movq	0x10(%rbp), %rax
  221e0c:      	movq	%rax, -0x48(%rbp)
  221e10:      	movq	%rcx, -0x40(%rbp)
  221e14:      	movl	%ebx, -0x38(%rbp)
  221e17:      	movl	%esi, -0x34(%rbp)
  221e1a:      	cmpb	%dil, (%rdi)
  221e1d:      	leaq	0x8(%rdi), %rbx
  221e21:      	leaq	-0x90(%rbp), %rcx
  221e28:      	leaq	-0x48(%rbp), %rdx
  221e2c:      	callq	0x221f0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQueryViews___ctor>
  221e31:      	movq	-0x90(%rbp), %rsi
  221e38:      	leaq	0x28(%rdi), %rcx
  221e3c:      	movq	(%rcx), %r14
  221e3f:      	movq	(%rbx), %rbx
  221e42:      	movl	0x8(%rbx), %r15d
  221e46:      	testl	%r15d, %r15d
  221e49:      	jle	0x221ec3 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x203>
  221e4b:      	addq	$0x10, %rbx
  221e4f:      	movups	(%rbx), %xmm0
  221e52:      	movups	%xmm0, -0xa0(%rbp)
  221e59:      	movq	%rsi, %rcx
  221e5c:      	movq	-0xa0(%rbp), %rdx
  221e63:      	callq	0x1d95c0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  221e68:      	movq	%rax, %r13
  221e6b:      	testq	%r13, %r13
  221e6e:      	je	0x221eba <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fa>
  221e70:      	movq	%rsi, %rcx
  221e73:      	movq	-0x98(%rbp), %rdx
  221e7a:      	callq	0x1d95c0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  221e7f:      	testq	%rax, %rax
  221e82:      	je	0x221eba <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fa>
  221e84:      	movq	0x8(%r13), %rcx
  221e88:      	testq	%rcx, (%rax)
  221e8b:      	je	0x221eba <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fa>
  221e8d:      	movq	0x8(%rax), %rcx
  221e91:      	testq	%rcx, (%r13)
  221e95:      	je	0x221eba <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fa>
  221e97:      	movl	0x10(%r14), %ecx
  221e9b:      	leal	0x1(%rcx), %edx
  221e9e:      	movl	%edx, 0x10(%r14)
  221ea2:      	cmpl	0x8(%r14), %ecx
  221ea6:      	jae	0x221edc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x21c>
  221ea8:      	movq	(%r14), %rdx
  221eab:      	shlq	$0x4, %rcx
  221eaf:      	movups	-0xa0(%rbp), %xmm0
  221eb6:      	movups	%xmm0, (%rdx,%rcx)
  221eba:      	addq	$0x10, %rbx
  221ebe:      	decl	%r15d
  221ec1:      	jne	0x221e4f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x18f>
  221ec3:      	movb	$0x1, (%rdi)
  221ec6:      	jmp	0x221ee2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x222>
  221ec8:      	movq	%rdx, %rcx
  221ecb:      	leaq	-0x118c22(%rip), %rax   # 0x1092b0 <S_P_CoreLib_Internal_Runtime_ThreadStatics__GetInlinedThreadStaticBaseSlow>
  221ed2:      	callq	*%rax
  221ed4:      	movq	%rax, %r14
  221ed7:      	jmp	0x221dbb <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xfb>
  221edc:      	callq	0x10c710 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  221ee1:      	int3
  221ee2:      	cmpb	$0x0, 0x30(%rbp)
  221ee6:      	jne	0x221f06 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x246>
  221ee8:      	movq	0x10(%rbp), %rcx
  221eec:      	movq	0x38(%rcx), %rcx
  221ef0:      	leaq	0x14(%rcx), %rax
  221ef4:      	movl	$0xffffffff, %edx       # imm = 0xFFFFFFFF
  221ef9:      	lock
  221efa:      	xaddl	%edx, (%rax)
  221efd:      	decl	%edx
  221eff:      	jns	0x221f14 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x254>
  221f01:      	jmp	0x221fd2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x312>
  221f06:      	movq	0x10(%rbp), %rcx
  221f0a:      	movl	$0x1, %edx
  221f0f:      	callq	0x72f0 <SomeEngine_ECS_SomeEngine_ECS_World__EndIteration>
  221f14:      	nop
  221f15:      	addq	$0xa0, %rsp
  221f1c:      	popq	%rbx
  221f1d:      	popq	%rsi
  221f1e:      	popq	%rdi
  221f1f:      	popq	%r13
  221f21:      	popq	%r14
  221f23:      	popq	%r15
  221f25:      	popq	%rbp
  221f26:      	retq
  221f27:      	leaq	(%rip), %rcx            # 0x221f2e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x26e>
		0000000000221f2a:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f2e:      	callq	0x221f33 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x273>
		0000000000221f2f:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f33:      	movq	%rax, %rbx
  221f36:      	movq	%rbx, %rcx
  221f39:      	leaq	(%rip), %rdx            # 0x221f40 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x280>
		0000000000221f3c:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  221f40:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  221f45:      	movq	%rbx, %rcx
  221f48:      	callq	0x221f4d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x28d>
		0000000000221f49:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221f4d:      	int3
  221f4e:      	xorl	%ecx, %ecx
  221f50:      	movl	%ecx, 0x10(%r14)
  221f54:      	leaq	(%rip), %rcx            # 0x221f5b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x29b>
		0000000000221f57:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f5b:      	callq	0x221f60 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a0>
		0000000000221f5c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f60:      	movq	%rax, %rbx
  221f63:      	movq	%rbx, %rcx
  221f66:      	leaq	(%rip), %rdx            # 0x221f6d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2ad>
		0000000000221f69:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  221f6d:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  221f72:      	movq	%rbx, %rcx
  221f75:      	callq	0x221f7a <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2ba>
		0000000000221f76:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221f7a:      	int3
  221f7b:      	leaq	(%rip), %rcx            # 0x221f82 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2c2>
		0000000000221f7e:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f82:      	callq	0x221f87 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2c7>
		0000000000221f83:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f87:      	movq	%rax, %rsi
  221f8a:      	movq	%rsi, %rcx
  221f8d:      	leaq	(%rip), %rdx            # 0x221f94 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2d4>
		0000000000221f90:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  221f94:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  221f99:      	movq	%rsi, %rcx
  221f9c:      	callq	0x221fa1 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2e1>
		0000000000221f9d:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221fa1:      	int3
  221fa2:      	leaq	0x14(%rdx), %rcx
  221fa6:      	callq	0xc9080 <S_P_CoreLib_System_Threading_Interlocked__Decrement>
  221fab:      	leaq	(%rip), %rcx            # 0x221fb2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f2>
		0000000000221fae:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221fb2:      	callq	0x221fb7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f7>
		0000000000221fb3:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221fb7:      	movq	%rax, %rbx
  221fba:      	movq	%rbx, %rcx
  221fbd:      	leaq	(%rip), %rdx            # 0x221fc4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x304>
		0000000000221fc0:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  221fc4:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  221fc9:      	movq	%rbx, %rcx
  221fcc:      	callq	0x221fd1 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x311>
		0000000000221fcd:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221fd1:      	int3
  221fd2:      	addq	$0x14, %rcx
  221fd6:      	callq	0xc9070 <S_P_CoreLib_System_Threading_Interlocked__Increment>
  221fdb:      	leaq	(%rip), %rcx            # 0x221fe2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x322>
		0000000000221fde:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221fe2:      	callq	0x221fe7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x327>
		0000000000221fe3:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221fe7:      	movq	%rax, %rbx
  221fea:      	movq	%rbx, %rcx
  221fed:      	leaq	(%rip), %rdx            # 0x221ff4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x334>
		0000000000221ff0:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  221ff4:      	callq	0x9d1b0 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  221ff9:      	movq	%rbx, %rcx
  221ffc:      	callq	0x222001 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x341>
		0000000000221ffd:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  222001:      	int3
  222002:      	subq	$0x28, %rsp
  222006:      	movq	%rcx, %rbx
  222009:      	leaq	(%rip), %rsi            # 0x222010 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x350>
		000000000022200c:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  222010:      	leaq	(%rip), %rcx            # 0x222017 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x357>
		0000000000222013:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  222017:      	cmpb	$0x0, 0x30(%rbp)
  22201b:      	cmoveq	%rcx, %rsi
  22201f:      	leaq	(%rip), %rcx            # 0x222026 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x366>
		0000000000222022:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  222026:      	movl	$0x2, %edx
  22202b:      	callq	0x222030 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x370>
		000000000022202c:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  222030:      	movq	%rax, %rdi
  222033:      	leaq	0x10(%rdi), %rcx
  222037:      	movq	-0xa8(%rbp), %rdx
  22203e:      	callq	0x222043 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x383>
		000000000022203f:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  222043:      	leaq	0x18(%rdi), %rcx
  222047:      	movq	%rbx, %rdx
  22204a:      	callq	0x22204f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x38f>
		000000000022204b:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  22204f:      	leaq	(%rip), %rcx            # 0x222056 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x396>
		0000000000222052:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  222056:      	callq	0x22205b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x39b>
		0000000000222057:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22205b:      	movq	%rax, %rbx
  22205e:      	movq	%rbx, %rcx
  222061:      	movq	%rsi, %rdx
  222064:      	movq	%rdi, %r8
  222067:      	callq	0x964b0 <S_P_CoreLib_System_AggregateException___ctor_5>
  22206c:      	movq	%rbx, %rcx
  22206f:      	callq	0x222074 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3b4>
		0000000000222070:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  222074:      	int3
  222075:      	subq	$0x28, %rsp
  222079:      	movq	%rcx, %rbx
  22207c:      	movq	%rbx, -0xa8(%rbp)
  222083:      	movzbl	0x30(%rbp), %edx
  222087:      	movq	0x10(%rbp), %rcx
  22208b:      	xorl	%r8d, %r8d
  22208e:      	callq	0x7630 <SomeEngine_ECS_SomeEngine_ECS_World__EndQueryIteration>
  222093:      	nop
  222094:      	movq	%rbx, %rcx
  222097:      	callq	0xe7370 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Capture>
  22209c:      	movq	%rax, %rcx
  22209f:      	cmpl	%ecx, (%rcx)
  2220a1:      	callq	0xe73d0 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Throw>
  2220a6:      	callq	0x2220ab <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3eb>
		00000000002220a7:  IMAGE_REL_AMD64_REL32	RhpRethrow
  2220ab:      	int3
  2220ac:      	nop
  2220ad:      	nop
  2220ae:      	nop
  2220af:      	nop
