00000000001db3a0 <symbol>
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
  1db3d4:      	movl	(%rip), %eax
		00000000001db3d6:  IMAGE_REL_AMD64_REL32	_tls_index
  1db3da:      	movl	$0x0, %edx
		00000000001db3db:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  1db3df:      	addq	(%rcx,%rax,8), %rdx
  1db3e3:      	movq	(%rdx), %rbp
  1db3e6:      	testq	%rbp, %rbp
  1db3e9:      	je	0x1db74c <symbol>
  1db3ef:      	movq	0x18(%rbp), %rbp
  1db3f3:      	movl	0x88(%rsp), %r14d
  1db3fb:      	testq	%rbp, %rbp
  1db3fe:      	je	0x1db41a <symbol>
  1db400:      	cmpq	%rbx, 0x8(%rbp)
  1db404:      	je	0x1db424 <symbol>
  1db406:      	movq	0x18(%rbp), %rdx
  1db40a:      	movq	%rbx, %rcx
  1db40d:      	callq	0x6400 <symbol>
  1db412:      	movq	%rax, %rbp
  1db415:      	testq	%rbp, %rbp
  1db418:      	jne	0x1db424 <symbol>
  1db41a:      	movq	0x30(%rbx), %rdx
  1db41e:      	movq	0x8(%rdx), %rdx
  1db422:      	jmp	0x1db428 <symbol>
  1db424:      	movq	0x10(%rbp), %rdx
  1db428:      	movq	0x18(%rdx), %rdx
  1db42c:      	movq	0x10(%rdx), %rcx
  1db430:      	cmpb	%cl, (%rcx)
  1db432:      	testl	%r14d, %r14d
  1db435:      	jle	0x1db5d2 <symbol>
  1db43b:      	cmpl	0x48(%rcx), %r14d
  1db43f:      	jg	0x1db5d2 <symbol>
  1db445:      	movq	0x10(%rcx), %rdx
  1db449:      	movl	%r14d, %r8d
  1db44c:      	shrl	$0x8, %r8d
  1db450:      	cmpl	0x8(%rdx), %r8d
  1db454:      	jae	0x1db7b6 <symbol>
  1db45a:      	movq	0x10(%rdx,%r8,8), %rdx
  1db45f:      	movq	0x8(%rdx), %rdx
  1db463:      	movzbl	%r14b, %r8d
  1db467:      	cmpl	0x8(%rdx), %r8d
  1db46b:      	jae	0x1db7b6 <symbol>
  1db471:      	movzbl	%r14b, %r8d
  1db475:      	shlq	$0x5, %r8
  1db479:      	movl	0x28(%rdx,%r8), %edx
  1db47e:      	cmpl	0x8c(%rsp), %edx
  1db485:      	jne	0x1db5d2 <symbol>
  1db48b:      	leaq	0x30(%rsp), %rdx
  1db490:      	movq	0x88(%rsp), %r8
  1db498:      	callq	0x1b770 <symbol>
  1db49d:      	movq	0x30(%rsp), %rbx
  1db4a2:      	movq	0x38(%rsp), %rbp
  1db4a7:      	movl	0x44(%rsp), %r14d
  1db4ac:      	testq	%rbx, %rbx
  1db4af:      	je	0x1db5d2 <symbol>
  1db4b5:      	movq	0x18(%rsi), %rcx
  1db4b9:      	movq	0x70(%rbx), %rdx
  1db4bd:      	cmpb	%cl, (%rcx)
  1db4bf:      	callq	0x183f20 <symbol>
  1db4c4:      	testq	%rax, %rax
  1db4c7:      	je	0x1db5d2 <symbol>
  1db4cd:      	movq	(%rax), %rsi
  1db4d0:      	cmpq	%rbx, 0x30(%rsi)
  1db4d4:      	jne	0x1db5d2 <symbol>
  1db4da:      	movq	0x10(%rsi), %rdx
  1db4de:      	xorl	%ecx, %ecx
  1db4e0:      	jmp	0x1db54a <symbol>
  1db4e2:      	xorl	%r8d, %r8d
  1db4e5:      	cmpl	%r8d, %ecx
  1db4e8:      	jge	0x1db558 <symbol>
  1db4ea:      	movq	0x8(%rbp), %r8
  1db4ee:      	movq	0x18(%r8), %r8
  1db4f2:      	testq	%r8, %r8
  1db4f5:      	je	0x1db501 <symbol>
  1db4f7:      	leaq	0x10(%r8), %rax
  1db4fb:      	movl	0x8(%r8), %r8d
  1db4ff:      	jmp	0x1db506 <symbol>
  1db501:      	xorl	%eax, %eax
  1db503:      	xorl	%r8d, %r8d
  1db506:      	movq	0x10(%rsi), %r10
  1db50a:      	testq	%r10, %r10
  1db50d:      	je	0x1db519 <symbol>
  1db50f:      	leaq	0x10(%r10), %r9
  1db513:      	movl	0x8(%r10), %r10d
  1db517:      	jmp	0x1db51f <symbol>
  1db519:      	xorl	%r9d, %r9d
  1db51c:      	xorl	%r10d, %r10d
  1db51f:      	cmpl	%r10d, %ecx
  1db522:      	jae	0x1db7b6 <symbol>
  1db528:      	movl	(%r9,%rcx,4), %r10d
  1db52c:      	cmpl	%r8d, %r10d
  1db52f:      	jae	0x1db7b6 <symbol>
  1db535:      	movl	%r10d, %r8d
  1db538:      	movl	(%rax,%r8,4), %r8d
  1db53c:      	subl	%edi, %r8d
  1db53f:      	testl	%r8d, %r8d
  1db542:      	jle	0x1db5d2 <symbol>
  1db548:      	incl	%ecx
  1db54a:      	movq	%rdx, %r8
  1db54d:      	testq	%r8, %r8
  1db550:      	je	0x1db4e2 <symbol>
  1db552:      	movl	0x8(%r8), %r8d
  1db556:      	jmp	0x1db4e5 <symbol>
  1db558:      	movq	0x8(%rsi), %rdx
  1db55c:      	xorl	%ecx, %ecx
  1db55e:      	jmp	0x1db562 <symbol>
  1db560:      	incl	%ecx
  1db562:      	movq	%rdx, %r8
  1db565:      	testq	%r8, %r8
  1db568:      	je	0x1db570 <symbol>
  1db56a:      	movl	0x8(%r8), %r8d
  1db56e:      	jmp	0x1db573 <symbol>
  1db570:      	xorl	%r8d, %r8d
  1db573:      	cmpl	%r8d, %ecx
  1db576:      	jge	0x1db5df <symbol>
  1db578:      	movq	0x8(%rbp), %r8
  1db57c:      	movq	0x18(%r8), %r8
  1db580:      	testq	%r8, %r8
  1db583:      	je	0x1db58f <symbol>
  1db585:      	leaq	0x10(%r8), %rax
  1db589:      	movl	0x8(%r8), %r8d
  1db58d:      	jmp	0x1db594 <symbol>
  1db58f:      	xorl	%eax, %eax
  1db591:      	xorl	%r8d, %r8d
  1db594:      	movq	0x8(%rsi), %r10
  1db598:      	testq	%r10, %r10
  1db59b:      	je	0x1db5a7 <symbol>
  1db59d:      	leaq	0x10(%r10), %r9
  1db5a1:      	movl	0x8(%r10), %r10d
  1db5a5:      	jmp	0x1db5ad <symbol>
  1db5a7:      	xorl	%r9d, %r9d
  1db5aa:      	xorl	%r10d, %r10d
  1db5ad:      	cmpl	%r10d, %ecx
  1db5b0:      	jae	0x1db7b6 <symbol>
  1db5b6:      	movl	(%r9,%rcx,8), %r10d
  1db5ba:      	cmpl	%r8d, %r10d
  1db5bd:      	jae	0x1db7b6 <symbol>
  1db5c3:      	movl	%r10d, %r8d
  1db5c6:      	movl	(%rax,%r8,4), %r8d
  1db5ca:      	subl	%edi, %r8d
  1db5cd:      	testl	%r8d, %r8d
  1db5d0:      	jg	0x1db560 <symbol>
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
  1db5e6:      	je	0x1db5ed <symbol>
  1db5e8:      	movl	0x8(%rdx), %edx
  1db5eb:      	jmp	0x1db5ef <symbol>
  1db5ed:      	xorl	%edx, %edx
  1db5ef:      	testl	%edx, %edx
  1db5f1:      	jne	0x1db607 <symbol>
  1db5f3:      	movq	0x20(%rsi), %rdx
  1db5f7:      	testq	%rdx, %rdx
  1db5fa:      	je	0x1db601 <symbol>
  1db5fc:      	movl	0x8(%rdx), %edx
  1db5ff:      	jmp	0x1db603 <symbol>
  1db601:      	xorl	%edx, %edx
  1db603:      	testl	%edx, %edx
  1db605:      	je	0x1db638 <symbol>
  1db607:      	leaq	0x20(%rsp), %rdx
  1db60c:      	movq	%rsi, %rcx
  1db60f:      	movq	%rbp, %r8
  1db612:      	callq	0xe160 <symbol>
  1db617:      	cmpq	$0x0, 0x20(%rsp)
  1db61d:      	je	0x1db626 <symbol>
  1db61f:      	movl	$0x1, %ecx
  1db624:      	jmp	0x1db632 <symbol>
  1db626:      	cmpq	$0x0, 0x28(%rsp)
  1db62c:      	setne	%cl
  1db62f:      	movzbl	%cl, %ecx
  1db632:      	testl	%ecx, %ecx
  1db634:      	je	0x1db5d2 <symbol>
  1db636:      	jmp	0x1db63e <symbol>
  1db638:      	cmpl	$0x0, 0x30(%rbp)
  1db63c:      	jle	0x1db5d2 <symbol>
  1db63e:      	movq	%rsi, %rcx
  1db641:      	movq	%rbp, %rdx
  1db644:      	movl	%r14d, %r8d
  1db647:      	movl	%edi, %r9d
  1db64a:      	callq	0xdea0 <symbol>
  1db64f:      	testl	%eax, %eax
  1db651:      	je	0x1db5d2 <symbol>
  1db657:      	leaq	(%rip), %rbx
		00000000001db65a:  IMAGE_REL_AMD64_REL32	?__NONGCSTATICS@SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>@@
  1db65e:      	cmpq	$0x0, -0x8(%rbx)
  1db663:      	jne	0x1db760 <symbol>
  1db669:      	movl	(%rbx), %edx
  1db66b:      	movq	0x28(%rsi), %rax
  1db66f:      	xorl	%ecx, %ecx
  1db671:      	jmp	0x1db675 <symbol>
  1db673:      	incl	%ecx
  1db675:      	movq	%rax, %r8
  1db678:      	testq	%r8, %r8
  1db67b:      	je	0x1db683 <symbol>
  1db67d:      	movl	0x8(%r8), %r8d
  1db681:      	jmp	0x1db686 <symbol>
  1db683:      	xorl	%r8d, %r8d
  1db686:      	cmpl	%r8d, %ecx
  1db689:      	jge	0x1db737 <symbol>
  1db68f:      	movq	%rax, %r8
  1db692:      	testq	%r8, %r8
  1db695:      	je	0x1db6a1 <symbol>
  1db697:      	leaq	0x10(%r8), %r10
  1db69b:      	movl	0x8(%r8), %r8d
  1db69f:      	jmp	0x1db6a7 <symbol>
  1db6a1:      	xorl	%r10d, %r10d
  1db6a4:      	xorl	%r8d, %r8d
  1db6a7:      	cmpl	%r8d, %ecx
  1db6aa:      	jae	0x1db7b6 <symbol>
  1db6b0:      	leaq	(%rcx,%rcx,2), %r8
  1db6b4:      	cmpl	%edx, (%r10,%r8,4)
  1db6b8:      	jne	0x1db673 <symbol>
  1db6ba:      	testq	%rax, %rax
  1db6bd:      	je	0x1db6c8 <symbol>
  1db6bf:      	leaq	0x10(%rax), %rdx
  1db6c3:      	movl	0x8(%rax), %eax
  1db6c6:      	jmp	0x1db6cc <symbol>
  1db6c8:      	xorl	%edx, %edx
  1db6ca:      	xorl	%eax, %eax
  1db6cc:      	cmpl	%eax, %ecx
  1db6ce:      	jae	0x1db7b6 <symbol>
  1db6d4:      	movl	%ecx, %eax
  1db6d6:      	leaq	(%rax,%rax,2), %rax
  1db6da:      	leaq	(%rdx,%rax,4), %rax
  1db6de:      	movl	0x4(%rax), %ecx
  1db6e1:      	movzbl	0x8(%rax), %eax
  1db6e5:      	cmpl	$0x1, %eax
  1db6e8:      	je	0x1db6ef <symbol>
  1db6ea:      	cmpl	$0x3, %eax
  1db6ed:      	jne	0x1db76a <symbol>
  1db6ef:      	movq	0x8(%rbp), %rax
  1db6f3:      	movq	0x10(%rax), %rax
  1db6f7:      	cmpl	0x8(%rax), %ecx
  1db6fa:      	jae	0x1db7b6 <symbol>
  1db700:      	movl	%ecx, %ecx
  1db702:      	movq	0x10(%rax,%rcx,8), %rax
  1db707:      	testq	%rax, %rax
  1db70a:      	je	0x1db715 <symbol>
  1db70c:      	leaq	0x10(%rax), %rcx
  1db710:      	movl	0x8(%rax), %eax
  1db713:      	jmp	0x1db719 <symbol>
  1db715:      	xorl	%ecx, %ecx
  1db717:      	xorl	%eax, %eax
  1db719:      	cmpl	%eax, %r14d
  1db71c:      	jae	0x1db7b6 <symbol>
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
  1db73d:      	callq	0x1dfd0 <symbol>
  1db742:      	testl	%eax, %eax
  1db744:      	je	0x1db5d2 <symbol>
  1db74a:      	jmp	0x1db7a8 <symbol>
  1db74c:      	movq	%rdx, %rcx
  1db74f:      	leaq	-0xd2426(%rip), %rax
  1db756:      	callq	*%rax
  1db758:      	movq	%rax, %rbp
  1db75b:      	jmp	0x1db3ef <symbol>
  1db760:      	callq	0x1db765 <symbol>
		00000000001db761:  IMAGE_REL_AMD64_REL32	__GetNonGCStaticBase_SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer>
  1db765:      	jmp	0x1db669 <symbol>
  1db76a:      	leaq	(%rip), %rcx
		00000000001db76d:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  1db771:      	callq	0x1db776 <symbol>
		00000000001db772:  IMAGE_REL_AMD64_REL32	RhpNewFast
  1db776:      	movq	%rax, %rsi
  1db779:      	leaq	(%rip), %rcx
		00000000001db77c:  IMAGE_REL_AMD64_REL32	__RuntimeType_SomeEngine_ECS_Relations_Benchmarks_SomeEngine_ECS_Benchmarks_Relations_Bitmap_CollisionLayer
  1db780:      	callq	0x8d2a0 <symbol>
  1db785:      	movq	%rax, %rcx
  1db788:      	leaq	(%rip), %rdx
		00000000001db78b:  IMAGE_REL_AMD64_REL32	__Str__was_not_declared_for_query_re_3DAC58C3D425A8E368C52F9A8DF72803BD0D416C884BD906DC32246D69AA8921
  1db78f:      	callq	0x8ed30 <symbol>
  1db794:      	movq	%rax, %rdx
  1db797:      	movq	%rsi, %rcx
  1db79a:      	callq	0x9d230 <symbol>
  1db79f:      	movq	%rsi, %rcx
  1db7a2:      	callq	0x1db7a7 <symbol>
		00000000001db7a3:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  1db7a7:      	int3
  1db7a8:      	callq	0x1db870 <symbol>
  1db7ad:      	movq	%rax, %rcx
  1db7b0:      	callq	0x1db7b5 <symbol>
		00000000001db7b1:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  1db7b5:      	int3
  1db7b6:      	callq	0x10c790 <symbol>
  1db7bb:      	int3
  1db7bc:      	nop
  1db7bd:      	nop
  1db7be:      	nop
  1db7bf:      	nop

