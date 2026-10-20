
F:\.codex\worktrees\ecs-relations-worker-3\SomeEngine.Next\benchmarks\SomeEngine.ECS.Relations.Benchmarks\obj\Release\net10.0\win-x64\native\SomeEngine.ECS.Relations.Benchmarks.obj:	file format coff-x86-64

Disassembly of section .managedcode$I:

000000000015e460 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  15e460:      	pushq	%r15
  15e462:      	pushq	%r14
  15e464:      	pushq	%r13
  15e466:      	pushq	%rdi
  15e467:      	pushq	%rsi
  15e468:      	pushq	%rbp
  15e469:      	pushq	%rbx
  15e46a:      	subq	$0x180, %rsp            # imm = 0x180
  15e471:      	xorps	%xmm4, %xmm4
  15e474:      	movaps	%xmm4, 0x20(%rsp)
  15e479:      	movabsq	$-0x150, %rax           # imm = 0xFEB0
  15e483:      	movaps	%xmm4, 0x180(%rsp,%rax)
  15e48b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  15e493:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  15e49b:      	addq	$0x30, %rax
  15e49f:      	jne	0x15e483 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x23>
  15e4a1:      	movq	%rdx, 0x1c8(%rsp)
  15e4a9:      	movq	%rcx, %rbx
  15e4ac:      	leaq	0x1c8(%rsp), %rcx
  15e4b4:      	leaq	0x20(%rsp), %rdx
  15e4b9:      	callq	0xb130 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryCursor__get_Rows>
  15e4be:      	movups	0x20(%rsp), %xmm0
  15e4c3:      	movups	%xmm0, 0x60(%rsp)
  15e4c8:      	movups	0x30(%rsp), %xmm0
  15e4cd:      	movups	%xmm0, 0x70(%rsp)
  15e4d2:      	movups	0x40(%rsp), %xmm0
  15e4d7:      	movups	%xmm0, 0x80(%rsp)
  15e4df:      	movups	0x50(%rsp), %xmm0
  15e4e4:      	movups	%xmm0, 0x90(%rsp)
  15e4ec:      	xorps	%xmm0, %xmm0
  15e4ef:      	movups	%xmm0, 0xa0(%rsp)
  15e4f7:      	xorps	%xmm0, %xmm0
  15e4fa:      	movups	%xmm0, 0xb0(%rsp)
  15e502:      	leaq	0x120(%rsp), %rcx
  15e50a:      	leaq	0x60(%rsp), %rdx
  15e50f:      	movl	$0x60, %r8d
  15e515:      	callq	0x88010 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  15e51a:      	movq	0x140(%rsp), %rdx
  15e522:      	movq	%rdx, %rsi
  15e525:      	leaq	0x120(%rsp), %r9
  15e52d:      	movq	0x138(%rsp), %rcx
  15e535:      	movl	0x148(%rsp), %r8d
  15e53d:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e542:      	testl	%eax, %eax
  15e544:      	jne	0x15e58f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x12f>
  15e546:      	leaq	0x128(%rsp), %rcx
  15e54e:      	callq	0x154cc0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  15e553:      	testl	%eax, %eax
  15e555:      	je	0x15e787 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x327>
  15e55b:      	movl	$0xffffffff, 0x120(%rsp) # imm = 0xFFFFFFFF
  15e566:      	leaq	0x120(%rsp), %r9
  15e56e:      	movq	0x138(%rsp), %rcx
  15e576:      	movq	0x140(%rsp), %rdx
  15e57e:      	movl	0x148(%rsp), %r8d
  15e586:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e58b:      	testl	%eax, %eax
  15e58d:      	je	0x15e546 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0xe6>
  15e58f:      	movl	0x120(%rsp), %edi
  15e596:      	movq	0x140(%rsp), %rbp
  15e59e:      	cmpq	0x140(%rsp), %rsi
  15e5a6:      	je	0x15e5c5 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x165>
  15e5a8:      	leaq	0x120(%rsp), %rcx
  15e5b0:      	movq	0x138(%rsp), %rdx
  15e5b8:      	movq	0x140(%rsp), %r8
  15e5c0:      	callq	0x161420 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  15e5c5:      	cmpl	$0x0, 0x168(%rsp)
  15e5cd:      	jne	0x15e5d3 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x173>
  15e5cf:      	xorl	%esi, %esi
  15e5d1:      	jmp	0x15e5e5 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x185>
  15e5d3:      	movq	0x160(%rsp), %rsi
  15e5db:      	movslq	%edi, %rcx
  15e5de:      	imulq	$0x30, %rcx, %rcx
  15e5e2:      	addq	%rcx, %rsi
  15e5e5:      	movq	0x8(%rbp), %rcx
  15e5e9:      	movq	0x8(%rcx), %rcx
  15e5ed:      	testq	%rcx, %rcx
  15e5f0:      	je	0x15e5fb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x19b>
  15e5f2:      	leaq	0x10(%rcx), %rdx
  15e5f6:      	movl	0x8(%rcx), %ecx
  15e5f9:      	jmp	0x15e5ff <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x19f>
  15e5fb:      	xorl	%edx, %edx
  15e5fd:      	xorl	%ecx, %ecx
  15e5ff:      	cmpl	%ecx, %edi
  15e601:      	jae	0x15e79d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x33d>
  15e607:      	movl	%edi, %ecx
  15e609:      	leaq	(%rdx,%rcx,8), %rcx
  15e60d:      	movl	(%rcx), %edi
  15e60f:      	movl	0x4(%rcx), %ebp
  15e612:      	leaq	0xc0(%rsp), %rcx
  15e61a:      	leaq	0x120(%rsp), %rdx
  15e622:      	movl	$0x60, %r8d
  15e628:      	callq	0x88010 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  15e62d:      	jmp	0x15e6c2 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x262>
  15e632:      	movq	0x100(%rsp), %rax
  15e63a:      	movslq	%r15d, %rcx
  15e63d:      	imulq	$0x30, %rcx, %rcx
  15e641:      	addq	%rcx, %rax
  15e644:      	cmpl	$0x0, (%rsi)
  15e647:      	je	0x15e64e <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1ee>
  15e649:      	cmpl	$0x0, (%rax)
  15e64c:      	jne	0x15e6c2 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x262>
  15e64e:      	movq	0x8(%r13), %rax
  15e652:      	movq	0x8(%rax), %rax
  15e656:      	testq	%rax, %rax
  15e659:      	je	0x15e664 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x204>
  15e65b:      	leaq	0x10(%rax), %rcx
  15e65f:      	movl	0x8(%rax), %eax
  15e662:      	jmp	0x15e668 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x208>
  15e664:      	xorl	%ecx, %ecx
  15e666:      	xorl	%eax, %eax
  15e668:      	cmpl	%eax, %r15d
  15e66b:      	jae	0x15e79d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x33d>
  15e671:      	movl	%r15d, %eax
  15e674:      	leaq	(%rcx,%rax,8), %rax
  15e678:      	movl	(%rax), %ecx
  15e67a:      	movl	0x4(%rax), %eax
  15e67d:      	cmpl	%ecx, %edi
  15e67f:      	jl	0x15e691 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x231>
  15e681:      	cmpl	%ecx, %edi
  15e683:      	jne	0x15e779 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x319>
  15e689:      	cmpl	%eax, %ebp
  15e68b:      	jge	0x15e779 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x319>
  15e691:      	movl	%edi, %edx
  15e693:      	movl	%ebp, %r8d
  15e696:      	movl	0x10(%rbx), %r10d
  15e69a:      	leal	0x1(%r10), %r9d
  15e69e:      	movl	%r9d, 0x10(%rbx)
  15e6a2:      	cmpl	0x8(%rbx), %r10d
  15e6a6:      	jae	0x15e79d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x33d>
  15e6ac:      	shlq	$0x4, %r10
  15e6b0:      	addq	(%rbx), %r10
  15e6b3:      	movl	%edx, (%r10)
  15e6b6:      	movl	%r8d, 0x4(%r10)
  15e6ba:      	movl	%ecx, 0x8(%r10)
  15e6be:      	movl	%eax, 0xc(%r10)
  15e6c2:      	movq	0xe0(%rsp), %rdx
  15e6ca:      	movq	%rdx, %r14
  15e6cd:      	leaq	0xc0(%rsp), %r9
  15e6d5:      	movq	0xd8(%rsp), %rcx
  15e6dd:      	movl	0xe8(%rsp), %r8d
  15e6e5:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e6ea:      	testl	%eax, %eax
  15e6ec:      	jne	0x15e737 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x2d7>
  15e6ee:      	leaq	0xc8(%rsp), %rcx
  15e6f6:      	callq	0x154cc0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  15e6fb:      	testl	%eax, %eax
  15e6fd:      	je	0x15e51a <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0xba>
  15e703:      	movl	$0xffffffff, 0xc0(%rsp) # imm = 0xFFFFFFFF
  15e70e:      	leaq	0xc0(%rsp), %r9
  15e716:      	movq	0xd8(%rsp), %rcx
  15e71e:      	movq	0xe0(%rsp), %rdx
  15e726:      	movl	0xe8(%rsp), %r8d
  15e72e:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  15e733:      	testl	%eax, %eax
  15e735:      	je	0x15e6ee <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x28e>
  15e737:      	movl	0xc0(%rsp), %r15d
  15e73f:      	movq	0xe0(%rsp), %r8
  15e747:      	movq	%r8, %r13
  15e74a:      	cmpq	%r8, %r14
  15e74d:      	je	0x15e764 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x304>
  15e74f:      	leaq	0xc0(%rsp), %rcx
  15e757:      	movq	0xd8(%rsp), %rdx
  15e75f:      	callq	0x161420 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  15e764:      	cmpl	$0x0, 0x108(%rsp)
  15e76c:      	jne	0x15e632 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1d2>
  15e772:      	xorl	%eax, %eax
  15e774:      	jmp	0x15e644 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1e4>
  15e779:      	movl	%ecx, %edx
  15e77b:      	movl	%eax, %r8d
  15e77e:      	movl	%edi, %ecx
  15e780:      	movl	%ebp, %eax
  15e782:      	jmp	0x15e696 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HandwrittenDefaultPairs_PairExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x236>
  15e787:      	movb	$0x1, 0x18(%rbx)
  15e78b:      	addq	$0x180, %rsp            # imm = 0x180
  15e792:      	popq	%rbx
  15e793:      	popq	%rbp
  15e794:      	popq	%rsi
  15e795:      	popq	%rdi
  15e796:      	popq	%r13
  15e798:      	popq	%r14
  15e79a:      	popq	%r15
  15e79c:      	retq
  15e79d:      	callq	0x10c8f0 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  15e7a2:      	int3
  15e7a3:      	nop
  15e7a4:      	nop
  15e7a5:      	nop
  15e7a6:      	nop
  15e7a7:      	nop
  15e7a8:      	nop
  15e7a9:      	nop
  15e7aa:      	nop
  15e7ab:      	nop
  15e7ac:      	nop
  15e7ad:      	nop
  15e7ae:      	nop
  15e7af:      	nop
  15e7b0:      	nop
  15e7b1:      	nop
  15e7b2:      	nop
  15e7b3:      	nop
  15e7b4:      	nop
  15e7b5:      	nop
  15e7b6:      	nop
  15e7b7:      	nop
  15e7b8:      	nop
  15e7b9:      	nop
  15e7ba:      	nop
  15e7bb:      	nop
  15e7bc:      	nop
  15e7bd:      	nop
  15e7be:      	nop
  15e7bf:      	nop

