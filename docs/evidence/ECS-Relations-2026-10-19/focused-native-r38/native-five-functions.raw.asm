
benchmarks/SomeEngine.ECS.Relations.Benchmarks/obj/Release/net10.0/win-x64/native/SomeEngine.ECS.Relations.Benchmarks.obj:	file format coff-x86-64

Disassembly of section .managedcode$I:

0000000000160480 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  160480:      	pushq	%r15
  160482:      	pushq	%r14
  160484:      	pushq	%rdi
  160485:      	pushq	%rsi
  160486:      	pushq	%rbp
  160487:      	pushq	%rbx
  160488:      	subq	$0x38, %rsp
  16048c:      	movq	%rcx, %rbx
  16048f:      	movq	%rdx, %rsi
  160492:      	movq	(%rbx), %rdi
  160495:      	movl	0x8(%rbx), %ebp
  160498:      	testl	%ebp, %ebp
  16049a:      	jle	0x160503 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x83>
  16049c:      	xorl	%r14d, %r14d
  16049f:      	movups	(%rdi,%r14), %xmm0
  1604a4:      	movups	%xmm0, 0x28(%rsp)
  1604a9:      	movq	%rsi, %rcx
  1604ac:      	movq	0x28(%rsp), %rdx
  1604b1:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1604b6:      	movq	%rax, %r15
  1604b9:      	testq	%r15, %r15
  1604bc:      	je	0x1604fb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x7b>
  1604be:      	movq	%rsi, %rcx
  1604c1:      	movq	0x30(%rsp), %rdx
  1604c6:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1604cb:      	testq	%rax, %rax
  1604ce:      	je	0x1604fb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x7b>
  1604d0:      	movq	0x8(%r15), %rcx
  1604d4:      	testq	%rcx, (%rax)
  1604d7:      	je	0x1604fb <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x7b>
  1604d9:      	leaq	0x10(%rbx), %rax
  1604dd:      	movl	0x10(%rax), %ecx
  1604e0:      	leal	0x1(%rcx), %edx
  1604e3:      	movl	%edx, 0x10(%rax)
  1604e6:      	cmpl	0x8(%rax), %ecx
  1604e9:      	jae	0x160514 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x94>
  1604eb:      	movq	(%rax), %rax
  1604ee:      	shlq	$0x4, %rcx
  1604f2:      	movups	0x28(%rsp), %xmm0
  1604f7:      	movups	%xmm0, (%rax,%rcx)
  1604fb:      	addq	$0x10, %r14
  1604ff:      	decl	%ebp
  160501:      	jne	0x16049f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_OneWayExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1f>
  160503:      	movb	$0x1, 0x28(%rbx)
  160507:      	addq	$0x38, %rsp
  16050b:      	popq	%rbx
  16050c:      	popq	%rbp
  16050d:      	popq	%rsi
  16050e:      	popq	%rdi
  16050f:      	popq	%r14
  160511:      	popq	%r15
  160513:      	retq
  160514:      	callq	0x10c790 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  160519:      	int3
  16051a:      	nop
  16051b:      	nop
  16051c:      	nop
  16051d:      	nop
  16051e:      	nop
  16051f:      	nop

0000000000160520 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute>:
  160520:      	pushq	%r15
  160522:      	pushq	%r14
  160524:      	pushq	%rdi
  160525:      	pushq	%rsi
  160526:      	pushq	%rbp
  160527:      	pushq	%rbx
  160528:      	subq	$0x38, %rsp
  16052c:      	movq	%rcx, %rbx
  16052f:      	movq	%rdx, %rsi
  160532:      	movq	(%rbx), %rdi
  160535:      	movl	0x8(%rbx), %ebp
  160538:      	testl	%ebp, %ebp
  16053a:      	jle	0x1605ac <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x8c>
  16053c:      	xorl	%r14d, %r14d
  16053f:      	movups	(%rdi,%r14), %xmm0
  160544:      	movups	%xmm0, 0x28(%rsp)
  160549:      	movq	%rsi, %rcx
  16054c:      	movq	0x28(%rsp), %rdx
  160551:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  160556:      	movq	%rax, %r15
  160559:      	testq	%r15, %r15
  16055c:      	je	0x1605a4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  16055e:      	movq	%rsi, %rcx
  160561:      	movq	0x30(%rsp), %rdx
  160566:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  16056b:      	testq	%rax, %rax
  16056e:      	je	0x1605a4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  160570:      	movq	0x8(%r15), %rcx
  160574:      	testq	%rcx, (%rax)
  160577:      	je	0x1605a4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  160579:      	movq	0x8(%rax), %rax
  16057d:      	testq	%rax, (%r15)
  160580:      	je	0x1605a4 <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x84>
  160582:      	leaq	0x10(%rbx), %rax
  160586:      	movl	0x10(%rax), %ecx
  160589:      	leal	0x1(%rcx), %edx
  16058c:      	movl	%edx, 0x10(%rax)
  16058f:      	cmpl	0x8(%rax), %ecx
  160592:      	jae	0x1605bd <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x9d>
  160594:      	movq	(%rax), %rax
  160597:      	shlq	$0x4, %rcx
  16059b:      	movups	0x28(%rsp), %xmm0
  1605a0:      	movups	%xmm0, (%rax,%rcx)
  1605a4:      	addq	$0x10, %r14
  1605a8:      	decl	%ebp
  1605aa:      	jne	0x16053f <SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_HandwrittenBitmapCollision_MutualExecution_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput>__Execute+0x1f>
  1605ac:      	movb	$0x1, 0x28(%rbx)
  1605b0:      	addq	$0x38, %rsp
  1605b4:      	popq	%rbx
  1605b5:      	popq	%rbp
  1605b6:      	popq	%rsi
  1605b7:      	popq	%rdi
  1605b8:      	popq	%r14
  1605ba:      	popq	%r15
  1605bc:      	retq
  1605bd:      	callq	0x10c790 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  1605c2:      	int3
  1605c3:      	nop
  1605c4:      	nop
  1605c5:      	nop
  1605c6:      	nop
  1605c7:      	nop
  1605c8:      	nop
  1605c9:      	nop
  1605ca:      	nop
  1605cb:      	nop
  1605cc:      	nop
  1605cd:      	nop
  1605ce:      	nop
  1605cf:      	nop

