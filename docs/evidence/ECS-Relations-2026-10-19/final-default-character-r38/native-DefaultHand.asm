000000000015e300 <symbol>
  15e300:      	pushq	%r15
  15e302:      	pushq	%r14
  15e304:      	pushq	%r13
  15e306:      	pushq	%rdi
  15e307:      	pushq	%rsi
  15e308:      	pushq	%rbp
  15e309:      	pushq	%rbx
  15e30a:      	subq	$0x180, %rsp
  15e311:      	xorps	%xmm4, %xmm4
  15e314:      	movaps	%xmm4, 0x20(%rsp)
  15e319:      	movabsq	$-0x150, %rax
  15e323:      	movaps	%xmm4, 0x180(%rsp,%rax)
  15e32b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  15e333:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  15e33b:      	addq	$0x30, %rax
  15e33f:      	jne	0x15e323 <symbol>
  15e341:      	movq	%rdx, 0x1c8(%rsp)
  15e349:      	movq	%rcx, %rbx
  15e34c:      	leaq	0x1c8(%rsp), %rcx
  15e354:      	leaq	0x20(%rsp), %rdx
  15e359:      	callq	0xb210 <symbol>
  15e35e:      	movups	0x20(%rsp), %xmm0
  15e363:      	movups	%xmm0, 0x60(%rsp)
  15e368:      	movups	0x30(%rsp), %xmm0
  15e36d:      	movups	%xmm0, 0x70(%rsp)
  15e372:      	movups	0x40(%rsp), %xmm0
  15e377:      	movups	%xmm0, 0x80(%rsp)
  15e37f:      	movups	0x50(%rsp), %xmm0
  15e384:      	movups	%xmm0, 0x90(%rsp)
  15e38c:      	xorps	%xmm0, %xmm0
  15e38f:      	movups	%xmm0, 0xa0(%rsp)
  15e397:      	xorps	%xmm0, %xmm0
  15e39a:      	movups	%xmm0, 0xb0(%rsp)
  15e3a2:      	leaq	0x120(%rsp), %rcx
  15e3aa:      	leaq	0x60(%rsp), %rdx
  15e3af:      	movl	$0x60, %r8d
  15e3b5:      	callq	0x87eb0 <symbol>
  15e3ba:      	movq	0x140(%rsp), %rdx
  15e3c2:      	movq	%rdx, %rsi
  15e3c5:      	leaq	0x120(%rsp), %r9
  15e3cd:      	movq	0x138(%rsp), %rcx
  15e3d5:      	movl	0x148(%rsp), %r8d
  15e3dd:      	callq	0xd820 <symbol>
  15e3e2:      	testl	%eax, %eax
  15e3e4:      	jne	0x15e42f <symbol>
  15e3e6:      	leaq	0x128(%rsp), %rcx
  15e3ee:      	callq	0x154b60 <symbol>
  15e3f3:      	testl	%eax, %eax
  15e3f5:      	je	0x15e63b <symbol>
  15e3fb:      	movl	$0xffffffff, 0x120(%rsp)
  15e406:      	leaq	0x120(%rsp), %r9
  15e40e:      	movq	0x138(%rsp), %rcx
  15e416:      	movq	0x140(%rsp), %rdx
  15e41e:      	movl	0x148(%rsp), %r8d
  15e426:      	callq	0xd820 <symbol>
  15e42b:      	testl	%eax, %eax
  15e42d:      	je	0x15e3e6 <symbol>
  15e42f:      	movl	0x120(%rsp), %edi
  15e436:      	movq	0x140(%rsp), %rbp
  15e43e:      	cmpq	0x140(%rsp), %rsi
  15e446:      	je	0x15e465 <symbol>
  15e448:      	leaq	0x120(%rsp), %rcx
  15e450:      	movq	0x138(%rsp), %rdx
  15e458:      	movq	0x140(%rsp), %r8
  15e460:      	callq	0x1612c0 <symbol>
  15e465:      	cmpl	$0x0, 0x168(%rsp)
  15e46d:      	jne	0x15e473 <symbol>
  15e46f:      	xorl	%esi, %esi
  15e471:      	jmp	0x15e48e <symbol>
  15e473:      	cmpl	0x168(%rsp), %edi
  15e47a:      	jae	0x15e651 <symbol>
  15e480:      	movl	%edi, %ecx
  15e482:      	imulq	$0x30, %rcx, %rsi
  15e486:      	addq	0x160(%rsp), %rsi
  15e48e:      	movq	0x8(%rbp), %rcx
  15e492:      	movq	0x8(%rcx), %rcx
  15e496:      	testq	%rcx, %rcx
  15e499:      	je	0x15e4a4 <symbol>
  15e49b:      	leaq	0x10(%rcx), %rdx
  15e49f:      	movl	0x8(%rcx), %ecx
  15e4a2:      	jmp	0x15e4a8 <symbol>
  15e4a4:      	xorl	%edx, %edx
  15e4a6:      	xorl	%ecx, %ecx
  15e4a8:      	cmpl	%ecx, %edi
  15e4aa:      	jae	0x15e651 <symbol>
  15e4b0:      	movl	%edi, %ecx
  15e4b2:      	leaq	(%rdx,%rcx,8), %rcx
  15e4b6:      	movl	(%rcx), %edi
  15e4b8:      	movl	0x4(%rcx), %ebp
  15e4bb:      	leaq	0xc0(%rsp), %rcx
  15e4c3:      	leaq	0x120(%rsp), %rdx
  15e4cb:      	movl	$0x60, %r8d
  15e4d1:      	callq	0x87eb0 <symbol>
  15e4d6:      	jmp	0x15e576 <symbol>
  15e4db:      	cmpl	0x108(%rsp), %r15d
  15e4e3:      	jae	0x15e651 <symbol>
  15e4e9:      	movl	%r15d, %eax
  15e4ec:      	imulq	$0x30, %rax, %rax
  15e4f0:      	addq	0x100(%rsp), %rax
  15e4f8:      	cmpl	$0x0, (%rsi)
  15e4fb:      	je	0x15e502 <symbol>
  15e4fd:      	cmpl	$0x0, (%rax)
  15e500:      	jne	0x15e576 <symbol>
  15e502:      	movq	0x8(%r13), %rax
  15e506:      	movq	0x8(%rax), %rax
  15e50a:      	testq	%rax, %rax
  15e50d:      	je	0x15e518 <symbol>
  15e50f:      	leaq	0x10(%rax), %rcx
  15e513:      	movl	0x8(%rax), %eax
  15e516:      	jmp	0x15e51c <symbol>
  15e518:      	xorl	%ecx, %ecx
  15e51a:      	xorl	%eax, %eax
  15e51c:      	cmpl	%eax, %r15d
  15e51f:      	jae	0x15e651 <symbol>
  15e525:      	movl	%r15d, %eax
  15e528:      	leaq	(%rcx,%rax,8), %rax
  15e52c:      	movl	(%rax), %ecx
  15e52e:      	movl	0x4(%rax), %eax
  15e531:      	cmpl	%ecx, %edi
  15e533:      	jl	0x15e545 <symbol>
  15e535:      	cmpl	%ecx, %edi
  15e537:      	jne	0x15e62d <symbol>
  15e53d:      	cmpl	%eax, %ebp
  15e53f:      	jge	0x15e62d <symbol>
  15e545:      	movl	%edi, %edx
  15e547:      	movl	%ebp, %r8d
  15e54a:      	movl	0x10(%rbx), %r10d
  15e54e:      	leal	0x1(%r10), %r9d
  15e552:      	movl	%r9d, 0x10(%rbx)
  15e556:      	cmpl	0x8(%rbx), %r10d
  15e55a:      	jae	0x15e651 <symbol>
  15e560:      	shlq	$0x4, %r10
  15e564:      	addq	(%rbx), %r10
  15e567:      	movl	%edx, (%r10)
  15e56a:      	movl	%r8d, 0x4(%r10)
  15e56e:      	movl	%ecx, 0x8(%r10)
  15e572:      	movl	%eax, 0xc(%r10)
  15e576:      	movq	0xe0(%rsp), %rdx
  15e57e:      	movq	%rdx, %r14
  15e581:      	leaq	0xc0(%rsp), %r9
  15e589:      	movq	0xd8(%rsp), %rcx
  15e591:      	movl	0xe8(%rsp), %r8d
  15e599:      	callq	0xd820 <symbol>
  15e59e:      	testl	%eax, %eax
  15e5a0:      	jne	0x15e5eb <symbol>
  15e5a2:      	leaq	0xc8(%rsp), %rcx
  15e5aa:      	callq	0x154b60 <symbol>
  15e5af:      	testl	%eax, %eax
  15e5b1:      	je	0x15e3ba <symbol>
  15e5b7:      	movl	$0xffffffff, 0xc0(%rsp)
  15e5c2:      	leaq	0xc0(%rsp), %r9
  15e5ca:      	movq	0xd8(%rsp), %rcx
  15e5d2:      	movq	0xe0(%rsp), %rdx
  15e5da:      	movl	0xe8(%rsp), %r8d
  15e5e2:      	callq	0xd820 <symbol>
  15e5e7:      	testl	%eax, %eax
  15e5e9:      	je	0x15e5a2 <symbol>
  15e5eb:      	movl	0xc0(%rsp), %r15d
  15e5f3:      	movq	0xe0(%rsp), %r8
  15e5fb:      	movq	%r8, %r13
  15e5fe:      	cmpq	%r8, %r14
  15e601:      	je	0x15e618 <symbol>
  15e603:      	leaq	0xc0(%rsp), %rcx
  15e60b:      	movq	0xd8(%rsp), %rdx
  15e613:      	callq	0x1612c0 <symbol>
  15e618:      	cmpl	$0x0, 0x108(%rsp)
  15e620:      	jne	0x15e4db <symbol>
  15e626:      	xorl	%eax, %eax
  15e628:      	jmp	0x15e4f8 <symbol>
  15e62d:      	movl	%ecx, %edx
  15e62f:      	movl	%eax, %r8d
  15e632:      	movl	%edi, %ecx
  15e634:      	movl	%ebp, %eax
  15e636:      	jmp	0x15e54a <symbol>
  15e63b:      	movb	$0x1, 0x18(%rbx)
  15e63f:      	addq	$0x180, %rsp
  15e646:      	popq	%rbx
  15e647:      	popq	%rbp
  15e648:      	popq	%rsi
  15e649:      	popq	%rdi
  15e64a:      	popq	%r13
  15e64c:      	popq	%r14
  15e64e:      	popq	%r15
  15e650:      	retq
  15e651:      	callq	0x10c790 <symbol>
  15e656:      	int3
  15e657:      	nop
  15e658:      	nop
  15e659:      	nop
  15e65a:      	nop
  15e65b:      	nop
  15e65c:      	nop
  15e65d:      	nop
  15e65e:      	nop
  15e65f:      	nop