0000000000160680 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  160680:      	pushq	%r15
  160682:      	pushq	%r14
  160684:      	pushq	%rdi
  160685:      	pushq	%rsi
  160686:      	pushq	%rbp
  160687:      	pushq	%rbx
  160688:      	subq	$0x38, %rsp
  16068c:      	movq	%rcx, %rbx
  16068f:      	movq	%rdx, %rsi
  160692:      	movq	(%rbx), %rdi
  160695:      	movl	0x8(%rbx), %ebp
  160698:      	testl	%ebp, %ebp
  16069a:      	jle	0x16070c <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x8c>
  16069c:      	xorl	%r14d, %r14d
  16069f:      	movups	(%rdi,%r14), %xmm0
  1606a4:      	movups	%xmm0, 0x28(%rsp)
  1606a9:      	movq	%rsi, %rcx
  1606ac:      	movq	0x28(%rsp), %rdx
  1606b1:      	callq	0x1d8a40 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1606b6:      	movq	%rax, %r15
  1606b9:      	testq	%r15, %r15
  1606bc:      	je	0x160704 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1606be:      	movq	%rsi, %rcx
  1606c1:      	movq	0x30(%rsp), %rdx
  1606c6:      	callq	0x1d8a40 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1606cb:      	testq	%rax, %rax
  1606ce:      	je	0x160704 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1606d0:      	movq	0x8(%r15), %rcx
  1606d4:      	testq	%rcx, (%rax)
  1606d7:      	je	0x160704 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1606d9:      	movq	0x8(%rax), %rax
  1606dd:      	testq	%rax, (%r15)
  1606e0:      	je	0x160704 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  1606e2:      	leaq	0x10(%rbx), %rax
  1606e6:      	movl	0x10(%rax), %ecx
  1606e9:      	leal	0x1(%rcx), %edx
  1606ec:      	movl	%edx, 0x10(%rax)
  1606ef:      	cmpl	0x8(%rax), %ecx
  1606f2:      	jae	0x16071d <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x9d>
  1606f4:      	movq	(%rax), %rax
  1606f7:      	shlq	$0x4, %rcx
  1606fb:      	movups	0x28(%rsp), %xmm0
  160700:      	movups	%xmm0, (%rax,%rcx)
  160704:      	addq	$0x10, %r14
  160708:      	decl	%ebp
  16070a:      	jne	0x16069f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1f>
  16070c:      	movb	$0x1, 0x28(%rbx)
  160710:      	addq	$0x38, %rsp
  160714:      	popq	%rbx
  160715:      	popq	%rbp
  160716:      	popq	%rsi
  160717:      	popq	%rdi
  160718:      	popq	%r14
  16071a:      	popq	%r15
  16071c:      	retq
  16071d:      	callq	0x10c8f0 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  160722:      	int3
  160723:      	nop
  160724:      	nop
  160725:      	nop
  160726:      	nop
  160727:      	nop
  160728:      	nop
  160729:      	nop
  16072a:      	nop
  16072b:      	nop
  16072c:      	nop
  16072d:      	nop
  16072e:      	nop
  16072f:      	nop

