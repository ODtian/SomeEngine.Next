000000000015e280 <symbol>
  15e280:      	pushq	%r15
  15e282:      	pushq	%r14
  15e284:      	pushq	%r13
  15e286:      	pushq	%rdi
  15e287:      	pushq	%rsi
  15e288:      	pushq	%rbp
  15e289:      	pushq	%rbx
  15e28a:      	subq	$0x180, %rsp
  15e291:      	xorps	%xmm4, %xmm4
  15e294:      	movaps	%xmm4, 0x20(%rsp)
  15e299:      	movabsq	$-0x150, %rax
  15e2a3:      	movaps	%xmm4, 0x180(%rsp,%rax)
  15e2ab:      	movaps	%xmm4, 0x190(%rsp,%rax)
  15e2b3:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  15e2bb:      	addq	$0x30, %rax
  15e2bf:      	jne	0x15e2a3 <symbol>
  15e2c1:      	movq	%rdx, 0x1c8(%rsp)
  15e2c9:      	movq	%rcx, %rbx
  15e2cc:      	leaq	0x1c8(%rsp), %rcx
  15e2d4:      	leaq	0x20(%rsp), %rdx
  15e2d9:      	callq	0xb1d0 <symbol>
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
  15e335:      	callq	0x87e30 <symbol>
  15e33a:      	movq	0x140(%rsp), %rdx
  15e342:      	movq	%rdx, %rsi
  15e345:      	leaq	0x120(%rsp), %r9
  15e34d:      	movq	0x138(%rsp), %rcx
  15e355:      	movl	0x148(%rsp), %r8d
  15e35d:      	callq	0xd7e0 <symbol>
  15e362:      	testl	%eax, %eax
  15e364:      	jne	0x15e3af <symbol>
  15e366:      	leaq	0x128(%rsp), %rcx
  15e36e:      	callq	0x154ae0 <symbol>
  15e373:      	testl	%eax, %eax
  15e375:      	je	0x15e5bb <symbol>
  15e37b:      	movl	$0xffffffff, 0x120(%rsp)
  15e386:      	leaq	0x120(%rsp), %r9
  15e38e:      	movq	0x138(%rsp), %rcx
  15e396:      	movq	0x140(%rsp), %rdx
  15e39e:      	movl	0x148(%rsp), %r8d
  15e3a6:      	callq	0xd7e0 <symbol>
  15e3ab:      	testl	%eax, %eax
  15e3ad:      	je	0x15e366 <symbol>
  15e3af:      	movl	0x120(%rsp), %edi
  15e3b6:      	movq	0x140(%rsp), %rbp
  15e3be:      	cmpq	0x140(%rsp), %rsi
  15e3c6:      	je	0x15e3e5 <symbol>
  15e3c8:      	leaq	0x120(%rsp), %rcx
  15e3d0:      	movq	0x138(%rsp), %rdx
  15e3d8:      	movq	0x140(%rsp), %r8
  15e3e0:      	callq	0x161240 <symbol>
  15e3e5:      	cmpl	$0x0, 0x168(%rsp)
  15e3ed:      	jne	0x15e3f3 <symbol>
  15e3ef:      	xorl	%esi, %esi
  15e3f1:      	jmp	0x15e40e <symbol>
  15e3f3:      	cmpl	0x168(%rsp), %edi
  15e3fa:      	jae	0x15e5d1 <symbol>
  15e400:      	movl	%edi, %ecx
  15e402:      	imulq	$0x30, %rcx, %rsi
  15e406:      	addq	0x160(%rsp), %rsi
  15e40e:      	movq	0x8(%rbp), %rcx
  15e412:      	movq	0x8(%rcx), %rcx
  15e416:      	testq	%rcx, %rcx
  15e419:      	je	0x15e424 <symbol>
  15e41b:      	leaq	0x10(%rcx), %rdx
  15e41f:      	movl	0x8(%rcx), %ecx
  15e422:      	jmp	0x15e428 <symbol>
  15e424:      	xorl	%edx, %edx
  15e426:      	xorl	%ecx, %ecx
  15e428:      	cmpl	%ecx, %edi
  15e42a:      	jae	0x15e5d1 <symbol>
  15e430:      	movl	%edi, %ecx
  15e432:      	leaq	(%rdx,%rcx,8), %rcx
  15e436:      	movl	(%rcx), %edi
  15e438:      	movl	0x4(%rcx), %ebp
  15e43b:      	leaq	0xc0(%rsp), %rcx
  15e443:      	leaq	0x120(%rsp), %rdx
  15e44b:      	movl	$0x60, %r8d
  15e451:      	callq	0x87e30 <symbol>
  15e456:      	jmp	0x15e4f6 <symbol>
  15e45b:      	cmpl	0x108(%rsp), %r15d
  15e463:      	jae	0x15e5d1 <symbol>
  15e469:      	movl	%r15d, %eax
  15e46c:      	imulq	$0x30, %rax, %rax
  15e470:      	addq	0x100(%rsp), %rax
  15e478:      	cmpl	$0x0, (%rsi)
  15e47b:      	je	0x15e482 <symbol>
  15e47d:      	cmpl	$0x0, (%rax)
  15e480:      	jne	0x15e4f6 <symbol>
  15e482:      	movq	0x8(%r13), %rax
  15e486:      	movq	0x8(%rax), %rax
  15e48a:      	testq	%rax, %rax
  15e48d:      	je	0x15e498 <symbol>
  15e48f:      	leaq	0x10(%rax), %rcx
  15e493:      	movl	0x8(%rax), %eax
  15e496:      	jmp	0x15e49c <symbol>
  15e498:      	xorl	%ecx, %ecx
  15e49a:      	xorl	%eax, %eax
  15e49c:      	cmpl	%eax, %r15d
  15e49f:      	jae	0x15e5d1 <symbol>
  15e4a5:      	movl	%r15d, %eax
  15e4a8:      	leaq	(%rcx,%rax,8), %rax
  15e4ac:      	movl	(%rax), %ecx
  15e4ae:      	movl	0x4(%rax), %eax
  15e4b1:      	cmpl	%ecx, %edi
  15e4b3:      	jl	0x15e4c5 <symbol>
  15e4b5:      	cmpl	%ecx, %edi
  15e4b7:      	jne	0x15e5ad <symbol>
  15e4bd:      	cmpl	%eax, %ebp
  15e4bf:      	jge	0x15e5ad <symbol>
  15e4c5:      	movl	%edi, %edx
  15e4c7:      	movl	%ebp, %r8d
  15e4ca:      	movl	0x10(%rbx), %r10d
  15e4ce:      	leal	0x1(%r10), %r9d
  15e4d2:      	movl	%r9d, 0x10(%rbx)
  15e4d6:      	cmpl	0x8(%rbx), %r10d
  15e4da:      	jae	0x15e5d1 <symbol>
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
  15e519:      	callq	0xd7e0 <symbol>
  15e51e:      	testl	%eax, %eax
  15e520:      	jne	0x15e56b <symbol>
  15e522:      	leaq	0xc8(%rsp), %rcx
  15e52a:      	callq	0x154ae0 <symbol>
  15e52f:      	testl	%eax, %eax
  15e531:      	je	0x15e33a <symbol>
  15e537:      	movl	$0xffffffff, 0xc0(%rsp)
  15e542:      	leaq	0xc0(%rsp), %r9
  15e54a:      	movq	0xd8(%rsp), %rcx
  15e552:      	movq	0xe0(%rsp), %rdx
  15e55a:      	movl	0xe8(%rsp), %r8d
  15e562:      	callq	0xd7e0 <symbol>
  15e567:      	testl	%eax, %eax
  15e569:      	je	0x15e522 <symbol>
  15e56b:      	movl	0xc0(%rsp), %r15d
  15e573:      	movq	0xe0(%rsp), %r8
  15e57b:      	movq	%r8, %r13
  15e57e:      	cmpq	%r8, %r14
  15e581:      	je	0x15e598 <symbol>
  15e583:      	leaq	0xc0(%rsp), %rcx
  15e58b:      	movq	0xd8(%rsp), %rdx
  15e593:      	callq	0x161240 <symbol>
  15e598:      	cmpl	$0x0, 0x108(%rsp)
  15e5a0:      	jne	0x15e45b <symbol>
  15e5a6:      	xorl	%eax, %eax
  15e5a8:      	jmp	0x15e478 <symbol>
  15e5ad:      	movl	%ecx, %edx
  15e5af:      	movl	%eax, %r8d
  15e5b2:      	movl	%edi, %ecx
  15e5b4:      	movl	%ebp, %eax
  15e5b6:      	jmp	0x15e4ca <symbol>
  15e5bb:      	movb	$0x1, 0x18(%rbx)
  15e5bf:      	addq	$0x180, %rsp
  15e5c6:      	popq	%rbx
  15e5c7:      	popq	%rbp
  15e5c8:      	popq	%rsi
  15e5c9:      	popq	%rdi
  15e5ca:      	popq	%r13
  15e5cc:      	popq	%r14
  15e5ce:      	popq	%r15
  15e5d0:      	retq
  15e5d1:      	callq	0x10c710 <symbol>
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

