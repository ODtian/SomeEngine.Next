00000000002246e0 <symbol>
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
  224719:      	jne	0x224706 <symbol>
  22471b:      	movq	%rcx, 0x10(%rbp)
  22471f:      	movq	%rdx, 0x18(%rbp)
  224723:      	movl	%r8d, %ebx
  224726:      	movl	%r9d, %esi
  224729:      	movl	0x30(%rbp), %eax
  22472c:      	movq	0x38(%rbp), %rdi
  224730:      	testb	%al, %al
  224732:      	jne	0x224755 <symbol>
  224734:      	movq	0x38(%rcx), %rdx
  224738:      	cmpl	$0x0, 0x10(%rdx)
  22473c:      	jne	0x224993 <symbol>
  224742:      	leaq	0x14(%rdx), %r8
  224746:      	lock
  224747:      	incl	(%r8)
  22474a:      	cmpl	$0x0, 0x10(%rdx)
  22474e:      	je	0x2247b7 <symbol>
  224750:      	jmp	0x2249ba <symbol>
  224755:      	movq	0x38(%rcx), %r14
  224759:      	cmpb	%r14b, (%r14)
  22475c:      	callq	0xc7b00 <symbol>
  224761:      	movl	%eax, %ecx
  224763:      	movl	0x10(%r14), %edx
  224767:      	testl	%edx, %edx
  224769:      	jne	0x22477b <symbol>
  22476b:      	leaq	0x10(%r14), %rdx
  22476f:      	xorl	%eax, %eax
  224771:      	lock
  224772:      	cmpxchgl	%ecx, (%rdx)
  224775:      	movl	%eax, %edx
  224777:      	testl	%edx, %edx
  224779:      	je	0x224783 <symbol>
  22477b:      	cmpl	%ecx, %edx
  22477d:      	jne	0x22493f <symbol>
  224783:      	cmpl	$0x0, 0x8(%r14)
  224788:      	jne	0x2247a0 <symbol>
  22478a:      	cmpl	$0x0, 0x14(%r14)
  22478f:      	jne	0x224966 <symbol>
  224795:      	cmpl	$0x0, 0x18(%r14)
  22479a:      	jne	0x224966 <symbol>
  2247a0:      	movl	0x8(%r14), %ecx
  2247a4:      	testl	%ecx, %ecx
  2247a6:      	jne	0x2247ad <symbol>
  2247a8:      	movb	$0x0, 0x1c(%r14)
  2247ad:      	incl	%ecx
  2247af:      	movl	%ecx, 0x8(%r14)
  2247b3:      	incl	0xc(%r14)
  2247b7:      	movq	%gs:0x58, %rcx
  2247c0:      	movl	(%rip), %eax
		00000000002247c2:  IMAGE_REL_AMD64_REL32	_tls_index
  2247c6:      	movl	$0x0, %edx
		00000000002247c7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2247cb:      	addq	(%rcx,%rax,8), %rdx
  2247cf:      	movq	(%rdx), %r14
  2247d2:      	testq	%r14, %r14
  2247d5:      	je	0x2248e0 <symbol>
  2247db:      	movq	0x18(%r14), %r14
  2247df:      	testq	%r14, %r14
  2247e2:      	je	0x2247ff <symbol>
  2247e4:      	movq	0x10(%rbp), %rcx
  2247e8:      	cmpq	%rcx, 0x8(%r14)
  2247ec:      	je	0x22480d <symbol>
  2247ee:      	movq	0x18(%r14), %rdx
  2247f2:      	callq	0x6400 <symbol>
  2247f7:      	movq	%rax, %r14
  2247fa:      	testq	%r14, %r14
  2247fd:      	jne	0x22480d <symbol>
  2247ff:      	movq	0x10(%rbp), %rcx
  224803:      	movq	0x30(%rcx), %rdx
  224807:      	movq	0x8(%rdx), %rdx
  22480b:      	jmp	0x224815 <symbol>
  22480d:      	movq	0x10(%rbp), %rcx
  224811:      	movq	0x10(%r14), %rdx
  224815:      	movq	0x40(%rdx), %rcx
  224819:      	movq	0x18(%rbp), %rdx
  22481d:      	cmpl	%ecx, (%rcx)
  22481f:      	callq	0xcea0 <symbol>
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
  22484c:      	callq	0x22230 <symbol>
  224851:      	movq	-0x90(%rbp), %rsi
  224858:      	leaq	0x28(%rdi), %rcx
  22485c:      	movq	(%rcx), %r14
  22485f:      	movq	(%rbx), %rbx
  224862:      	movl	0x8(%rbx), %r15d
  224866:      	testl	%r15d, %r15d
  224869:      	jle	0x2248db <symbol>
  22486b:      	addq	$0x10, %rbx
  22486f:      	movups	(%rbx), %xmm0
  224872:      	movups	%xmm0, -0xa0(%rbp)
  224879:      	movq	%rsi, %rcx
  22487c:      	movq	-0xa0(%rbp), %rdx
  224883:      	callq	0x1db3a0 <symbol>
  224888:      	movq	%rax, %r13
  22488b:      	testq	%r13, %r13
  22488e:      	je	0x2248d2 <symbol>
  224890:      	movq	%rsi, %rcx
  224893:      	movq	-0x98(%rbp), %rdx
  22489a:      	callq	0x1db3a0 <symbol>
  22489f:      	testq	%rax, %rax
  2248a2:      	je	0x2248d2 <symbol>
  2248a4:      	movq	0x8(%r13), %rcx
  2248a8:      	testq	%rcx, (%rax)
  2248ab:      	je	0x2248d2 <symbol>
  2248ad:      	movq	(%r14), %rcx
  2248b0:      	movl	0x10(%rcx), %edx
  2248b3:      	leal	0x1(%rdx), %r8d
  2248b7:      	movl	%r8d, 0x10(%rcx)
  2248bb:      	cmpl	0x8(%rcx), %edx
  2248be:      	jae	0x2248f4 <symbol>
  2248c0:      	movq	(%rcx), %rcx
  2248c3:      	shlq	$0x4, %rdx
  2248c7:      	movups	-0xa0(%rbp), %xmm0
  2248ce:      	movups	%xmm0, (%rcx,%rdx)
  2248d2:      	addq	$0x10, %rbx
  2248d6:      	decl	%r15d
  2248d9:      	jne	0x22486f <symbol>
  2248db:      	movb	$0x1, (%rdi)
  2248de:      	jmp	0x2248fa <symbol>
  2248e0:      	movq	%rdx, %rcx
  2248e3:      	leaq	-0x11b5ba(%rip), %rax
  2248ea:      	callq	*%rax
  2248ec:      	movq	%rax, %r14
  2248ef:      	jmp	0x2247db <symbol>
  2248f4:      	callq	0x10c790 <symbol>
  2248f9:      	int3
  2248fa:      	cmpb	$0x0, 0x30(%rbp)
  2248fe:      	jne	0x22491e <symbol>
  224900:      	movq	0x10(%rbp), %rcx
  224904:      	movq	0x38(%rcx), %rcx
  224908:      	leaq	0x14(%rcx), %rax
  22490c:      	movl	$0xffffffff, %edx
  224911:      	lock
  224912:      	xaddl	%edx, (%rax)
  224915:      	decl	%edx
  224917:      	jns	0x22492c <symbol>
  224919:      	jmp	0x2249ea <symbol>
  22491e:      	movq	0x10(%rbp), %rcx
  224922:      	movl	$0x1, %edx
  224927:      	callq	0x7330 <symbol>
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
  22493f:      	leaq	(%rip), %rcx
		0000000000224942:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  224946:      	callq	0x22494b <symbol>
		0000000000224947:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22494b:      	movq	%rax, %rbx
  22494e:      	movq	%rbx, %rcx
  224951:      	leaq	(%rip), %rdx
		0000000000224954:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  224958:      	callq	0x9d230 <symbol>
  22495d:      	movq	%rbx, %rcx
  224960:      	callq	0x224965 <symbol>
		0000000000224961:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224965:      	int3
  224966:      	xorl	%ecx, %ecx
  224968:      	movl	%ecx, 0x10(%r14)
  22496c:      	leaq	(%rip), %rcx
		000000000022496f:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  224973:      	callq	0x224978 <symbol>
		0000000000224974:  IMAGE_REL_AMD64_REL32	RhpNewFast
  224978:      	movq	%rax, %rbx
  22497b:      	movq	%rbx, %rcx
  22497e:      	leaq	(%rip), %rdx
		0000000000224981:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  224985:      	callq	0x9d230 <symbol>
  22498a:      	movq	%rbx, %rcx
  22498d:      	callq	0x224992 <symbol>
		000000000022498e:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224992:      	int3
  224993:      	leaq	(%rip), %rcx
		0000000000224996:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  22499a:      	callq	0x22499f <symbol>
		000000000022499b:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22499f:      	movq	%rax, %rsi
  2249a2:      	movq	%rsi, %rcx
  2249a5:      	leaq	(%rip), %rdx
		00000000002249a8:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2249ac:      	callq	0x9d230 <symbol>
  2249b1:      	movq	%rsi, %rcx
  2249b4:      	callq	0x2249b9 <symbol>
		00000000002249b5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2249b9:      	int3
  2249ba:      	leaq	0x14(%rdx), %rcx
  2249be:      	callq	0xc9100 <symbol>
  2249c3:      	leaq	(%rip), %rcx
		00000000002249c6:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2249ca:      	callq	0x2249cf <symbol>
		00000000002249cb:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2249cf:      	movq	%rax, %rbx
  2249d2:      	movq	%rbx, %rcx
  2249d5:      	leaq	(%rip), %rdx
		00000000002249d8:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2249dc:      	callq	0x9d230 <symbol>
  2249e1:      	movq	%rbx, %rcx
  2249e4:      	callq	0x2249e9 <symbol>
		00000000002249e5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2249e9:      	int3
  2249ea:      	addq	$0x14, %rcx
  2249ee:      	callq	0xc90f0 <symbol>
  2249f3:      	leaq	(%rip), %rcx
		00000000002249f6:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2249fa:      	callq	0x2249ff <symbol>
		00000000002249fb:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2249ff:      	movq	%rax, %rbx
  224a02:      	movq	%rbx, %rcx
  224a05:      	leaq	(%rip), %rdx
		0000000000224a08:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  224a0c:      	callq	0x9d230 <symbol>
  224a11:      	movq	%rbx, %rcx
  224a14:      	callq	0x224a19 <symbol>
		0000000000224a15:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224a19:      	int3
  224a1a:      	subq	$0x28, %rsp
  224a1e:      	movq	%rcx, %rbx
  224a21:      	leaq	(%rip), %rsi
		0000000000224a24:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  224a28:      	leaq	(%rip), %rcx
		0000000000224a2b:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  224a2f:      	cmpb	$0x0, 0x30(%rbp)
  224a33:      	cmoveq	%rcx, %rsi
  224a37:      	leaq	(%rip), %rcx
		0000000000224a3a:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  224a3e:      	movl	$0x2, %edx
  224a43:      	callq	0x224a48 <symbol>
		0000000000224a44:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  224a48:      	movq	%rax, %rdi
  224a4b:      	leaq	0x10(%rdi), %rcx
  224a4f:      	movq	-0xa8(%rbp), %rdx
  224a56:      	callq	0x224a5b <symbol>
		0000000000224a57:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  224a5b:      	leaq	0x18(%rdi), %rcx
  224a5f:      	movq	%rbx, %rdx
  224a62:      	callq	0x224a67 <symbol>
		0000000000224a63:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  224a67:      	leaq	(%rip), %rcx
		0000000000224a6a:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  224a6e:      	callq	0x224a73 <symbol>
		0000000000224a6f:  IMAGE_REL_AMD64_REL32	RhpNewFast
  224a73:      	movq	%rax, %rbx
  224a76:      	movq	%rbx, %rcx
  224a79:      	movq	%rsi, %rdx
  224a7c:      	movq	%rdi, %r8
  224a7f:      	callq	0x96530 <symbol>
  224a84:      	movq	%rbx, %rcx
  224a87:      	callq	0x224a8c <symbol>
		0000000000224a88:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  224a8c:      	int3
  224a8d:      	subq	$0x28, %rsp
  224a91:      	movq	%rcx, %rbx
  224a94:      	movq	%rbx, -0xa8(%rbp)
  224a9b:      	movzbl	0x30(%rbp), %edx
  224a9f:      	movq	0x10(%rbp), %rcx
  224aa3:      	xorl	%r8d, %r8d
  224aa6:      	callq	0x7670 <symbol>
  224aab:      	nop
  224aac:      	movq	%rbx, %rcx
  224aaf:      	callq	0xe73f0 <symbol>
  224ab4:      	movq	%rax, %rcx
  224ab7:      	cmpl	%ecx, (%rcx)
  224ab9:      	callq	0xe7450 <symbol>
  224abe:      	callq	0x224ac3 <symbol>
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