00000000001a7390 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJobData_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute>:
  1a7390:      	pushq	%r14
  1a7392:      	pushq	%rdi
  1a7393:      	pushq	%rsi
  1a7394:      	pushq	%rbp
  1a7395:      	pushq	%rbx
  1a7396:      	subq	$0x110, %rsp            # imm = 0x110
  1a739d:      	xorl	%eax, %eax
  1a739f:      	movq	%rax, 0x38(%rsp)
  1a73a4:      	xorps	%xmm4, %xmm4
  1a73a7:      	movaps	%xmm4, 0x40(%rsp)
  1a73ac:      	movabsq	$-0xc0, %rax
  1a73b6:      	movaps	%xmm4, 0x110(%rsp,%rax)
  1a73be:      	movaps	%xmm4, 0x120(%rsp,%rax)
  1a73c6:      	movaps	%xmm4, 0x130(%rsp,%rax)
  1a73ce:      	addq	$0x30, %rax
  1a73d2:      	jne	0x1a73b6 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJobData_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute+0x26>
  1a73d4:      	movq	%rcx, %rbx
  1a73d7:      	movl	%edx, %esi
  1a73d9:      	leaq	0x38(%rbx), %rdx
  1a73dd:      	movq	(%rdx), %rdi
  1a73e0:      	movq	0x10(%rdx), %rbp
  1a73e4:      	leaq	0xc8(%rsp), %rcx
  1a73ec:      	cmpb	%dl, (%rdx)
  1a73ee:      	movl	$0x48, %r8d
  1a73f4:      	callq	0x88010 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  1a73f9:      	leaq	0x8(%rbx), %rdx
  1a73fd:      	addq	$0x20, %rbx
  1a7401:      	movups	0xd0(%rsp), %xmm0
  1a7409:      	movups	%xmm0, 0x80(%rsp)
  1a7411:      	movups	0xe0(%rsp), %xmm0
  1a7419:      	movups	%xmm0, 0x90(%rsp)
  1a7421:      	movups	0xf0(%rsp), %xmm0
  1a7429:      	movups	%xmm0, 0xa0(%rsp)
  1a7431:      	movups	0x100(%rsp), %xmm0
  1a7439:      	movups	%xmm0, 0xb0(%rsp)
  1a7441:      	movq	0x10(%rdi), %r8
  1a7445:      	cmpl	0x8(%r8), %esi
  1a7449:      	jae	0x1a74e1 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJobData_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute+0x151>
  1a744f:      	movl	%esi, %r14d
  1a7452:      	leaq	0x10(%r8,%r14,8), %r8
  1a7457:      	movl	(%r8), %ecx
  1a745a:      	movl	0x4(%r8), %r8d
  1a745e:      	movq	%rdx, 0x38(%rsp)
  1a7463:      	movq	%rbx, 0x40(%rsp)
  1a7468:      	xorl	%edx, %edx
  1a746a:      	movq	%rdx, 0x48(%rsp)
  1a746f:      	movq	%rdx, 0x50(%rsp)
  1a7474:      	movq	%rdx, 0x58(%rsp)
  1a7479:      	movq	%rdx, 0x60(%rsp)
  1a747e:      	movq	%rdx, 0x68(%rsp)
  1a7483:      	movq	%rdx, 0x70(%rsp)
  1a7488:      	movq	%rdx, 0x78(%rsp)
  1a748d:      	movl	%ecx, 0x20(%rsp)
  1a7491:      	movl	%r8d, 0x24(%rsp)
  1a7496:      	leaq	0xc0(%rsp), %rdx
  1a749e:      	movq	%rdx, 0x28(%rsp)
  1a74a3:      	leaq	0x38(%rsp), %rdx
  1a74a8:      	leaq	0xa0(%rsp), %r8
  1a74b0:      	movq	%rbp, %rcx
  1a74b3:      	movl	$0x1, %r9d
  1a74b9:      	callq	0x226880 <SomeEngine_Physics_SomeEngine_Physics_SpatialTraversal__Enumerate_0<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_CollisionPairQueries_PairCount>>
  1a74be:      	movq	0x18(%rdi), %rax
  1a74c2:      	cmpl	0x8(%rax), %esi
  1a74c5:      	jae	0x1a74e1 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJobData_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute+0x151>
  1a74c7:      	movl	0xc0(%rsp), %ecx
  1a74ce:      	movl	%ecx, 0x10(%rax,%r14,4)
  1a74d3:      	addq	$0x110, %rsp            # imm = 0x110
  1a74da:      	popq	%rbx
  1a74db:      	popq	%rbp
  1a74dc:      	popq	%rsi
  1a74dd:      	popq	%rdi
  1a74de:      	popq	%r14
  1a74e0:      	retq
  1a74e1:      	callq	0x10c8f0 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  1a74e6:      	int3
  1a74e7:      	nop
  1a74e8:      	nop
  1a74e9:      	nop
  1a74ea:      	nop
  1a74eb:      	nop
  1a74ec:      	nop
  1a74ed:      	nop
  1a74ee:      	nop
  1a74ef:      	nop