00000000001db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>:
  1db3a0:      	pushq	%r14
  1db3a2:      	pushq	%rdi
  1db3a3:      	pushq	%rsi
  1db3a4:      	pushq	%rbp
  1db3a5:      	pushq	%rbx
  1db3a6:      	subq	$0x50, %rsp
  1db3aa:      	xorps	%xmm4, %xmm4
  1db3ad:      	movaps	%xmm4, 0x30(%rsp)
  1db3b2:      	movaps	%xmm4, 0x40(%rsp)
  1db3b7:      	movq	%rdx, 0x88(%rsp)
  1db3bf:      	movq	(%rcx), %rbx
  1db3c2:      	movq	0x8(%rcx), %rsi
  1db3c6:      	movl	0x10(%rcx), %edi
  1db3c9:      	cmpb	%bl, (%rbx)
  1db3cb:      	movq	%gs:0x58, %rcx
  1db3d4:      	movl	(%rip), %eax            # 0x1db3da <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3a>
		00000000001db3d6:  IMAGE_REL_AMD64_REL32	_tls_index
  1db3da:      	movl	$0x0, %edx
		00000000001db3db:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  1db3df:      	addq	(%rcx,%rax,8), %rdx
  1db3e3:      	movq	(%rdx), %rbp
  1db3e6:      	testq	%rbp, %rbp
  1db3e9:      	je	0x1db74c <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3ac>
  1db3ef:      	movq	0x18(%rbp), %rbp
  1db3f3:      	movl	0x88(%rsp), %r14d
  1db3fb:      	testq	%rbp, %rbp
  1db3fe:      	je	0x1db41a <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x7a>
  1db400:      	cmpq	%rbx, 0x8(%rbp)
  1db404:      	je	0x1db424 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x84>
  1db406:      	movq	0x18(%rbp), %rdx
  1db40a:      	movq	%rbx, %rcx
  1db40d:      	callq	0x6400 <SomeEngine_ECS_SomeEngine_ECS_World__FindStructuralCandidate>
  1db412:      	movq	%rax, %rbp
  1db415:      	testq	%rbp, %rbp
  1db418:      	jne	0x1db424 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x84>
  1db41a:      	movq	0x30(%rbx), %rdx
  1db41e:      	movq	0x8(%rdx), %rdx
  1db422:      	jmp	0x1db428 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x88>
  1db424:      	movq	0x10(%rbp), %rdx
  1db428:      	movq	0x18(%rdx), %rdx
  1db42c:      	movq	0x10(%rdx), %rcx
  1db430:      	cmpb	%cl, (%rcx)
  1db432:      	testl	%r14d, %r14d
  1db435:      	jle	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db43b:      	cmpl	0x48(%rcx), %r14d
  1db43f:      	jg	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db445:      	movq	0x10(%rcx), %rdx
  1db449:      	movl	%r14d, %r8d
  1db44c:      	shrl	$0x8, %r8d
  1db450:      	cmpl	0x8(%rdx), %r8d
  1db454:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db45a:      	movq	0x10(%rdx,%r8,8), %rdx
  1db45f:      	movq	0x8(%rdx), %rdx
  1db463:      	movzbl	%r14b, %r8d
  1db467:      	cmpl	0x8(%rdx), %r8d
  1db46b:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db471:      	movzbl	%r14b, %r8d
  1db475:      	shlq	$0x5, %r8
  1db479:      	movl	0x28(%rdx,%r8), %edx
  1db47e:      	cmpl	0x8c(%rsp), %edx
  1db485:      	jne	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db48b:      	leaq	0x30(%rsp), %rdx
  1db490:      	movq	0x88(%rsp), %r8
  1db498:      	callq	0x1b770 <SomeEngine_ECS_SomeEngine_ECS_Entities_EntityStore__GetRecordReadOnly>
  1db49d:      	movq	0x30(%rsp), %rbx
  1db4a2:      	movq	0x38(%rsp), %rbp
  1db4a7:      	movl	0x44(%rsp), %r14d
  1db4ac:      	testq	%rbx, %rbx
  1db4af:      	je	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db4b5:      	movq	0x18(%rsi), %rcx
  1db4b9:      	movq	0x70(%rbx), %rdx
  1db4bd:      	cmpb	%cl, (%rcx)
  1db4bf:      	callq	0x183f20 <S_P_CoreLib_System_Collections_Generic_Dictionary_2<Int64__System___Canon>__FindValue>
  1db4c4:      	testq	%rax, %rax
  1db4c7:      	je	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db4cd:      	movq	(%rax), %rsi
  1db4d0:      	cmpq	%rbx, 0x30(%rsi)
  1db4d4:      	jne	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db4da:      	movq	0x10(%rsi), %rdx
  1db4de:      	xorl	%ecx, %ecx
  1db4e0:      	jmp	0x1db54a <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1aa>
  1db4e2:      	xorl	%r8d, %r8d
  1db4e5:      	cmpl	%r8d, %ecx
  1db4e8:      	jge	0x1db558 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1b8>
  1db4ea:      	movq	0x8(%rbp), %r8
  1db4ee:      	movq	0x18(%r8), %r8
  1db4f2:      	testq	%r8, %r8
  1db4f5:      	je	0x1db501 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x161>
  1db4f7:      	leaq	0x10(%r8), %rax
  1db4fb:      	movl	0x8(%r8), %r8d
  1db4ff:      	jmp	0x1db506 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x166>
  1db501:      	xorl	%eax, %eax
  1db503:      	xorl	%r8d, %r8d
  1db506:      	movq	0x10(%rsi), %r10
  1db50a:      	testq	%r10, %r10
  1db50d:      	je	0x1db519 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x179>
  1db50f:      	leaq	0x10(%r10), %r9
  1db513:      	movl	0x8(%r10), %r10d
  1db517:      	jmp	0x1db51f <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x17f>
  1db519:      	xorl	%r9d, %r9d
  1db51c:      	xorl	%r10d, %r10d
  1db51f:      	cmpl	%r10d, %ecx
  1db522:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db528:      	movl	(%r9,%rcx,4), %r10d
  1db52c:      	cmpl	%r8d, %r10d
  1db52f:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db535:      	movl	%r10d, %r8d
  1db538:      	movl	(%rax,%r8,4), %r8d
  1db53c:      	subl	%edi, %r8d
  1db53f:      	testl	%r8d, %r8d
  1db542:      	jle	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db548:      	incl	%ecx
  1db54a:      	movq	%rdx, %r8
  1db54d:      	testq	%r8, %r8
  1db550:      	je	0x1db4e2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x142>
  1db552:      	movl	0x8(%r8), %r8d
  1db556:      	jmp	0x1db4e5 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x145>
  1db558:      	movq	0x8(%rsi), %rdx
  1db55c:      	xorl	%ecx, %ecx
  1db55e:      	jmp	0x1db562 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1c2>
  1db560:      	incl	%ecx
  1db562:      	movq	%rdx, %r8
  1db565:      	testq	%r8, %r8
  1db568:      	je	0x1db570 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1d0>
  1db56a:      	movl	0x8(%r8), %r8d
  1db56e:      	jmp	0x1db573 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1d3>
  1db570:      	xorl	%r8d, %r8d
  1db573:      	cmpl	%r8d, %ecx
  1db576:      	jge	0x1db5df <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x23f>
  1db578:      	movq	0x8(%rbp), %r8
  1db57c:      	movq	0x18(%r8), %r8
  1db580:      	testq	%r8, %r8
  1db583:      	je	0x1db58f <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1ef>
  1db585:      	leaq	0x10(%r8), %rax
  1db589:      	movl	0x8(%r8), %r8d
  1db58d:      	jmp	0x1db594 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1f4>
  1db58f:      	xorl	%eax, %eax
  1db591:      	xorl	%r8d, %r8d
  1db594:      	movq	0x8(%rsi), %r10
  1db598:      	testq	%r10, %r10
  1db59b:      	je	0x1db5a7 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x207>
  1db59d:      	leaq	0x10(%r10), %r9
  1db5a1:      	movl	0x8(%r10), %r10d
  1db5a5:      	jmp	0x1db5ad <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x20d>
  1db5a7:      	xorl	%r9d, %r9d
  1db5aa:      	xorl	%r10d, %r10d
  1db5ad:      	cmpl	%r10d, %ecx
  1db5b0:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db5b6:      	movl	(%r9,%rcx,8), %r10d
  1db5ba:      	cmpl	%r8d, %r10d
  1db5bd:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db5c3:      	movl	%r10d, %r8d
  1db5c6:      	movl	(%rax,%r8,4), %r8d
  1db5ca:      	subl	%edi, %r8d
  1db5cd:      	testl	%r8d, %r8d
  1db5d0:      	jg	0x1db560 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x1c0>
  1db5d2:      	xorl	%eax, %eax
  1db5d4:      	addq	$0x50, %rsp
  1db5d8:      	popq	%rbx
  1db5d9:      	popq	%rbp
  1db5da:      	popq	%rsi
  1db5db:      	popq	%rdi
  1db5dc:      	popq	%r14
  1db5de:      	retq
  1db5df:      	movq	0x18(%rsi), %rdx
  1db5e3:      	testq	%rdx, %rdx
  1db5e6:      	je	0x1db5ed <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x24d>
  1db5e8:      	movl	0x8(%rdx), %edx
  1db5eb:      	jmp	0x1db5ef <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x24f>
  1db5ed:      	xorl	%edx, %edx
  1db5ef:      	testl	%edx, %edx
  1db5f1:      	jne	0x1db607 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x267>
  1db5f3:      	movq	0x20(%rsi), %rdx
  1db5f7:      	testq	%rdx, %rdx
  1db5fa:      	je	0x1db601 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x261>
  1db5fc:      	movl	0x8(%rdx), %edx
  1db5ff:      	jmp	0x1db603 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x263>
  1db601:      	xorl	%edx, %edx
  1db603:      	testl	%edx, %edx
  1db605:      	je	0x1db638 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x298>
  1db607:      	leaq	0x20(%rsp), %rdx
  1db60c:      	movq	%rsi, %rcx
  1db60f:      	movq	%rbp, %r8
  1db612:      	callq	0xe160 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryArchetypeMatch__RowFilterMask>
  1db617:      	cmpq	$0x0, 0x20(%rsp)
  1db61d:      	je	0x1db626 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x286>
  1db61f:      	movl	$0x1, %ecx
  1db624:      	jmp	0x1db632 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x292>
  1db626:      	cmpq	$0x0, 0x28(%rsp)
  1db62c:      	setne	%cl
  1db62f:      	movzbl	%cl, %ecx
  1db632:      	testl	%ecx, %ecx
  1db634:      	je	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db636:      	jmp	0x1db63e <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x29e>
  1db638:      	cmpl	$0x0, 0x30(%rbp)
  1db63c:      	jle	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db63e:      	movq	%rsi, %rcx
  1db641:      	movq	%rbp, %rdx
  1db644:      	movl	%r14d, %r8d
  1db647:      	movl	%edi, %r9d
  1db64a:      	callq	0xdea0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryArchetypeMatch__MatchesRow>
  1db64f:      	testl	%eax, %eax
  1db651:      	je	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db657:      	leaq	(%rip), %rbx            # 0x1db65e <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2be>
		00000000001db65a:  IMAGE_REL_AMD64_REL32	?__NONGCSTATICS@SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>@@
  1db65e:      	cmpq	$0x0, -0x8(%rbx)
  1db663:      	jne	0x1db760 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3c0>
  1db669:      	movl	(%rbx), %edx
  1db66b:      	movq	0x28(%rsi), %rax
  1db66f:      	xorl	%ecx, %ecx
  1db671:      	jmp	0x1db675 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2d5>
  1db673:      	incl	%ecx
  1db675:      	movq	%rax, %r8
  1db678:      	testq	%r8, %r8
  1db67b:      	je	0x1db683 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2e3>
  1db67d:      	movl	0x8(%r8), %r8d
  1db681:      	jmp	0x1db686 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2e6>
  1db683:      	xorl	%r8d, %r8d
  1db686:      	cmpl	%r8d, %ecx
  1db689:      	jge	0x1db737 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x397>
  1db68f:      	movq	%rax, %r8
  1db692:      	testq	%r8, %r8
  1db695:      	je	0x1db6a1 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x301>
  1db697:      	leaq	0x10(%r8), %r10
  1db69b:      	movl	0x8(%r8), %r8d
  1db69f:      	jmp	0x1db6a7 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x307>
  1db6a1:      	xorl	%r10d, %r10d
  1db6a4:      	xorl	%r8d, %r8d
  1db6a7:      	cmpl	%r8d, %ecx
  1db6aa:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db6b0:      	leaq	(%rcx,%rcx,2), %r8
  1db6b4:      	cmpl	%edx, (%r10,%r8,4)
  1db6b8:      	jne	0x1db673 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2d3>
  1db6ba:      	testq	%rax, %rax
  1db6bd:      	je	0x1db6c8 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x328>
  1db6bf:      	leaq	0x10(%rax), %rdx
  1db6c3:      	movl	0x8(%rax), %eax
  1db6c6:      	jmp	0x1db6cc <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x32c>
  1db6c8:      	xorl	%edx, %edx
  1db6ca:      	xorl	%eax, %eax
  1db6cc:      	cmpl	%eax, %ecx
  1db6ce:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db6d4:      	movl	%ecx, %eax
  1db6d6:      	leaq	(%rax,%rax,2), %rax
  1db6da:      	leaq	(%rdx,%rax,4), %rax
  1db6de:      	movl	0x4(%rax), %ecx
  1db6e1:      	movzbl	0x8(%rax), %eax
  1db6e5:      	cmpl	$0x1, %eax
  1db6e8:      	je	0x1db6ef <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x34f>
  1db6ea:      	cmpl	$0x3, %eax
  1db6ed:      	jne	0x1db76a <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3ca>
  1db6ef:      	movq	0x8(%rbp), %rax
  1db6f3:      	movq	0x10(%rax), %rax
  1db6f7:      	cmpl	0x8(%rax), %ecx
  1db6fa:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db700:      	movl	%ecx, %ecx
  1db702:      	movq	0x10(%rax,%rcx,8), %rax
  1db707:      	testq	%rax, %rax
  1db70a:      	je	0x1db715 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x375>
  1db70c:      	leaq	0x10(%rax), %rcx
  1db710:      	movl	0x8(%rax), %eax
  1db713:      	jmp	0x1db719 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x379>
  1db715:      	xorl	%ecx, %ecx
  1db717:      	xorl	%eax, %eax
  1db719:      	cmpl	%eax, %r14d
  1db71c:      	jae	0x1db7b6 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x416>
  1db722:      	movl	%r14d, %eax
  1db725:      	shlq	$0x4, %rax
  1db729:      	addq	%rcx, %rax
  1db72c:      	addq	$0x50, %rsp
  1db730:      	popq	%rbx
  1db731:      	popq	%rbp
  1db732:      	popq	%rsi
  1db733:      	popq	%rdi
  1db734:      	popq	%r14
  1db736:      	retq
  1db737:      	movq	0x30(%rsi), %rcx
  1db73b:      	cmpl	%ecx, (%rcx)
  1db73d:      	callq	0x1dfd0 <SomeEngine_ECS_SomeEngine_ECS_Archetypes_Archetype__HasComponent>
  1db742:      	testl	%eax, %eax
  1db744:      	je	0x1db5d2 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x232>
  1db74a:      	jmp	0x1db7a8 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x408>
  1db74c:      	movq	%rdx, %rcx
  1db74f:      	leaq	-0xd2426(%rip), %rax    # 0x109330 <S_P_CoreLib_Internal_Runtime_ThreadStatics__GetInlinedThreadStaticBaseSlow>
  1db756:      	callq	*%rax
  1db758:      	movq	%rax, %rbp
  1db75b:      	jmp	0x1db3ef <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x4f>
  1db760:      	callq	0x1db765 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3c5>
		00000000001db761:  IMAGE_REL_AMD64_REL32	__GetNonGCStaticBase_SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>
  1db765:      	jmp	0x1db669 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x2c9>
  1db76a:      	leaq	(%rip), %rcx            # 0x1db771 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3d1>
		00000000001db76d:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  1db771:      	callq	0x1db776 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3d6>
		00000000001db772:  IMAGE_REL_AMD64_REL32	RhpNewFast
  1db776:      	movq	%rax, %rsi
  1db779:      	leaq	(%rip), %rcx            # 0x1db780 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3e0>
		00000000001db77c:  IMAGE_REL_AMD64_REL32	__RuntimeType_SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer
  1db780:      	callq	0x8d2a0 <S_P_CoreLib_System_RuntimeType__get_Name>
  1db785:      	movq	%rax, %rcx
  1db788:      	leaq	(%rip), %rdx            # 0x1db78f <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x3ef>
		00000000001db78b:  IMAGE_REL_AMD64_REL32	__Str__was_not_declared_for_query_re_3DAC58C3D425A8E368C52F9A8DF72803BD0D416C884BD906DC32246D69AA8921
  1db78f:      	callq	0x8ed30 <String__Concat_6>
  1db794:      	movq	%rax, %rdx
  1db797:      	movq	%rsi, %rcx
  1db79a:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  1db79f:      	movq	%rsi, %rcx
  1db7a2:      	callq	0x1db7a7 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x407>
		00000000001db7a3:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  1db7a7:      	int3
  1db7a8:      	callq	0x1db870 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryAccessGuards__UndeclaredAccess<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  1db7ad:      	movq	%rax, %rcx
  1db7b0:      	callq	0x1db7b5 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>+0x415>
		00000000001db7b1:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  1db7b5:      	int3
  1db7b6:      	callq	0x10c790 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  1db7bb:      	int3
  1db7bc:      	nop
  1db7bd:      	nop
  1db7be:      	nop
  1db7bf:      	nop

