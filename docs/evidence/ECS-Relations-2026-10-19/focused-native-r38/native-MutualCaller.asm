00000000002253c0 <symbol>
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
  2253f9:      	jne	0x2253e6 <symbol>
  2253fb:      	movq	%rcx, 0x10(%rbp)
  2253ff:      	movq	%rdx, 0x18(%rbp)
  225403:      	movl	%r8d, %ebx
  225406:      	movl	%r9d, %esi
  225409:      	movl	0x30(%rbp), %eax
  22540c:      	movq	0x38(%rbp), %rdi
  225410:      	testb	%al, %al
  225412:      	jne	0x225435 <symbol>
  225414:      	movq	0x38(%rcx), %rdx
  225418:      	cmpl	$0x0, 0x10(%rdx)
  22541c:      	jne	0x22567d <symbol>
  225422:      	leaq	0x14(%rdx), %r8
  225426:      	lock
  225427:      	incl	(%r8)
  22542a:      	cmpl	$0x0, 0x10(%rdx)
  22542e:      	je	0x225497 <symbol>
  225430:      	jmp	0x2256a4 <symbol>
  225435:      	movq	0x38(%rcx), %r14
  225439:      	cmpb	%r14b, (%r14)
  22543c:      	callq	0xc7b00 <symbol>
  225441:      	movl	%eax, %ecx
  225443:      	movl	0x10(%r14), %edx
  225447:      	testl	%edx, %edx
  225449:      	jne	0x22545b <symbol>
  22544b:      	leaq	0x10(%r14), %rdx
  22544f:      	xorl	%eax, %eax
  225451:      	lock
  225452:      	cmpxchgl	%ecx, (%rdx)
  225455:      	movl	%eax, %edx
  225457:      	testl	%edx, %edx
  225459:      	je	0x225463 <symbol>
  22545b:      	cmpl	%ecx, %edx
  22545d:      	jne	0x225629 <symbol>
  225463:      	cmpl	$0x0, 0x8(%r14)
  225468:      	jne	0x225480 <symbol>
  22546a:      	cmpl	$0x0, 0x14(%r14)
  22546f:      	jne	0x225650 <symbol>
  225475:      	cmpl	$0x0, 0x18(%r14)
  22547a:      	jne	0x225650 <symbol>
  225480:      	movl	0x8(%r14), %ecx
  225484:      	testl	%ecx, %ecx
  225486:      	jne	0x22548d <symbol>
  225488:      	movb	$0x0, 0x1c(%r14)
  22548d:      	incl	%ecx
  22548f:      	movl	%ecx, 0x8(%r14)
  225493:      	incl	0xc(%r14)
  225497:      	movq	%gs:0x58, %rcx
  2254a0:      	movl	(%rip), %eax
		00000000002254a2:  IMAGE_REL_AMD64_REL32	_tls_index
  2254a6:      	movl	$0x0, %edx
		00000000002254a7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2254ab:      	addq	(%rcx,%rax,8), %rdx
  2254af:      	movq	(%rdx), %r14
  2254b2:      	testq	%r14, %r14
  2254b5:      	je	0x2255ca <symbol>
  2254bb:      	movq	0x18(%r14), %r14
  2254bf:      	testq	%r14, %r14
  2254c2:      	je	0x2254df <symbol>
  2254c4:      	movq	0x10(%rbp), %rcx
  2254c8:      	cmpq	%rcx, 0x8(%r14)
  2254cc:      	je	0x2254ed <symbol>
  2254ce:      	movq	0x18(%r14), %rdx
  2254d2:      	callq	0x6400 <symbol>
  2254d7:      	movq	%rax, %r14
  2254da:      	testq	%r14, %r14
  2254dd:      	jne	0x2254ed <symbol>
  2254df:      	movq	0x10(%rbp), %rcx
  2254e3:      	movq	0x30(%rcx), %rdx
  2254e7:      	movq	0x8(%rdx), %rdx
  2254eb:      	jmp	0x2254f5 <symbol>
  2254ed:      	movq	0x10(%rbp), %rcx
  2254f1:      	movq	0x10(%r14), %rdx
  2254f5:      	movq	0x40(%rdx), %rcx
  2254f9:      	movq	0x18(%rbp), %rdx
  2254fd:      	cmpl	%ecx, (%rcx)
  2254ff:      	callq	0xcea0 <symbol>
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
  22552c:      	callq	0x22230 <symbol>
  225531:      	movq	-0x90(%rbp), %rsi
  225538:      	leaq	0x28(%rdi), %rcx
  22553c:      	movq	(%rcx), %r14
  22553f:      	movq	(%rbx), %rbx
  225542:      	movl	0x8(%rbx), %r15d
  225546:      	testl	%r15d, %r15d
  225549:      	jle	0x2255c5 <symbol>
  22554b:      	addq	$0x10, %rbx
  22554f:      	movups	(%rbx), %xmm0
  225552:      	movups	%xmm0, -0xa0(%rbp)
  225559:      	movq	%rsi, %rcx
  22555c:      	movq	-0xa0(%rbp), %rdx
  225563:      	callq	0x1db3a0 <symbol>
  225568:      	movq	%rax, %r13
  22556b:      	testq	%r13, %r13
  22556e:      	je	0x2255bc <symbol>
  225570:      	movq	%rsi, %rcx
  225573:      	movq	-0x98(%rbp), %rdx
  22557a:      	callq	0x1db3a0 <symbol>
  22557f:      	testq	%rax, %rax
  225582:      	je	0x2255bc <symbol>
  225584:      	movq	0x8(%r13), %rcx
  225588:      	testq	%rcx, (%rax)
  22558b:      	je	0x2255bc <symbol>
  22558d:      	movq	0x8(%rax), %rcx
  225591:      	testq	%rcx, (%r13)
  225595:      	je	0x2255bc <symbol>
  225597:      	movq	(%r14), %rcx
  22559a:      	movl	0x10(%rcx), %edx
  22559d:      	leal	0x1(%rdx), %r8d
  2255a1:      	movl	%r8d, 0x10(%rcx)
  2255a5:      	cmpl	0x8(%rcx), %edx
  2255a8:      	jae	0x2255de <symbol>
  2255aa:      	movq	(%rcx), %rcx
  2255ad:      	shlq	$0x4, %rdx
  2255b1:      	movups	-0xa0(%rbp), %xmm0
  2255b8:      	movups	%xmm0, (%rcx,%rdx)
  2255bc:      	addq	$0x10, %rbx
  2255c0:      	decl	%r15d
  2255c3:      	jne	0x22554f <symbol>
  2255c5:      	movb	$0x1, (%rdi)
  2255c8:      	jmp	0x2255e4 <symbol>
  2255ca:      	movq	%rdx, %rcx
  2255cd:      	leaq	-0x11c2a4(%rip), %rax
  2255d4:      	callq	*%rax
  2255d6:      	movq	%rax, %r14
  2255d9:      	jmp	0x2254bb <symbol>
  2255de:      	callq	0x10c790 <symbol>
  2255e3:      	int3
  2255e4:      	cmpb	$0x0, 0x30(%rbp)
  2255e8:      	jne	0x225608 <symbol>
  2255ea:      	movq	0x10(%rbp), %rcx
  2255ee:      	movq	0x38(%rcx), %rcx
  2255f2:      	leaq	0x14(%rcx), %rax
  2255f6:      	movl	$0xffffffff, %edx
  2255fb:      	lock
  2255fc:      	xaddl	%edx, (%rax)
  2255ff:      	decl	%edx
  225601:      	jns	0x225616 <symbol>
  225603:      	jmp	0x2256d4 <symbol>
  225608:      	movq	0x10(%rbp), %rcx
  22560c:      	movl	$0x1, %edx
  225611:      	callq	0x7330 <symbol>
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
  225629:      	leaq	(%rip), %rcx
		000000000022562c:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  225630:      	callq	0x225635 <symbol>
		0000000000225631:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225635:      	movq	%rax, %rbx
  225638:      	movq	%rbx, %rcx
  22563b:      	leaq	(%rip), %rdx
		000000000022563e:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  225642:      	callq	0x9d230 <symbol>
  225647:      	movq	%rbx, %rcx
  22564a:      	callq	0x22564f <symbol>
		000000000022564b:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  22564f:      	int3
  225650:      	xorl	%ecx, %ecx
  225652:      	movl	%ecx, 0x10(%r14)
  225656:      	leaq	(%rip), %rcx
		0000000000225659:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  22565d:      	callq	0x225662 <symbol>
		000000000022565e:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225662:      	movq	%rax, %rbx
  225665:      	movq	%rbx, %rcx
  225668:      	leaq	(%rip), %rdx
		000000000022566b:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  22566f:      	callq	0x9d230 <symbol>
  225674:      	movq	%rbx, %rcx
  225677:      	callq	0x22567c <symbol>
		0000000000225678:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  22567c:      	int3
  22567d:      	leaq	(%rip), %rcx
		0000000000225680:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  225684:      	callq	0x225689 <symbol>
		0000000000225685:  IMAGE_REL_AMD64_REL32	RhpNewFast
  225689:      	movq	%rax, %rsi
  22568c:      	movq	%rsi, %rcx
  22568f:      	leaq	(%rip), %rdx
		0000000000225692:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  225696:      	callq	0x9d230 <symbol>
  22569b:      	movq	%rsi, %rcx
  22569e:      	callq	0x2256a3 <symbol>
		000000000022569f:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2256a3:      	int3
  2256a4:      	leaq	0x14(%rdx), %rcx
  2256a8:      	callq	0xc9100 <symbol>
  2256ad:      	leaq	(%rip), %rcx
		00000000002256b0:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2256b4:      	callq	0x2256b9 <symbol>
		00000000002256b5:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2256b9:      	movq	%rax, %rbx
  2256bc:      	movq	%rbx, %rcx
  2256bf:      	leaq	(%rip), %rdx
		00000000002256c2:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2256c6:      	callq	0x9d230 <symbol>
  2256cb:      	movq	%rbx, %rcx
  2256ce:      	callq	0x2256d3 <symbol>
		00000000002256cf:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2256d3:      	int3
  2256d4:      	addq	$0x14, %rcx
  2256d8:      	callq	0xc90f0 <symbol>
  2256dd:      	leaq	(%rip), %rcx
		00000000002256e0:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2256e4:      	callq	0x2256e9 <symbol>
		00000000002256e5:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2256e9:      	movq	%rax, %rbx
  2256ec:      	movq	%rbx, %rcx
  2256ef:      	leaq	(%rip), %rdx
		00000000002256f2:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  2256f6:      	callq	0x9d230 <symbol>
  2256fb:      	movq	%rbx, %rcx
  2256fe:      	callq	0x225703 <symbol>
		00000000002256ff:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  225703:      	int3
  225704:      	subq	$0x28, %rsp
  225708:      	movq	%rcx, %rbx
  22570b:      	leaq	(%rip), %rsi
		000000000022570e:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  225712:      	leaq	(%rip), %rcx
		0000000000225715:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  225719:      	cmpb	$0x0, 0x30(%rbp)
  22571d:      	cmoveq	%rcx, %rsi
  225721:      	leaq	(%rip), %rcx
		0000000000225724:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  225728:      	movl	$0x2, %edx
  22572d:      	callq	0x225732 <symbol>
		000000000022572e:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  225732:      	movq	%rax, %rdi
  225735:      	leaq	0x10(%rdi), %rcx
  225739:      	movq	-0xa8(%rbp), %rdx
  225740:      	callq	0x225745 <symbol>
		0000000000225741:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  225745:      	leaq	0x18(%rdi), %rcx
  225749:      	movq	%rbx, %rdx
  22574c:      	callq	0x225751 <symbol>
		000000000022574d:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  225751:      	leaq	(%rip), %rcx
		0000000000225754:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  225758:      	callq	0x22575d <symbol>
		0000000000225759:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22575d:      	movq	%rax, %rbx
  225760:      	movq	%rbx, %rcx
  225763:      	movq	%rsi, %rdx
  225766:      	movq	%rdi, %r8
  225769:      	callq	0x96530 <symbol>
  22576e:      	movq	%rbx, %rcx
  225771:      	callq	0x225776 <symbol>
		0000000000225772:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  225776:      	int3
  225777:      	subq	$0x28, %rsp
  22577b:      	movq	%rcx, %rbx
  22577e:      	movq	%rbx, -0xa8(%rbp)
  225785:      	movzbl	0x30(%rbp), %edx
  225789:      	movq	0x10(%rbp), %rcx
  22578d:      	xorl	%r8d, %r8d
  225790:      	callq	0x7670 <symbol>
  225795:      	nop
  225796:      	movq	%rbx, %rcx
  225799:      	callq	0xe73f0 <symbol>
  22579e:      	movq	%rax, %rcx
  2257a1:      	cmpl	%ecx, (%rcx)
  2257a3:      	callq	0xe7450 <symbol>
  2257a8:      	callq	0x2257ad <symbol>
		00000000002257a9:  IMAGE_REL_AMD64_REL32	RhpRethrow
  2257ad:      	int3
  2257ae:      	nop
  2257af:      	nop