00000000001a74f0 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJob_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute>:
  1a74f0:      	movq	%rcx, %rdx
  1a74f3:      	movq	(%rdx), %rcx
  1a74f6:      	movl	0x8(%rdx), %edx
  1a74f9:      	cmpl	%ecx, (%rcx)
  1a74fb:      	jmp	0x1a7390 <SomeEngine_ECS_Systems_SomeEngine_ECS_Systems_ReadOnlyQueryPairJobs_PairJobData_1<SomeEngine_Physics_SomeEngine_Physics_CollisionPairBuffer_CountJob_1<SomeEngine_Physics_SomeEngine_Physics_SpatialCandidates_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_BodyBoundsReader__SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialAnd_4<SomeEngine_Physics_SomeEngine_Physics_SpatialComponentFilter_3<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__SomeEngine_Physics_SomeEngine_Physics_SpatialBitmapFilter_5<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategories__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_MutualCategoryBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_Physics_SomeEngine_Physics_MutualSpatialPermission>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_SpatialExcept_2<SomeEngine_Physics_SomeEngine_Physics_SpatialKeyFilter_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SameCharacterHair__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_HairCharacterIndex__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Hair__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__S_P_CoreLib_System_ValueTuple_2<S_P_CoreLib_System_ValueTuple_2<SomeEngine_Physics_SomeEngine_Physics_GroupMatch__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>__SomeEngine_Physics_SomeEngine_Physics_GroupMatch>>>>__Execute>

