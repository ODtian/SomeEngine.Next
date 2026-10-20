00000000002211e0 <symbol>
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
  221239:      	jne	0x22125c <symbol>
  22123b:      	movq	0x38(%rcx), %rdx
  22123f:      	cmpl	$0x0, 0x10(%rdx)
  221243:      	jne	0x2214b0 <symbol>
  221249:      	leaq	0x14(%rdx), %r8
  22124d:      	lock
  22124e:      	incl	(%r8)
  221251:      	cmpl	$0x0, 0x10(%rdx)
  221255:      	je	0x2212be <symbol>
  221257:      	jmp	0x2214d7 <symbol>
  22125c:      	movq	0x38(%rcx), %r14
  221260:      	cmpb	%r14b, (%r14)
  221263:      	callq	0xc7c60 <symbol>
  221268:      	movl	%eax, %ecx
  22126a:      	movl	0x10(%r14), %edx
  22126e:      	testl	%edx, %edx
  221270:      	jne	0x221282 <symbol>
  221272:      	leaq	0x10(%r14), %rdx
  221276:      	xorl	%eax, %eax
  221278:      	lock
  221279:      	cmpxchgl	%ecx, (%rdx)
  22127c:      	movl	%eax, %edx
  22127e:      	testl	%edx, %edx
  221280:      	je	0x22128a <symbol>
  221282:      	cmpl	%ecx, %edx
  221284:      	jne	0x22145c <symbol>
  22128a:      	cmpl	$0x0, 0x8(%r14)
  22128f:      	jne	0x2212a7 <symbol>
  221291:      	cmpl	$0x0, 0x14(%r14)
  221296:      	jne	0x221483 <symbol>
  22129c:      	cmpl	$0x0, 0x18(%r14)
  2212a1:      	jne	0x221483 <symbol>
  2212a7:      	movl	0x8(%r14), %ecx
  2212ab:      	testl	%ecx, %ecx
  2212ad:      	jne	0x2212b4 <symbol>
  2212af:      	movb	$0x0, 0x1c(%r14)
  2212b4:      	incl	%ecx
  2212b6:      	movl	%ecx, 0x8(%r14)
  2212ba:      	incl	0xc(%r14)
  2212be:      	movq	%gs:0x58, %rcx
  2212c7:      	movl	(%rip), %eax
		00000000002212c9:  IMAGE_REL_AMD64_REL32	_tls_index
  2212cd:      	movl	$0x0, %edx
		00000000002212ce:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  2212d2:      	addq	(%rcx,%rax,8), %rdx
  2212d6:      	movq	(%rdx), %r14
  2212d9:      	testq	%r14, %r14
  2212dc:      	je	0x2213fb <symbol>
  2212e2:      	movq	0x18(%r14), %r14
  2212e6:      	testq	%r14, %r14
  2212e9:      	je	0x221306 <symbol>
  2212eb:      	movq	0x10(%rbp), %rcx
  2212ef:      	cmpq	%rcx, 0x8(%r14)
  2212f3:      	je	0x221314 <symbol>
  2212f5:      	movq	0x18(%r14), %rdx
  2212f9:      	callq	0x6340 <symbol>
  2212fe:      	movq	%rax, %r14
  221301:      	testq	%r14, %r14
  221304:      	jne	0x221314 <symbol>
  221306:      	movq	0x10(%rbp), %rcx
  22130a:      	movq	0x30(%rcx), %rdx
  22130e:      	movq	0x8(%rdx), %rdx
  221312:      	jmp	0x22131c <symbol>
  221314:      	movq	0x10(%rbp), %rcx
  221318:      	movq	0x10(%r14), %rdx
  22131c:      	movq	0x40(%rdx), %rcx
  221320:      	movq	0x18(%rbp), %rdx
  221324:      	cmpl	%ecx, (%rcx)
  221326:      	callq	0xcdc0 <symbol>
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
  221353:      	callq	0x223d0 <symbol>
  221358:      	movq	-0x98(%rbp), %rsi
  22135f:      	leaq	0x28(%rdi), %rdx
  221363:      	movq	(%rdx), %r14
  221366:      	movq	(%rbx), %rdx
  221369:      	testq	%rdx, %rdx
  22136c:      	je	0x221378 <symbol>
  22136e:      	leaq	0x10(%rdx), %rbx
  221372:      	movl	0x8(%rdx), %r15d
  221376:      	jmp	0x22137d <symbol>
  221378:      	xorl	%ebx, %ebx
  22137a:      	xorl	%r15d, %r15d
  22137d:      	testl	%r15d, %r15d
  221380:      	jle	0x2213f6 <symbol>
  221382:      	xorl	%r13d, %r13d
  221385:      	leaq	(%rbx,%r13), %r12
  221389:      	movq	(%r12), %rdx
  22138d:      	movq	%rsi, %rcx
  221390:      	callq	0x1d8a40 <symbol>
  221395:      	movq	%rax, -0xa8(%rbp)
  22139c:      	testq	%rax, %rax
  22139f:      	je	0x2213ed <symbol>
  2213a1:      	movq	0x8(%r12), %rdx
  2213a6:      	movq	%rsi, %rcx
  2213a9:      	callq	0x1d8a40 <symbol>
  2213ae:      	testq	%rax, %rax
  2213b1:      	je	0x2213ed <symbol>
  2213b3:      	movq	-0xa8(%rbp), %rcx
  2213ba:      	movq	0x8(%rcx), %rdx
  2213be:      	testq	%rdx, (%rax)
  2213c1:      	je	0x2213ed <symbol>
  2213c3:      	movq	0x8(%rax), %rdx
  2213c7:      	testq	%rdx, (%rcx)
  2213ca:      	je	0x2213ed <symbol>
  2213cc:      	movl	0x10(%r14), %ecx
  2213d0:      	leal	0x1(%rcx), %edx
  2213d3:      	movl	%edx, 0x10(%r14)
  2213d7:      	cmpl	0x8(%r14), %ecx
  2213db:      	jae	0x22140f <symbol>
  2213dd:      	movq	(%r14), %rdx
  2213e0:      	shlq	$0x4, %rcx
  2213e4:      	movups	(%r12), %xmm0
  2213e9:      	movups	%xmm0, (%rdx,%rcx)
  2213ed:      	addq	$0x10, %r13
  2213f1:      	decl	%r15d
  2213f4:      	jne	0x221385 <symbol>
  2213f6:      	movb	$0x1, (%rdi)
  2213f9:      	jmp	0x221415 <symbol>
  2213fb:      	movq	%rdx, %rcx
  2213fe:      	leaq	-0x117f75(%rip), %rax
  221405:      	callq	*%rax
  221407:      	movq	%rax, %r14
  22140a:      	jmp	0x2212e2 <symbol>
  22140f:      	callq	0x10c8f0 <symbol>
  221414:      	int3
  221415:      	cmpb	$0x0, 0x30(%rbp)
  221419:      	jne	0x221439 <symbol>
  22141b:      	movq	0x10(%rbp), %rcx
  22141f:      	movq	0x38(%rcx), %rcx
  221423:      	leaq	0x14(%rcx), %rax
  221427:      	movl	$0xffffffff, %edx
  22142c:      	lock
  22142d:      	xaddl	%edx, (%rax)
  221430:      	decl	%edx
  221432:      	jns	0x221447 <symbol>
  221434:      	jmp	0x221507 <symbol>
  221439:      	movq	0x10(%rbp), %rcx
  22143d:      	movl	$0x1, %edx
  221442:      	callq	0x7250 <symbol>
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
  22145c:      	leaq	(%rip), %rcx
		000000000022145f:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221463:      	callq	0x221468 <symbol>
		0000000000221464:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221468:      	movq	%rax, %rbx
  22146b:      	movq	%rbx, %rcx
  22146e:      	leaq	(%rip), %rdx
		0000000000221471:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  221475:      	callq	0x9d390 <symbol>
  22147a:      	movq	%rbx, %rcx
  22147d:      	callq	0x221482 <symbol>
		000000000022147e:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221482:      	int3
  221483:      	xorl	%ecx, %ecx
  221485:      	movl	%ecx, 0x10(%r14)
  221489:      	leaq	(%rip), %rcx
		000000000022148c:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221490:      	callq	0x221495 <symbol>
		0000000000221491:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221495:      	movq	%rax, %rbx
  221498:      	movq	%rbx, %rcx
  22149b:      	leaq	(%rip), %rdx
		000000000022149e:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  2214a2:      	callq	0x9d390 <symbol>
  2214a7:      	movq	%rbx, %rcx
  2214aa:      	callq	0x2214af <symbol>
		00000000002214ab:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2214af:      	int3
  2214b0:      	leaq	(%rip), %rcx
		00000000002214b3:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2214b7:      	callq	0x2214bc <symbol>
		00000000002214b8:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2214bc:      	movq	%rax, %rsi
  2214bf:      	movq	%rsi, %rcx
  2214c2:      	leaq	(%rip), %rdx
		00000000002214c5:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2214c9:      	callq	0x9d390 <symbol>
  2214ce:      	movq	%rsi, %rcx
  2214d1:      	callq	0x2214d6 <symbol>
		00000000002214d2:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2214d6:      	int3
  2214d7:      	leaq	0x14(%rdx), %rcx
  2214db:      	callq	0xc9260 <symbol>
  2214e0:      	leaq	(%rip), %rcx
		00000000002214e3:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  2214e7:      	callq	0x2214ec <symbol>
		00000000002214e8:  IMAGE_REL_AMD64_REL32	RhpNewFast
  2214ec:      	movq	%rax, %rbx
  2214ef:      	movq	%rbx, %rcx
  2214f2:      	leaq	(%rip), %rdx
		00000000002214f5:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  2214f9:      	callq	0x9d390 <symbol>
  2214fe:      	movq	%rbx, %rcx
  221501:      	callq	0x221506 <symbol>
		0000000000221502:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221506:      	int3
  221507:      	addq	$0x14, %rcx
  22150b:      	callq	0xc9250 <symbol>
  221510:      	leaq	(%rip), %rcx
		0000000000221513:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221517:      	callq	0x22151c <symbol>
		0000000000221518:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22151c:      	movq	%rax, %rbx
  22151f:      	movq	%rbx, %rcx
  221522:      	leaq	(%rip), %rdx
		0000000000221525:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  221529:      	callq	0x9d390 <symbol>
  22152e:      	movq	%rbx, %rcx
  221531:      	callq	0x221536 <symbol>
		0000000000221532:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221536:      	int3
  221537:      	subq	$0x28, %rsp
  22153b:      	movq	%rcx, %rbx
  22153e:      	leaq	(%rip), %rsi
		0000000000221541:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  221545:      	leaq	(%rip), %rcx
		0000000000221548:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  22154c:      	cmpb	$0x0, 0x30(%rbp)
  221550:      	cmoveq	%rcx, %rsi
  221554:      	leaq	(%rip), %rcx
		0000000000221557:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  22155b:      	movl	$0x2, %edx
  221560:      	callq	0x221565 <symbol>
		0000000000221561:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  221565:      	movq	%rax, %rdi
  221568:      	leaq	0x10(%rdi), %rcx
  22156c:      	movq	-0xa0(%rbp), %rdx
  221573:      	callq	0x221578 <symbol>
		0000000000221574:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  221578:      	leaq	0x18(%rdi), %rcx
  22157c:      	movq	%rbx, %rdx
  22157f:      	callq	0x221584 <symbol>
		0000000000221580:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  221584:      	leaq	(%rip), %rcx
		0000000000221587:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  22158b:      	callq	0x221590 <symbol>
		000000000022158c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221590:      	movq	%rax, %rbx
  221593:      	movq	%rbx, %rcx
  221596:      	movq	%rsi, %rdx
  221599:      	movq	%rdi, %r8
  22159c:      	callq	0x96690 <symbol>
  2215a1:      	movq	%rbx, %rcx
  2215a4:      	callq	0x2215a9 <symbol>
		00000000002215a5:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  2215a9:      	int3
  2215aa:      	subq	$0x28, %rsp
  2215ae:      	movq	%rcx, %rbx
  2215b1:      	movq	%rbx, -0xa0(%rbp)
  2215b8:      	movzbl	0x30(%rbp), %edx
  2215bc:      	movq	0x10(%rbp), %rcx
  2215c0:      	xorl	%r8d, %r8d
  2215c3:      	callq	0x7590 <symbol>
  2215c8:      	nop
  2215c9:      	movq	%rbx, %rcx
  2215cc:      	callq	0xe7550 <symbol>
  2215d1:      	movq	%rax, %rcx
  2215d4:      	cmpl	%ecx, (%rcx)
  2215d6:      	callq	0xe75b0 <symbol>
  2215db:      	callq	0x2215e0 <symbol>
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