00000000002246e0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>>:
  2246e0:      	pushq	%rbp
  2246e1:      	pushq	%r15
  2246e3:      	pushq	%r14
  2246e5:      	pushq	%r13
  2246e7:      	pushq	%rdi
  2246e8:      	pushq	%rsi
  2246e9:      	pushq	%rbx
  2246ea:      	subq	$0xa0, %rsp
  2246f1:      	leaq	0xd0(%rsp), %rbp
  2246f9:      	xorps	%xmm4, %xmm4
  2246fc:      	movabsq	$-0x60, %rax
  224706:      	movaps	%xmm4, -0x30(%rbp,%rax)
  22470b:      	movaps	%xmm4, -0x20(%rbp,%rax)
  224710:      	movaps	%xmm4, -0x10(%rbp,%rax)
  224715:      	addq	$0x30, %rax
  224719:      	jne	0x224706 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x26>
  22471b:      	movq	%rcx, 0x10(%rbp)
  22471f:      	movq	%rdx, 0x18(%rbp)
  224723:      	movl	%r8d, %ebx
  224726:      	movl	%r9d, %esi
  224729:      	movl	0x30(%rbp), %eax
  22472c:      	movq	0x38(%rbp), %rdi
  224730:      	testb	%al, %al
  224732:      	jne	0x224755 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x75>
  224734:      	movq	0x38(%rcx), %rdx
  224738:      	cmpl	$0x0, 0x10(%rdx)
  22473c:      	jne	0x224993 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2b3>
  224742:      	leaq	0x14(%rdx), %r8
  224746:      	lock
  224747:      	incl	(%r8)
  22474a:      	cmpl	$0x0, 0x10(%rdx)
  22474e:      	je	0x2247b7 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xd7>
  224750:      	jmp	0x2249ba <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2da>
  224755:      	movq	0x38(%rcx), %r14
  224759:      	cmpb	%r14b, (%r14)
  22475c:      	callq	0xc7b00 <S_P_CoreLib_System_Threading_ManagedThreadId__get_Current>
  224761:      	movl	%eax, %ecx
  224763:      	movl	0x10(%r14), %edx
  224767:      	testl	%edx, %edx
  224769:      	jne	0x22477b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x9b>
  22476b:      	leaq	0x10(%r14), %rdx
  22476f:      	xorl	%eax, %eax
  224771:      	lock
  224772:      	cmpxchgl	%ecx, (%rdx)
  224775:      	movl	%eax, %edx
  224777:      	testl	%edx, %edx
  224779:      	je	0x224783 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xa3>
  22477b:      	cmpl	%ecx, %edx
  22477d:      	jne	0x22493f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x25f>
  224783:      	cmpl	$0x0, 0x8(%r14)
  224788:      	jne	0x2247a0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xc0>
  22478a:      	cmpl	$0x0, 0x14(%r14)
  22478f:      	jne	0x224966 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x286>
  224795:      	cmpl	$0x0, 0x18(%r14)
  22479a:      	jne	0x224966 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x286>
  2247a0:      	movl	0x8(%r14), %ecx
  2247a4:      	testl	%ecx, %ecx
  2247a6:      	jne	0x2247ad <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xcd>
  2247a8:      	movb	$0x0, 0x1c(%r14)
  2247ad:      	incl	%ecx
  2247af:      	movl	%ecx, 0x8(%r14)
  2247b3:      	incl	0xc(%r14)
  2247b7:      	movq	%gs:0x58, %rcx
  2247c0:      	movl	(%rip), %eax            # 0x2247c6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xe6>
		00000000002247c2:  IMAGE_REL_AMD64_REL32	_tls_index
  2247c6:      	movl	$0x0, %edx
		00000000002247c7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2247cb:      	addq	(%rcx,%rax,8), %rdx
  2247cf:      	movq	(%rdx), %r14
  2247d2:      	testq	%r14, %r14
  2247d5:      	je	0x2248e0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x200>
  2247db:      	movq	0x18(%r14), %r14
  2247df:      	testq	%r14, %r14
  2247e2:      	je	0x2247ff <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x11f>
  2247e4:      	movq	0x10(%rbp), %rcx
  2247e8:      	cmpq	%rcx, 0x8(%r14)
  2247ec:      	je	0x22480d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  2247ee:      	movq	0x18(%r14), %rdx
  2247f2:      	callq	0x6400 <SomeEngine_ECS_SomeEngine_ECS_World__FindStructuralCandidate>
  2247f7:      	movq	%rax, %r14
  2247fa:      	testq	%r14, %r14
  2247fd:      	jne	0x22480d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  2247ff:      	movq	0x10(%rbp), %rcx
  224803:      	movq	0x30(%rcx), %rdx
  224807:      	movq	0x8(%rdx), %rdx
  22480b:      	jmp	0x224815 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x135>
  22480d:      	movq	0x10(%rbp), %rcx
  224811:      	movq	0x10(%r14), %rdx
  224815:      	movq	0x40(%rdx), %rcx
  224819:      	movq	0x18(%rbp), %rdx
  22481d:      	cmpl	%ecx, (%rcx)
  22481f:      	callq	0xcea0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRegistry__Get>
  224824:      	movq	0x10(%rax), %rcx
  224828:      	movq	0x10(%rbp), %rax
  22482c:      	movq	%rax, -0x48(%rbp)
  224830:      	movq	%rcx, -0x40(%rbp)
  224834:      	movl	%ebx, -0x38(%rbp)
  224837:      	movl	%esi, -0x34(%rbp)
  22483a:      	cmpb	%dil, (%rdi)
  22483d:      	leaq	0x8(%rdi), %rbx
  224841:      	leaq	-0x90(%rbp), %rcx
  224848:      	leaq	-0x48(%rbp), %rdx
  22484c:      	callq	0x22230 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQueryViews___ctor>
  224851:      	movq	-0x90(%rbp), %rsi
  224858:      	leaq	0x28(%rdi), %rcx
  22485c:      	movq	(%rcx), %r14
  22485f:      	movq	(%rbx), %rbx
  224862:      	movl	0x8(%rbx), %r15d
  224866:      	testl	%r15d, %r15d
  224869:      	jle	0x2248db <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fb>
  22486b:      	addq	$0x10, %rbx
  22486f:      	movups	(%rbx), %xmm0
  224872:      	movups	%xmm0, -0xa0(%rbp)
  224879:      	movq	%rsi, %rcx
  22487c:      	movq	-0xa0(%rbp), %rdx
  224883:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  224888:      	movq	%rax, %r13
  22488b:      	testq	%r13, %r13
  22488e:      	je	0x2248d2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1f2>
  224890:      	movq	%rsi, %rcx
  224893:      	movq	-0x98(%rbp), %rdx
  22489a:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  22489f:      	testq	%rax, %rax
  2248a2:      	je	0x2248d2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1f2>
  2248a4:      	movq	0x8(%r13), %rcx
  2248a8:      	testq	%rcx, (%rax)
  2248ab:      	je	0x2248d2 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1f2>
  2248ad:      	movq	(%r14), %rcx
  2248b0:      	movl	0x10(%rcx), %edx
  2248b3:      	leal	0x1(%rdx), %r8d
  2248b7:      	movl	%r8d, 0x10(%rcx)
  2248bb:      	cmpl	0x8(%rcx), %edx
  2248be:      	jae	0x2248f4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x214>
  2248c0:      	movq	(%rcx), %rcx
  2248c3:      	shlq	$0x4, %rdx
  2248c7:      	movups	-0xa0(%rbp), %xmm0
  2248ce:      	movups	%xmm0, (%rcx,%rdx)
  2248d2:      	addq	$0x10, %rbx
  2248d6:      	decl	%r15d
  2248d9:      	jne	0x22486f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x18f>
  2248db:      	movb	$0x1, (%rdi)
  2248de:      	jmp	0x2248fa <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x21a>
  2248e0:      	movq	%rdx, %rcx
  2248e3:      	leaq	-0x11b5ba(%rip), %rax   # 0x109330 <S_P_CoreLib_Internal_Runtime_ThreadStatics__GetInlinedThreadStaticBaseSlow>
  2248ea:      	callq	*%rax
  2248ec:      	movq	%rax, %r14
  2248ef:      	jmp	0x2247db <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xfb>
  2248f4:      	callq	0x10c790 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  2248f9:      	int3
  2248fa:      	cmpb	$0x0, 0x30(%rbp)
  2248fe:      	jne	0x22491e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x23e>
  224900:      	movq	0x10(%rbp), %rcx
  224904:      	movq	0x38(%rcx), %rcx
  224908:      	leaq	0x14(%rcx), %rax
  22490c:      	movl	$0xffffffff, %edx       # imm = 0xFFFFFFFF
  224911:      	lock
  224912:      	xaddl	%edx, (%rax)
  224915:      	decl	%edx
  224917:      	jns	0x22492c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x24c>
  224919:      	jmp	0x2249ea <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x30a>
  22491e:      	movq	0x10(%rbp), %rcx
  224922:      	movl	$0x1, %edx
  224927:      	callq	0x7330 <SomeEngine_ECS_SomeEngine_ECS_World__EndIteration>
  22492c:      	nop
  22492d:      	addq	$0xa0, %rsp
  224934:      	popq	%rbx
  224935:      	popq	%rsi
  224936:      	popq	%rdi
  224937:      	popq	%r13
  224939:      	popq	%r14
  22493b:      	popq	%r15
  22493d:      	popq	%rbp
  22493e:      	retq
  22493f:      	leaq	(%rip), %rcx            # 0x224946 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x266>
		0000000000224942:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  224946:      	callq	0x22494b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x26b>
		0000000000224947:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22494b:      	movq	%rax, %rbx
  22494e:      	movq	%rbx, %rcx
  224951:      	leaq	(%rip), %rdx            # 0x224958 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x278>
		0000000000224954:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  224958:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  22495d:      	movq	%rbx, %rcx
  224960:      	callq	0x224965 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x285>
		0000000000224961:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224965:      	int3
  224966:      	xorl	%ecx, %ecx
  224968:      	movl	%ecx, 0x10(%r14)
  22496c:      	leaq	(%rip), %rcx            # 0x224973 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x293>
		000000000022496f:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  224973:      	callq	0x224978 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x298>
		0000000000224974:  IMAGE_REL_AMD64_REL32	RhpNewFast
  224978:      	movq	%rax, %rbx
  22497b:      	movq	%rbx, %rcx
  22497e:      	leaq	(%rip), %rdx            # 0x224985 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a5>
		0000000000224981:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  224985:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  22498a:      	movq	%rbx, %rcx
  22498d:      	callq	0x224992 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2b2>
		000000000022498e:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224992:      	int3
  224993:      	leaq	(%rip), %rcx            # 0x22499a <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2ba>
		0000000000224996:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  22499a:      	callq	0x22499f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2bf>
		000000000022499b:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22499f:      	movq	%rax, %rsi
  2249a2:      	movq	%rsi, %rcx
  2249a5:      	leaq	(%rip), %rdx            # 0x2249ac <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2cc>
		00000000002249a8:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2249ac:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2249b1:      	movq	%rsi, %rcx
  2249b4:      	callq	0x2249b9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2d9>
		00000000002249b5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2249b9:      	int3
  2249ba:      	leaq	0x14(%rdx), %rcx
  2249be:      	callq	0xc9100 <S_P_CoreLib_System_Threading_Interlocked__Decrement>
  2249c3:      	leaq	(%rip), %rcx            # 0x2249ca <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2ea>
		00000000002249c6:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2249ca:      	callq	0x2249cf <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2ef>
		00000000002249cb:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2249cf:      	movq	%rax, %rbx
  2249d2:      	movq	%rbx, %rcx
  2249d5:      	leaq	(%rip), %rdx            # 0x2249dc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2fc>
		00000000002249d8:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2249dc:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2249e1:      	movq	%rbx, %rcx
  2249e4:      	callq	0x2249e9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x309>
		00000000002249e5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2249e9:      	int3
  2249ea:      	addq	$0x14, %rcx
  2249ee:      	callq	0xc90f0 <S_P_CoreLib_System_Threading_Interlocked__Increment>
  2249f3:      	leaq	(%rip), %rcx            # 0x2249fa <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x31a>
		00000000002249f6:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2249fa:      	callq	0x2249ff <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x31f>
		00000000002249fb:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2249ff:      	movq	%rax, %rbx
  224a02:      	movq	%rbx, %rcx
  224a05:      	leaq	(%rip), %rdx            # 0x224a0c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x32c>
		0000000000224a08:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  224a0c:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  224a11:      	movq	%rbx, %rcx
  224a14:      	callq	0x224a19 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x339>
		0000000000224a15:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224a19:      	int3
  224a1a:      	subq	$0x28, %rsp
  224a1e:      	movq	%rcx, %rbx
  224a21:      	leaq	(%rip), %rsi            # 0x224a28 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x348>
		0000000000224a24:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  224a28:      	leaq	(%rip), %rcx            # 0x224a2f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x34f>
		0000000000224a2b:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  224a2f:      	cmpb	$0x0, 0x30(%rbp)
  224a33:      	cmoveq	%rcx, %rsi
  224a37:      	leaq	(%rip), %rcx            # 0x224a3e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x35e>
		0000000000224a3a:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  224a3e:      	movl	$0x2, %edx
  224a43:      	callq	0x224a48 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x368>
		0000000000224a44:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  224a48:      	movq	%rax, %rdi
  224a4b:      	leaq	0x10(%rdi), %rcx
  224a4f:      	movq	-0xa8(%rbp), %rdx
  224a56:      	callq	0x224a5b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x37b>
		0000000000224a57:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  224a5b:      	leaq	0x18(%rdi), %rcx
  224a5f:      	movq	%rbx, %rdx
  224a62:      	callq	0x224a67 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x387>
		0000000000224a63:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  224a67:      	leaq	(%rip), %rcx            # 0x224a6e <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x38e>
		0000000000224a6a:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  224a6e:      	callq	0x224a73 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x393>
		0000000000224a6f:  IMAGE_REL_AMD64_REL32	RhpNewFast
  224a73:      	movq	%rax, %rbx
  224a76:      	movq	%rbx, %rcx
  224a79:      	movq	%rsi, %rdx
  224a7c:      	movq	%rdi, %r8
  224a7f:      	callq	0x96530 <S_P_CoreLib_System_AggregateException___ctor_5>
  224a84:      	movq	%rbx, %rcx
  224a87:      	callq	0x224a8c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3ac>
		0000000000224a88:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224a8c:      	int3
  224a8d:      	subq	$0x28, %rsp
  224a91:      	movq	%rcx, %rbx
  224a94:      	movq	%rbx, -0xa8(%rbp)
  224a9b:      	movzbl	0x30(%rbp), %edx
  224a9f:      	movq	0x10(%rbp), %rcx
  224aa3:      	xorl	%r8d, %r8d
  224aa6:      	callq	0x7670 <SomeEngine_ECS_SomeEngine_ECS_World__EndQueryIteration>
  224aab:      	nop
  224aac:      	movq	%rbx, %rcx
  224aaf:      	callq	0xe73f0 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Capture>
  224ab4:      	movq	%rax, %rcx
  224ab7:      	cmpl	%ecx, (%rcx)
  224ab9:      	callq	0xe7450 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Throw>
  224abe:      	callq	0x224ac3 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_OneWayContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_OneWayPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3e3>
		0000000000224abf:  IMAGE_REL_AMD64_REL32	RhpRethrow
  224ac3:      	int3
  224ac4:      	nop
  224ac5:      	nop
  224ac6:      	nop
  224ac7:      	nop
  224ac8:      	nop
  224ac9:      	nop
  224aca:      	nop
  224acb:      	nop
  224acc:      	nop
  224acd:      	nop
  224ace:      	nop
  224acf:      	nop