00000000001d0b00 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>:
  1d0b00:      	pushq	%r15
  1d0b02:      	pushq	%r14
  1d0b04:      	pushq	%r13
  1d0b06:      	pushq	%rdi
  1d0b07:      	pushq	%rsi
  1d0b08:      	pushq	%rbp
  1d0b09:      	pushq	%rbx
  1d0b0a:      	subq	$0x180, %rsp            # imm = 0x180
  1d0b11:      	xorps	%xmm4, %xmm4
  1d0b14:      	movaps	%xmm4, 0x20(%rsp)
  1d0b19:      	movabsq	$-0x150, %rax           # imm = 0xFEB0
  1d0b23:      	movaps	%xmm4, 0x180(%rsp,%rax)
  1d0b2b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  1d0b33:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  1d0b3b:      	addq	$0x30, %rax
  1d0b3f:      	jne	0x1d0b23 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x23>
  1d0b41:      	movq	%rcx, 0x1c0(%rsp)
  1d0b49:      	movq	%r8, %rbx
  1d0b4c:      	leaq	0x1c0(%rsp), %rcx
  1d0b54:      	leaq	0x20(%rsp), %rdx
  1d0b59:      	callq	0xb130 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryCursor__get_Rows>
  1d0b5e:      	movups	0x20(%rsp), %xmm0
  1d0b63:      	movups	%xmm0, 0x60(%rsp)
  1d0b68:      	movups	0x30(%rsp), %xmm0
  1d0b6d:      	movups	%xmm0, 0x70(%rsp)
  1d0b72:      	movups	0x40(%rsp), %xmm0
  1d0b77:      	movups	%xmm0, 0x80(%rsp)
  1d0b7f:      	movups	0x50(%rsp), %xmm0
  1d0b84:      	movups	%xmm0, 0x90(%rsp)
  1d0b8c:      	xorps	%xmm0, %xmm0
  1d0b8f:      	movups	%xmm0, 0xa0(%rsp)
  1d0b97:      	xorps	%xmm0, %xmm0
  1d0b9a:      	movups	%xmm0, 0xb0(%rsp)
  1d0ba2:      	leaq	0x120(%rsp), %rcx
  1d0baa:      	leaq	0x60(%rsp), %rdx
  1d0baf:      	movl	$0x60, %r8d
  1d0bb5:      	callq	0x88010 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  1d0bba:      	movq	0x140(%rsp), %rsi
  1d0bc2:      	leaq	0x120(%rsp), %r9
  1d0bca:      	movq	0x138(%rsp), %rcx
  1d0bd2:      	movq	0x140(%rsp), %rdx
  1d0bda:      	movl	0x148(%rsp), %r8d
  1d0be2:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d0be7:      	testl	%eax, %eax
  1d0be9:      	jne	0x1d0c34 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x134>
  1d0beb:      	leaq	0x128(%rsp), %rcx
  1d0bf3:      	callq	0x154cc0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  1d0bf8:      	testl	%eax, %eax
  1d0bfa:      	je	0x1d0e2c <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x32c>
  1d0c00:      	movl	$0xffffffff, 0x120(%rsp) # imm = 0xFFFFFFFF
  1d0c0b:      	leaq	0x120(%rsp), %r9
  1d0c13:      	movq	0x138(%rsp), %rcx
  1d0c1b:      	movq	0x140(%rsp), %rdx
  1d0c23:      	movl	0x148(%rsp), %r8d
  1d0c2b:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d0c30:      	testl	%eax, %eax
  1d0c32:      	je	0x1d0beb <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0xeb>
  1d0c34:      	movl	0x120(%rsp), %edi
  1d0c3b:      	movq	0x140(%rsp), %rbp
  1d0c43:      	cmpq	0x140(%rsp), %rsi
  1d0c4b:      	je	0x1d0c6a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x16a>
  1d0c4d:      	leaq	0x120(%rsp), %rcx
  1d0c55:      	movq	0x138(%rsp), %rdx
  1d0c5d:      	movq	0x140(%rsp), %r8
  1d0c65:      	callq	0x161420 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  1d0c6a:      	cmpl	$0x0, 0x168(%rsp)
  1d0c72:      	jne	0x1d0c78 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x178>
  1d0c74:      	xorl	%esi, %esi
  1d0c76:      	jmp	0x1d0c8a <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x18a>
  1d0c78:      	movq	0x160(%rsp), %rsi
  1d0c80:      	movslq	%edi, %rcx
  1d0c83:      	imulq	$0x30, %rcx, %rcx
  1d0c87:      	addq	%rcx, %rsi
  1d0c8a:      	movq	0x8(%rbp), %rcx
  1d0c8e:      	movq	0x8(%rcx), %rcx
  1d0c92:      	testq	%rcx, %rcx
  1d0c95:      	je	0x1d0ca0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1a0>
  1d0c97:      	leaq	0x10(%rcx), %rdx
  1d0c9b:      	movl	0x8(%rcx), %ecx
  1d0c9e:      	jmp	0x1d0ca4 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1a4>
  1d0ca0:      	xorl	%edx, %edx
  1d0ca2:      	xorl	%ecx, %ecx
  1d0ca4:      	cmpl	%ecx, %edi
  1d0ca6:      	jae	0x1d0e43 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x343>
  1d0cac:      	movl	%edi, %ecx
  1d0cae:      	leaq	(%rdx,%rcx,8), %rcx
  1d0cb2:      	movl	(%rcx), %edi
  1d0cb4:      	movl	0x4(%rcx), %ebp
  1d0cb7:      	leaq	0xc0(%rsp), %rcx
  1d0cbf:      	leaq	0x120(%rsp), %rdx
  1d0cc7:      	movl	$0x60, %r8d
  1d0ccd:      	callq	0x88010 <S_P_CoreLib_System_Buffer__BulkMoveWithWriteBarrier>
  1d0cd2:      	jmp	0x1d0d67 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x267>
  1d0cd7:      	movq	0x100(%rsp), %rax
  1d0cdf:      	movslq	%r15d, %rcx
  1d0ce2:      	imulq	$0x30, %rcx, %rcx
  1d0ce6:      	addq	%rcx, %rax
  1d0ce9:      	cmpl	$0x0, (%rsi)
  1d0cec:      	je	0x1d0cf3 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1f3>
  1d0cee:      	cmpl	$0x0, (%rax)
  1d0cf1:      	jne	0x1d0d67 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x267>
  1d0cf3:      	movq	0x8(%r13), %rax
  1d0cf7:      	movq	0x8(%rax), %rax
  1d0cfb:      	testq	%rax, %rax
  1d0cfe:      	je	0x1d0d09 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x209>
  1d0d00:      	leaq	0x10(%rax), %rcx
  1d0d04:      	movl	0x8(%rax), %eax
  1d0d07:      	jmp	0x1d0d0d <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x20d>
  1d0d09:      	xorl	%ecx, %ecx
  1d0d0b:      	xorl	%eax, %eax
  1d0d0d:      	cmpl	%eax, %r15d
  1d0d10:      	jae	0x1d0e43 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x343>
  1d0d16:      	movl	%r15d, %eax
  1d0d19:      	leaq	(%rcx,%rax,8), %rax
  1d0d1d:      	movl	(%rax), %ecx
  1d0d1f:      	movl	0x4(%rax), %eax
  1d0d22:      	cmpl	%ecx, %edi
  1d0d24:      	jl	0x1d0d36 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x236>
  1d0d26:      	cmpl	%ecx, %edi
  1d0d28:      	jne	0x1d0e1e <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x31e>
  1d0d2e:      	cmpl	%eax, %ebp
  1d0d30:      	jge	0x1d0e1e <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x31e>
  1d0d36:      	movl	%edi, %edx
  1d0d38:      	movl	%ebp, %r8d
  1d0d3b:      	movl	0x10(%rbx), %r10d
  1d0d3f:      	leal	0x1(%r10), %r9d
  1d0d43:      	movl	%r9d, 0x10(%rbx)
  1d0d47:      	cmpl	0x8(%rbx), %r10d
  1d0d4b:      	jae	0x1d0e43 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x343>
  1d0d51:      	shlq	$0x4, %r10
  1d0d55:      	addq	(%rbx), %r10
  1d0d58:      	movl	%edx, (%r10)
  1d0d5b:      	movl	%r8d, 0x4(%r10)
  1d0d5f:      	movl	%ecx, 0x8(%r10)
  1d0d63:      	movl	%eax, 0xc(%r10)
  1d0d67:      	movq	0xe0(%rsp), %rdx
  1d0d6f:      	movq	%rdx, %r14
  1d0d72:      	leaq	0xc0(%rsp), %r9
  1d0d7a:      	movq	0xd8(%rsp), %rcx
  1d0d82:      	movl	0xe8(%rsp), %r8d
  1d0d8a:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d0d8f:      	testl	%eax, %eax
  1d0d91:      	jne	0x1d0ddc <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x2dc>
  1d0d93:      	leaq	0xc8(%rsp), %rcx
  1d0d9b:      	callq	0x154cc0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryChunkEnumerator_1<SomeEngine_ECS_SomeEngine_ECS_Queries_NoSharedFilter>__MoveNext>
  1d0da0:      	testl	%eax, %eax
  1d0da2:      	je	0x1d0bba <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0xba>
  1d0da8:      	movl	$0xffffffff, 0xc0(%rsp) # imm = 0xFFFFFFFF
  1d0db3:      	leaq	0xc0(%rsp), %r9
  1d0dbb:      	movq	0xd8(%rsp), %rcx
  1d0dc3:      	movq	0xe0(%rsp), %rdx
  1d0dcb:      	movl	0xe8(%rsp), %r8d
  1d0dd3:      	callq	0xd740 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRowCursor__MoveNext_0>
  1d0dd8:      	testl	%eax, %eax
  1d0dda:      	je	0x1d0d93 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x293>
  1d0ddc:      	movl	0xc0(%rsp), %r15d
  1d0de4:      	movq	0xe0(%rsp), %r8
  1d0dec:      	movq	%r8, %r13
  1d0def:      	cmpq	%r8, %r14
  1d0df2:      	je	0x1d0e09 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x309>
  1d0df4:      	leaq	0xc0(%rsp), %rcx
  1d0dfc:      	movq	0xd8(%rsp), %rdx
  1d0e04:      	callq	0x161420 <SomeEngine_ECS_SomeEngine_ECS_Queries_ComponentRowEnumerator_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body>__BindColumns>
  1d0e09:      	cmpl	$0x0, 0x108(%rsp)
  1d0e11:      	jne	0x1d0cd7 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1d7>
  1d0e17:      	xorl	%eax, %eax
  1d0e19:      	jmp	0x1d0ce9 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x1e9>
  1d0e1e:      	movl	%ecx, %edx
  1d0e20:      	movl	%eax, %r8d
  1d0e23:      	movl	%edi, %ecx
  1d0e25:      	movl	%ebp, %eax
  1d0e27:      	jmp	0x1d0d3b <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_ComponentPairsWithin_4<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_DefaultContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_Body__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__Enumerate_0<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RequiredRelationComponents__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>+0x23b>
  1d0e2c:      	movl	$0x1, %eax
  1d0e31:      	addq	$0x180, %rsp            # imm = 0x180
  1d0e38:      	popq	%rbx
  1d0e39:      	popq	%rbp
  1d0e3a:      	popq	%rsi
  1d0e3b:      	popq	%rdi
  1d0e3c:      	popq	%r13
  1d0e3e:      	popq	%r14
  1d0e40:      	popq	%r15
  1d0e42:      	retq
  1d0e43:      	callq	0x10c8f0 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  1d0e48:      	int3
  1d0e49:      	nop
  1d0e4a:      	nop
  1d0e4b:      	nop
  1d0e4c:      	nop
  1d0e4d:      	nop
  1d0e4e:      	nop
  1d0e4f:      	nop
  1d0e50:      	nop
  1d0e51:      	nop
  1d0e52:      	nop
  1d0e53:      	nop
  1d0e54:      	nop
  1d0e55:      	nop
  1d0e56:      	nop
  1d0e57:      	nop
  1d0e58:      	nop
  1d0e59:      	nop
  1d0e5a:      	nop
  1d0e5b:      	nop
  1d0e5c:      	nop
  1d0e5d:      	nop
  1d0e5e:      	nop
  1d0e5f:      	nop

00000000002211e0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>>:
  2211e0:      	pushq	%rbp
  2211e1:      	pushq	%r15
  2211e3:      	pushq	%r14
  2211e5:      	pushq	%r13
  2211e7:      	pushq	%r12
  2211e9:      	pushq	%rdi
  2211ea:      	pushq	%rsi
  2211eb:      	pushq	%rbx
  2211ec:      	subq	$0x98, %rsp
  2211f3:      	leaq	0xd0(%rsp), %rbp
  2211fb:      	xorl	%eax, %eax
  2211fd:      	movq	%rax, -0x98(%rbp)
  221204:      	xorps	%xmm4, %xmm4
  221207:      	movaps	%xmm4, -0x90(%rbp)
  22120e:      	movaps	%xmm4, -0x80(%rbp)
  221212:      	movaps	%xmm4, -0x70(%rbp)
  221216:      	movaps	%xmm4, -0x60(%rbp)
  22121a:      	movaps	%xmm4, -0x50(%rbp)
  22121e:      	movq	%rax, -0x40(%rbp)
  221222:      	movq	%rcx, 0x10(%rbp)
  221226:      	movq	%rdx, 0x18(%rbp)
  22122a:      	movl	%r8d, %ebx
  22122d:      	movl	%r9d, %esi
  221230:      	movl	0x30(%rbp), %eax
  221233:      	movq	0x38(%rbp), %rdi
  221237:      	testb	%al, %al
  221239:      	jne	0x22125c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x7c>
  22123b:      	movq	0x38(%rcx), %rdx
  22123f:      	cmpl	$0x0, 0x10(%rdx)
  221243:      	jne	0x2214b0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2d0>
  221249:      	leaq	0x14(%rdx), %r8
  22124d:      	lock
  22124e:      	incl	(%r8)
  221251:      	cmpl	$0x0, 0x10(%rdx)
  221255:      	je	0x2212be <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xde>
  221257:      	jmp	0x2214d7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f7>
  22125c:      	movq	0x38(%rcx), %r14
  221260:      	cmpb	%r14b, (%r14)
  221263:      	callq	0xc7c60 <S_P_CoreLib_System_Threading_ManagedThreadId__get_Current>
  221268:      	movl	%eax, %ecx
  22126a:      	movl	0x10(%r14), %edx
  22126e:      	testl	%edx, %edx
  221270:      	jne	0x221282 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xa2>
  221272:      	leaq	0x10(%r14), %rdx
  221276:      	xorl	%eax, %eax
  221278:      	lock
  221279:      	cmpxchgl	%ecx, (%rdx)
  22127c:      	movl	%eax, %edx
  22127e:      	testl	%edx, %edx
  221280:      	je	0x22128a <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xaa>
  221282:      	cmpl	%ecx, %edx
  221284:      	jne	0x22145c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x27c>
  22128a:      	cmpl	$0x0, 0x8(%r14)
  22128f:      	jne	0x2212a7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xc7>
  221291:      	cmpl	$0x0, 0x14(%r14)
  221296:      	jne	0x221483 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a3>
  22129c:      	cmpl	$0x0, 0x18(%r14)
  2212a1:      	jne	0x221483 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a3>
  2212a7:      	movl	0x8(%r14), %ecx
  2212ab:      	testl	%ecx, %ecx
  2212ad:      	jne	0x2212b4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xd4>
  2212af:      	movb	$0x0, 0x1c(%r14)
  2212b4:      	incl	%ecx
  2212b6:      	movl	%ecx, 0x8(%r14)
  2212ba:      	incl	0xc(%r14)
  2212be:      	movq	%gs:0x58, %rcx
  2212c7:      	movl	(%rip), %eax            # 0x2212cd <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xed>
		00000000002212c9:  IMAGE_REL_AMD64_REL32	_tls_index
  2212cd:      	movl	$0x0, %edx
		00000000002212ce:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2212d2:      	addq	(%rcx,%rax,8), %rdx
  2212d6:      	movq	(%rdx), %r14
  2212d9:      	testq	%r14, %r14
  2212dc:      	je	0x2213fb <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x21b>
  2212e2:      	movq	0x18(%r14), %r14
  2212e6:      	testq	%r14, %r14
  2212e9:      	je	0x221306 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x126>
  2212eb:      	movq	0x10(%rbp), %rcx
  2212ef:      	cmpq	%rcx, 0x8(%r14)
  2212f3:      	je	0x221314 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x134>
  2212f5:      	movq	0x18(%r14), %rdx
  2212f9:      	callq	0x6340 <SomeEngine_ECS_SomeEngine_ECS_World__FindStructuralCandidate>
  2212fe:      	movq	%rax, %r14
  221301:      	testq	%r14, %r14
  221304:      	jne	0x221314 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x134>
  221306:      	movq	0x10(%rbp), %rcx
  22130a:      	movq	0x30(%rcx), %rdx
  22130e:      	movq	0x8(%rdx), %rdx
  221312:      	jmp	0x22131c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x13c>
  221314:      	movq	0x10(%rbp), %rcx
  221318:      	movq	0x10(%r14), %rdx
  22131c:      	movq	0x40(%rdx), %rcx
  221320:      	movq	0x18(%rbp), %rdx
  221324:      	cmpl	%ecx, (%rcx)
  221326:      	callq	0xcdc0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRegistry__Get>
  22132b:      	movq	0x10(%rax), %rcx
  22132f:      	movq	0x10(%rbp), %rax
  221333:      	movq	%rax, -0x50(%rbp)
  221337:      	movq	%rcx, -0x48(%rbp)
  22133b:      	movl	%ebx, -0x40(%rbp)
  22133e:      	movl	%esi, -0x3c(%rbp)
  221341:      	cmpb	%dil, (%rdi)
  221344:      	leaq	0x8(%rdi), %rbx
  221348:      	leaq	-0x98(%rbp), %rcx
  22134f:      	leaq	-0x50(%rbp), %rdx
  221353:      	callq	0x223d0 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQueryViews___ctor>
  221358:      	movq	-0x98(%rbp), %rsi
  22135f:      	leaq	0x28(%rdi), %rdx
  221363:      	movq	(%rdx), %r14
  221366:      	movq	(%rbx), %rdx
  221369:      	testq	%rdx, %rdx
  22136c:      	je	0x221378 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x198>
  22136e:      	leaq	0x10(%rdx), %rbx
  221372:      	movl	0x8(%rdx), %r15d
  221376:      	jmp	0x22137d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x19d>
  221378:      	xorl	%ebx, %ebx
  22137a:      	xorl	%r15d, %r15d
  22137d:      	testl	%r15d, %r15d
  221380:      	jle	0x2213f6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x216>
  221382:      	xorl	%r13d, %r13d
  221385:      	leaq	(%rbx,%r13), %r12
  221389:      	movq	(%r12), %rdx
  22138d:      	movq	%rsi, %rcx
  221390:      	callq	0x1d8a40 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  221395:      	movq	%rax, -0xa8(%rbp)
  22139c:      	testq	%rax, %rax
  22139f:      	je	0x2213ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x20d>
  2213a1:      	movq	0x8(%r12), %rdx
  2213a6:      	movq	%rsi, %rcx
  2213a9:      	callq	0x1d8a40 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  2213ae:      	testq	%rax, %rax
  2213b1:      	je	0x2213ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x20d>
  2213b3:      	movq	-0xa8(%rbp), %rcx
  2213ba:      	movq	0x8(%rcx), %rdx
  2213be:      	testq	%rdx, (%rax)
  2213c1:      	je	0x2213ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x20d>
  2213c3:      	movq	0x8(%rax), %rdx
  2213c7:      	testq	%rdx, (%rcx)
  2213ca:      	je	0x2213ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x20d>
  2213cc:      	movl	0x10(%r14), %ecx
  2213d0:      	leal	0x1(%rcx), %edx
  2213d3:      	movl	%edx, 0x10(%r14)
  2213d7:      	cmpl	0x8(%r14), %ecx
  2213db:      	jae	0x22140f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x22f>
  2213dd:      	movq	(%r14), %rdx
  2213e0:      	shlq	$0x4, %rcx
  2213e4:      	movups	(%r12), %xmm0
  2213e9:      	movups	%xmm0, (%rdx,%rcx)
  2213ed:      	addq	$0x10, %r13
  2213f1:      	decl	%r15d
  2213f4:      	jne	0x221385 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1a5>
  2213f6:      	movb	$0x1, (%rdi)
  2213f9:      	jmp	0x221415 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x235>
  2213fb:      	movq	%rdx, %rcx
  2213fe:      	leaq	-0x117f75(%rip), %rax   # 0x109490 <S_P_CoreLib_Internal_Runtime_ThreadStatics__GetInlinedThreadStaticBaseSlow>
  221405:      	callq	*%rax
  221407:      	movq	%rax, %r14
  22140a:      	jmp	0x2212e2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x102>
  22140f:      	callq	0x10c8f0 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  221414:      	int3
  221415:      	cmpb	$0x0, 0x30(%rbp)
  221419:      	jne	0x221439 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x259>
  22141b:      	movq	0x10(%rbp), %rcx
  22141f:      	movq	0x38(%rcx), %rcx
  221423:      	leaq	0x14(%rcx), %rax
  221427:      	movl	$0xffffffff, %edx       # imm = 0xFFFFFFFF
  22142c:      	lock
  22142d:      	xaddl	%edx, (%rax)
  221430:      	decl	%edx
  221432:      	jns	0x221447 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x267>
  221434:      	jmp	0x221507 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x327>
  221439:      	movq	0x10(%rbp), %rcx
  22143d:      	movl	$0x1, %edx
  221442:      	callq	0x7250 <SomeEngine_ECS_SomeEngine_ECS_World__EndIteration>
  221447:      	nop
  221448:      	addq	$0x98, %rsp
  22144f:      	popq	%rbx
  221450:      	popq	%rsi
  221451:      	popq	%rdi
  221452:      	popq	%r12
  221454:      	popq	%r13
  221456:      	popq	%r14
  221458:      	popq	%r15
  22145a:      	popq	%rbp
  22145b:      	retq
  22145c:      	leaq	(%rip), %rcx            # 0x221463 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x283>
		000000000022145f:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221463:      	callq	0x221468 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x288>
		0000000000221464:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221468:      	movq	%rax, %rbx
  22146b:      	movq	%rbx, %rcx
  22146e:      	leaq	(%rip), %rdx            # 0x221475 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x295>
		0000000000221471:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  221475:      	callq	0x9d390 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  22147a:      	movq	%rbx, %rcx
  22147d:      	callq	0x221482 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a2>
		000000000022147e:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221482:      	int3
  221483:      	xorl	%ecx, %ecx
  221485:      	movl	%ecx, 0x10(%r14)
  221489:      	leaq	(%rip), %rcx            # 0x221490 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2b0>
		000000000022148c:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221490:      	callq	0x221495 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2b5>
		0000000000221491:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221495:      	movq	%rax, %rbx
  221498:      	movq	%rbx, %rcx
  22149b:      	leaq	(%rip), %rdx            # 0x2214a2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2c2>
		000000000022149e:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  2214a2:      	callq	0x9d390 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2214a7:      	movq	%rbx, %rcx
  2214aa:      	callq	0x2214af <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2cf>
		00000000002214ab:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2214af:      	int3
  2214b0:      	leaq	(%rip), %rcx            # 0x2214b7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2d7>
		00000000002214b3:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2214b7:      	callq	0x2214bc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2dc>
		00000000002214b8:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2214bc:      	movq	%rax, %rsi
  2214bf:      	movq	%rsi, %rcx
  2214c2:      	leaq	(%rip), %rdx            # 0x2214c9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2e9>
		00000000002214c5:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2214c9:      	callq	0x9d390 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2214ce:      	movq	%rsi, %rcx
  2214d1:      	callq	0x2214d6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f6>
		00000000002214d2:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2214d6:      	int3
  2214d7:      	leaq	0x14(%rdx), %rcx
  2214db:      	callq	0xc9260 <S_P_CoreLib_System_Threading_Interlocked__Decrement>
  2214e0:      	leaq	(%rip), %rcx            # 0x2214e7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x307>
		00000000002214e3:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2214e7:      	callq	0x2214ec <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x30c>
		00000000002214e8:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2214ec:      	movq	%rax, %rbx
  2214ef:      	movq	%rbx, %rcx
  2214f2:      	leaq	(%rip), %rdx            # 0x2214f9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x319>
		00000000002214f5:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2214f9:      	callq	0x9d390 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2214fe:      	movq	%rbx, %rcx
  221501:      	callq	0x221506 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x326>
		0000000000221502:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221506:      	int3
  221507:      	addq	$0x14, %rcx
  22150b:      	callq	0xc9250 <S_P_CoreLib_System_Threading_Interlocked__Increment>
  221510:      	leaq	(%rip), %rcx            # 0x221517 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x337>
		0000000000221513:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221517:      	callq	0x22151c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x33c>
		0000000000221518:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22151c:      	movq	%rax, %rbx
  22151f:      	movq	%rbx, %rcx
  221522:      	leaq	(%rip), %rdx            # 0x221529 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x349>
		0000000000221525:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  221529:      	callq	0x9d390 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  22152e:      	movq	%rbx, %rcx
  221531:      	callq	0x221536 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x356>
		0000000000221532:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221536:      	int3
  221537:      	subq	$0x28, %rsp
  22153b:      	movq	%rcx, %rbx
  22153e:      	leaq	(%rip), %rsi            # 0x221545 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x365>
		0000000000221541:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  221545:      	leaq	(%rip), %rcx            # 0x22154c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x36c>
		0000000000221548:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  22154c:      	cmpb	$0x0, 0x30(%rbp)
  221550:      	cmoveq	%rcx, %rsi
  221554:      	leaq	(%rip), %rcx            # 0x22155b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x37b>
		0000000000221557:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  22155b:      	movl	$0x2, %edx
  221560:      	callq	0x221565 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x385>
		0000000000221561:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  221565:      	movq	%rax, %rdi
  221568:      	leaq	0x10(%rdi), %rcx
  22156c:      	movq	-0xa0(%rbp), %rdx
  221573:      	callq	0x221578 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x398>
		0000000000221574:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  221578:      	leaq	0x18(%rdi), %rcx
  22157c:      	movq	%rbx, %rdx
  22157f:      	callq	0x221584 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3a4>
		0000000000221580:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  221584:      	leaq	(%rip), %rcx            # 0x22158b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3ab>
		0000000000221587:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  22158b:      	callq	0x221590 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3b0>
		000000000022158c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221590:      	movq	%rax, %rbx
  221593:      	movq	%rbx, %rcx
  221596:      	movq	%rsi, %rdx
  221599:      	movq	%rdi, %r8
  22159c:      	callq	0x96690 <S_P_CoreLib_System_AggregateException___ctor_5>
  2215a1:      	movq	%rbx, %rcx
  2215a4:      	callq	0x2215a9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3c9>
		00000000002215a5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2215a9:      	int3
  2215aa:      	subq	$0x28, %rsp
  2215ae:      	movq	%rcx, %rbx
  2215b1:      	movq	%rbx, -0xa0(%rbp)
  2215b8:      	movzbl	0x30(%rbp), %edx
  2215bc:      	movq	0x10(%rbp), %rcx
  2215c0:      	xorl	%r8d, %r8d
  2215c3:      	callq	0x7590 <SomeEngine_ECS_SomeEngine_ECS_World__EndQueryIteration>
  2215c8:      	nop
  2215c9:      	movq	%rbx, %rcx
  2215cc:      	callq	0xe7550 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Capture>
  2215d1:      	movq	%rax, %rcx
  2215d4:      	cmpl	%ecx, (%rcx)
  2215d6:      	callq	0xe75b0 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Throw>
  2215db:      	callq	0x2215e0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x400>
		00000000002215dc:  IMAGE_REL_AMD64_REL32	RhpRethrow
  2215e0:      	int3
  2215e1:      	nop
  2215e2:      	nop
  2215e3:      	nop
  2215e4:      	nop
  2215e5:      	nop
  2215e6:      	nop
  2215e7:      	nop
  2215e8:      	nop
  2215e9:      	nop
  2215ea:      	nop
  2215eb:      	nop
  2215ec:      	nop
  2215ed:      	nop
  2215ee:      	nop
  2215ef:      	nop