00000000002253c0 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>>:
  2253c0:      	pushq	%rbp
  2253c1:      	pushq	%r15
  2253c3:      	pushq	%r14
  2253c5:      	pushq	%r13
  2253c7:      	pushq	%rdi
  2253c8:      	pushq	%rsi
  2253c9:      	pushq	%rbx
  2253ca:      	subq	$0xa0, %rsp
  2253d1:      	leaq	0xd0(%rsp), %rbp
  2253d9:      	xorps	%xmm4, %xmm4
  2253dc:      	movabsq	$-0x60, %rax
  2253e6:      	movaps	%xmm4, -0x30(%rbp,%rax)
  2253eb:      	movaps	%xmm4, -0x20(%rbp,%rax)
  2253f0:      	movaps	%xmm4, -0x10(%rbp,%rax)
  2253f5:      	addq	$0x30, %rax
  2253f9:      	jne	0x2253e6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x26>
  2253fb:      	movq	%rcx, 0x10(%rbp)
  2253ff:      	movq	%rdx, 0x18(%rbp)
  225403:      	movl	%r8d, %ebx
  225406:      	movl	%r9d, %esi
  225409:      	movl	0x30(%rbp), %eax
  22540c:      	movq	0x38(%rbp), %rdi
  225410:      	testb	%al, %al
  225412:      	jne	0x225435 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x75>
  225414:      	movq	0x38(%rcx), %rdx
  225418:      	cmpl	$0x0, 0x10(%rdx)
  22541c:      	jne	0x22567d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2bd>
  225422:      	leaq	0x14(%rdx), %r8
  225426:      	lock
  225427:      	incl	(%r8)
  22542a:      	cmpl	$0x0, 0x10(%rdx)
  22542e:      	je	0x225497 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xd7>
  225430:      	jmp	0x2256a4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2e4>
  225435:      	movq	0x38(%rcx), %r14
  225439:      	cmpb	%r14b, (%r14)
  22543c:      	callq	0xc7b00 <S_P_CoreLib_System_Threading_ManagedThreadId__get_Current>
  225441:      	movl	%eax, %ecx
  225443:      	movl	0x10(%r14), %edx
  225447:      	testl	%edx, %edx
  225449:      	jne	0x22545b <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x9b>
  22544b:      	leaq	0x10(%r14), %rdx
  22544f:      	xorl	%eax, %eax
  225451:      	lock
  225452:      	cmpxchgl	%ecx, (%rdx)
  225455:      	movl	%eax, %edx
  225457:      	testl	%edx, %edx
  225459:      	je	0x225463 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xa3>
  22545b:      	cmpl	%ecx, %edx
  22545d:      	jne	0x225629 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x269>
  225463:      	cmpl	$0x0, 0x8(%r14)
  225468:      	jne	0x225480 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xc0>
  22546a:      	cmpl	$0x0, 0x14(%r14)
  22546f:      	jne	0x225650 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x290>
  225475:      	cmpl	$0x0, 0x18(%r14)
  22547a:      	jne	0x225650 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x290>
  225480:      	movl	0x8(%r14), %ecx
  225484:      	testl	%ecx, %ecx
  225486:      	jne	0x22548d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xcd>
  225488:      	movb	$0x0, 0x1c(%r14)
  22548d:      	incl	%ecx
  22548f:      	movl	%ecx, 0x8(%r14)
  225493:      	incl	0xc(%r14)
  225497:      	movq	%gs:0x58, %rcx
  2254a0:      	movl	(%rip), %eax            # 0x2254a6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xe6>
		00000000002254a2:  IMAGE_REL_AMD64_REL32	_tls_index
  2254a6:      	movl	$0x0, %edx
		00000000002254a7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2254ab:      	addq	(%rcx,%rax,8), %rdx
  2254af:      	movq	(%rdx), %r14
  2254b2:      	testq	%r14, %r14
  2254b5:      	je	0x2255ca <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x20a>
  2254bb:      	movq	0x18(%r14), %r14
  2254bf:      	testq	%r14, %r14
  2254c2:      	je	0x2254df <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x11f>
  2254c4:      	movq	0x10(%rbp), %rcx
  2254c8:      	cmpq	%rcx, 0x8(%r14)
  2254cc:      	je	0x2254ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  2254ce:      	movq	0x18(%r14), %rdx
  2254d2:      	callq	0x6400 <SomeEngine_ECS_SomeEngine_ECS_World__FindStructuralCandidate>
  2254d7:      	movq	%rax, %r14
  2254da:      	testq	%r14, %r14
  2254dd:      	jne	0x2254ed <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x12d>
  2254df:      	movq	0x10(%rbp), %rcx
  2254e3:      	movq	0x30(%rcx), %rdx
  2254e7:      	movq	0x8(%rdx), %rdx
  2254eb:      	jmp	0x2254f5 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x135>
  2254ed:      	movq	0x10(%rbp), %rcx
  2254f1:      	movq	0x10(%r14), %rdx
  2254f5:      	movq	0x40(%rdx), %rcx
  2254f9:      	movq	0x18(%rbp), %rdx
  2254fd:      	cmpl	%ecx, (%rcx)
  2254ff:      	callq	0xcea0 <SomeEngine_ECS_SomeEngine_ECS_Queries_QueryRegistry__Get>
  225504:      	movq	0x10(%rax), %rcx
  225508:      	movq	0x10(%rbp), %rax
  22550c:      	movq	%rax, -0x48(%rbp)
  225510:      	movq	%rcx, -0x40(%rbp)
  225514:      	movl	%ebx, -0x38(%rbp)
  225517:      	movl	%esi, -0x34(%rbp)
  22551a:      	cmpb	%dil, (%rdi)
  22551d:      	leaq	0x8(%rdi), %rbx
  225521:      	leaq	-0x90(%rbp), %rcx
  225528:      	leaq	-0x48(%rbp), %rdx
  22552c:      	callq	0x22230 <SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQueryViews___ctor>
  225531:      	movq	-0x90(%rbp), %rsi
  225538:      	leaq	0x28(%rdi), %rcx
  22553c:      	movq	(%rcx), %r14
  22553f:      	movq	(%rbx), %rbx
  225542:      	movl	0x8(%rbx), %r15d
  225546:      	testl	%r15d, %r15d
  225549:      	jle	0x2255c5 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x205>
  22554b:      	addq	$0x10, %rbx
  22554f:      	movups	(%rbx), %xmm0
  225552:      	movups	%xmm0, -0xa0(%rbp)
  225559:      	movq	%rsi, %rcx
  22555c:      	movq	-0xa0(%rbp), %rdx
  225563:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  225568:      	movq	%rax, %r13
  22556b:      	testq	%r13, %r13
  22556e:      	je	0x2255bc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fc>
  225570:      	movq	%rsi, %rcx
  225573:      	movq	-0x98(%rbp), %rdx
  22557a:      	callq	0x1db3a0 <SomeEngine_ECS_SomeEngine_ECS_World__ReadEntityQueryRef<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>>
  22557f:      	testq	%rax, %rax
  225582:      	je	0x2255bc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fc>
  225584:      	movq	0x8(%r13), %rcx
  225588:      	testq	%rcx, (%rax)
  22558b:      	je	0x2255bc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fc>
  22558d:      	movq	0x8(%rax), %rcx
  225591:      	testq	%rcx, (%r13)
  225595:      	je	0x2255bc <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x1fc>
  225597:      	movq	(%r14), %rcx
  22559a:      	movl	0x10(%rcx), %edx
  22559d:      	leal	0x1(%rdx), %r8d
  2255a1:      	movl	%r8d, 0x10(%rcx)
  2255a5:      	cmpl	0x8(%rcx), %edx
  2255a8:      	jae	0x2255de <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x21e>
  2255aa:      	movq	(%rcx), %rcx
  2255ad:      	shlq	$0x4, %rdx
  2255b1:      	movups	-0xa0(%rbp), %xmm0
  2255b8:      	movups	%xmm0, (%rcx,%rdx)
  2255bc:      	addq	$0x10, %rbx
  2255c0:      	decl	%r15d
  2255c3:      	jne	0x22554f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x18f>
  2255c5:      	movb	$0x1, (%rdi)
  2255c8:      	jmp	0x2255e4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x224>
  2255ca:      	movq	%rdx, %rcx
  2255cd:      	leaq	-0x11c2a4(%rip), %rax   # 0x109330 <S_P_CoreLib_Internal_Runtime_ThreadStatics__GetInlinedThreadStaticBaseSlow>
  2255d4:      	callq	*%rax
  2255d6:      	movq	%rax, %r14
  2255d9:      	jmp	0x2254bb <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0xfb>
  2255de:      	callq	0x10c790 <S_P_CoreLib_Internal_Runtime_CompilerHelpers_ThrowHelpers__ThrowIndexOutOfRangeException>
  2255e3:      	int3
  2255e4:      	cmpb	$0x0, 0x30(%rbp)
  2255e8:      	jne	0x225608 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x248>
  2255ea:      	movq	0x10(%rbp), %rcx
  2255ee:      	movq	0x38(%rcx), %rcx
  2255f2:      	leaq	0x14(%rcx), %rax
  2255f6:      	movl	$0xffffffff, %edx       # imm = 0xFFFFFFFF
  2255fb:      	lock
  2255fc:      	xaddl	%edx, (%rax)
  2255ff:      	decl	%edx
  225601:      	jns	0x225616 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x256>
  225603:      	jmp	0x2256d4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x314>
  225608:      	movq	0x10(%rbp), %rcx
  22560c:      	movl	$0x1, %edx
  225611:      	callq	0x7330 <SomeEngine_ECS_SomeEngine_ECS_World__EndIteration>
  225616:      	nop
  225617:      	addq	$0xa0, %rsp
  22561e:      	popq	%rbx
  22561f:      	popq	%rsi
  225620:      	popq	%rdi
  225621:      	popq	%r13
  225623:      	popq	%r14
  225625:      	popq	%r15
  225627:      	popq	%rbp
  225628:      	retq
  225629:      	leaq	(%rip), %rcx            # 0x225630 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x270>
		000000000022562c:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  225630:      	callq	0x225635 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x275>
		0000000000225631:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225635:      	movq	%rax, %rbx
  225638:      	movq	%rbx, %rcx
  22563b:      	leaq	(%rip), %rdx            # 0x225642 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x282>
		000000000022563e:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  225642:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  225647:      	movq	%rbx, %rcx
  22564a:      	callq	0x22564f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x28f>
		000000000022564b:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  22564f:      	int3
  225650:      	xorl	%ecx, %ecx
  225652:      	movl	%ecx, 0x10(%r14)
  225656:      	leaq	(%rip), %rcx            # 0x22565d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x29d>
		0000000000225659:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  22565d:      	callq	0x225662 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2a2>
		000000000022565e:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225662:      	movq	%rax, %rbx
  225665:      	movq	%rbx, %rcx
  225668:      	leaq	(%rip), %rdx            # 0x22566f <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2af>
		000000000022566b:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  22566f:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  225674:      	movq	%rbx, %rcx
  225677:      	callq	0x22567c <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2bc>
		0000000000225678:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  22567c:      	int3
  22567d:      	leaq	(%rip), %rcx            # 0x225684 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2c4>
		0000000000225680:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  225684:      	callq	0x225689 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2c9>
		0000000000225685:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225689:      	movq	%rax, %rsi
  22568c:      	movq	%rsi, %rcx
  22568f:      	leaq	(%rip), %rdx            # 0x225696 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2d6>
		0000000000225692:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  225696:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  22569b:      	movq	%rsi, %rcx
  22569e:      	callq	0x2256a3 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2e3>
		000000000022569f:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2256a3:      	int3
  2256a4:      	leaq	0x14(%rdx), %rcx
  2256a8:      	callq	0xc9100 <S_P_CoreLib_System_Threading_Interlocked__Decrement>
  2256ad:      	leaq	(%rip), %rcx            # 0x2256b4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f4>
		00000000002256b0:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2256b4:      	callq	0x2256b9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x2f9>
		00000000002256b5:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2256b9:      	movq	%rax, %rbx
  2256bc:      	movq	%rbx, %rcx
  2256bf:      	leaq	(%rip), %rdx            # 0x2256c6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x306>
		00000000002256c2:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2256c6:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2256cb:      	movq	%rbx, %rcx
  2256ce:      	callq	0x2256d3 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x313>
		00000000002256cf:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2256d3:      	int3
  2256d4:      	addq	$0x14, %rcx
  2256d8:      	callq	0xc90f0 <S_P_CoreLib_System_Threading_Interlocked__Increment>
  2256dd:      	leaq	(%rip), %rcx            # 0x2256e4 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x324>
		00000000002256e0:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2256e4:      	callq	0x2256e9 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x329>
		00000000002256e5:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2256e9:      	movq	%rax, %rbx
  2256ec:      	movq	%rbx, %rcx
  2256ef:      	leaq	(%rip), %rdx            # 0x2256f6 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x336>
		00000000002256f2:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  2256f6:      	callq	0x9d230 <S_P_CoreLib_System_InvalidOperationException___ctor_0>
  2256fb:      	movq	%rbx, %rcx
  2256fe:      	callq	0x225703 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x343>
		00000000002256ff:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  225703:      	int3
  225704:      	subq	$0x28, %rsp
  225708:      	movq	%rcx, %rbx
  22570b:      	leaq	(%rip), %rsi            # 0x225712 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x352>
		000000000022570e:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  225712:      	leaq	(%rip), %rcx            # 0x225719 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x359>
		0000000000225715:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  225719:      	cmpb	$0x0, 0x30(%rbp)
  22571d:      	cmoveq	%rcx, %rsi
  225721:      	leaq	(%rip), %rcx            # 0x225728 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x368>
		0000000000225724:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  225728:      	movl	$0x2, %edx
  22572d:      	callq	0x225732 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x372>
		000000000022572e:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  225732:      	movq	%rax, %rdi
  225735:      	leaq	0x10(%rdi), %rcx
  225739:      	movq	-0xa8(%rbp), %rdx
  225740:      	callq	0x225745 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x385>
		0000000000225741:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  225745:      	leaq	0x18(%rdi), %rcx
  225749:      	movq	%rbx, %rdx
  22574c:      	callq	0x225751 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x391>
		000000000022574d:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  225751:      	leaq	(%rip), %rcx            # 0x225758 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x398>
		0000000000225754:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  225758:      	callq	0x22575d <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x39d>
		0000000000225759:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22575d:      	movq	%rax, %rbx
  225760:      	movq	%rbx, %rcx
  225763:      	movq	%rsi, %rdx
  225766:      	movq	%rdi, %r8
  225769:      	callq	0x96530 <S_P_CoreLib_System_AggregateException___ctor_5>
  22576e:      	movq	%rbx, %rcx
  225771:      	callq	0x225776 <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3b6>
		0000000000225772:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  225776:      	int3
  225777:      	subq	$0x28, %rsp
  22577b:      	movq	%rcx, %rbx
  22577e:      	movq	%rbx, -0xa8(%rbp)
  225785:      	movzbl	0x30(%rbp), %edx
  225789:      	movq	0x10(%rbp), %rcx
  22578d:      	xorl	%r8d, %r8d
  225790:      	callq	0x7670 <SomeEngine_ECS_SomeEngine_ECS_World__EndQueryIteration>
  225795:      	nop
  225796:      	movq	%rbx, %rcx
  225799:      	callq	0xe73f0 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Capture>
  22579e:      	movq	%rax, %rcx
  2257a1:      	cmpl	%ecx, (%rcx)
  2257a3:      	callq	0xe7450 <S_P_CoreLib_System_Runtime_ExceptionServices_ExceptionDispatchInfo__Throw>
  2257a8:      	callq	0x2257ad <SomeEngine_ECS_SomeEngine_ECS_World__ExecuteQueryAdmitted<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_RelationQuery_2_SingleExecution_1<SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_BitmapFilter_6<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_FixedCandidatePairs__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContacts__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_MutualContactBitmap__SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_MutualPermission__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_WhereSelection>__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>__SomeEngine_ECS_Relations_SomeEngine_ECS_Relations_QueryIdentityOutput_2<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Spatial_SpatialPairOutput__S_P_CoreLib_System_ValueTuple_2<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity__SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>>>>+0x3ed>
		00000000002257a9:  IMAGE_REL_AMD64_REL32	RhpRethrow
  2257ad:      	int3
  2257ae:      	nop
  2257af:      	nop
