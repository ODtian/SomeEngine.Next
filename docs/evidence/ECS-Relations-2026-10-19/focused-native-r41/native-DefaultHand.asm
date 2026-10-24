000000000015e460 <symbol>
  15e460:      	pushq	%r15
  15e462:      	pushq	%r14
  15e464:      	pushq	%r13
  15e466:      	pushq	%rdi
  15e467:      	pushq	%rsi
  15e468:      	pushq	%rbp
  15e469:      	pushq	%rbx
  15e46a:      	subq	$0x180, %rsp
  15e471:      	xorps	%xmm4, %xmm4
  15e474:      	movaps	%xmm4, 0x20(%rsp)
  15e479:      	movabsq	$-0x150, %rax
  15e483:      	movaps	%xmm4, 0x180(%rsp,%rax)
  15e48b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  15e493:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  15e49b:      	addq	$0x30, %rax
  15e49f:      	jne	0x15e483 <symbol>
  15e4a1:      	movq	%rdx, 0x1c8(%rsp)
  15e4a9:      	movq	%rcx, %rbx
  15e4ac:      	leaq	0x1c8(%rsp), %rcx
  15e4b4:      	leaq	0x20(%rsp), %rdx
  15e4b9:      	callq	0xb130 <symbol>
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
  15e515:      	callq	0x88010 <symbol>
  15e51a:      	movq	0x140(%rsp), %rdx
  15e522:      	movq	%rdx, %rsi
  15e525:      	leaq	0x120(%rsp), %r9
  15e52d:      	movq	0x138(%rsp), %rcx
  15e535:      	movl	0x148(%rsp), %r8d
  15e53d:      	callq	0xd740 <symbol>
  15e542:      	testl	%eax, %eax
  15e544:      	jne	0x15e58f <symbol>
  15e546:      	leaq	0x128(%rsp), %rcx
  15e54e:      	callq	0x154cc0 <symbol>
  15e553:      	testl	%eax, %eax
  15e555:      	je	0x15e787 <symbol>
  15e55b:      	movl	$0xffffffff, 0x120(%rsp)
  15e566:      	leaq	0x120(%rsp), %r9
  15e56e:      	movq	0x138(%rsp), %rcx
  15e576:      	movq	0x140(%rsp), %rdx
  15e57e:      	movl	0x148(%rsp), %r8d
  15e586:      	callq	0xd740 <symbol>
  15e58b:      	testl	%eax, %eax
  15e58d:      	je	0x15e546 <symbol>
  15e58f:      	movl	0x120(%rsp), %edi
  15e596:      	movq	0x140(%rsp), %rbp
  15e59e:      	cmpq	0x140(%rsp), %rsi
  15e5a6:      	je	0x15e5c5 <symbol>
  15e5a8:      	leaq	0x120(%rsp), %rcx
  15e5b0:      	movq	0x138(%rsp), %rdx
  15e5b8:      	movq	0x140(%rsp), %r8
  15e5c0:      	callq	0x161420 <symbol>
  15e5c5:      	cmpl	$0x0, 0x168(%rsp)
  15e5cd:      	jne	0x15e5d3 <symbol>
  15e5cf:      	xorl	%esi, %esi
  15e5d1:      	jmp	0x15e5e5 <symbol>
  15e5d3:      	movq	0x160(%rsp), %rsi
  15e5db:      	movslq	%edi, %rcx
  15e5de:      	imulq	$0x30, %rcx, %rcx
  15e5e2:      	addq	%rcx, %rsi
  15e5e5:      	movq	0x8(%rbp), %rcx
  15e5e9:      	movq	0x8(%rcx), %rcx
  15e5ed:      	testq	%rcx, %rcx
  15e5f0:      	je	0x15e5fb <symbol>
  15e5f2:      	leaq	0x10(%rcx), %rdx
  15e5f6:      	movl	0x8(%rcx), %ecx
  15e5f9:      	jmp	0x15e5ff <symbol>
  15e5fb:      	xorl	%edx, %edx
  15e5fd:      	xorl	%ecx, %ecx
  15e5ff:      	cmpl	%ecx, %edi
  15e601:      	jae	0x15e79d <symbol>
  15e607:      	movl	%edi, %ecx
  15e609:      	leaq	(%rdx,%rcx,8), %rcx
  15e60d:      	movl	(%rcx), %edi
  15e60f:      	movl	0x4(%rcx), %ebp
  15e612:      	leaq	0xc0(%rsp), %rcx
  15e61a:      	leaq	0x120(%rsp), %rdx
  15e622:      	movl	$0x60, %r8d
  15e628:      	callq	0x88010 <symbol>
  15e62d:      	jmp	0x15e6c2 <symbol>
  15e632:      	movq	0x100(%rsp), %rax
  15e63a:      	movslq	%r15d, %rcx
  15e63d:      	imulq	$0x30, %rcx, %rcx
  15e641:      	addq	%rcx, %rax
  15e644:      	cmpl	$0x0, (%rsi)
  15e647:      	je	0x15e64e <symbol>
  15e649:      	cmpl	$0x0, (%rax)
  15e64c:      	jne	0x15e6c2 <symbol>
  15e64e:      	movq	0x8(%r13), %rax
  15e652:      	movq	0x8(%rax), %rax
  15e656:      	testq	%rax, %rax
  15e659:      	je	0x15e664 <symbol>
  15e65b:      	leaq	0x10(%rax), %rcx
  15e65f:      	movl	0x8(%rax), %eax
  15e662:      	jmp	0x15e668 <symbol>
  15e664:      	xorl	%ecx, %ecx
  15e666:      	xorl	%eax, %eax
  15e668:      	cmpl	%eax, %r15d
  15e66b:      	jae	0x15e79d <symbol>
  15e671:      	movl	%r15d, %eax
  15e674:      	leaq	(%rcx,%rax,8), %rax
  15e678:      	movl	(%rax), %ecx
  15e67a:      	movl	0x4(%rax), %eax
  15e67d:      	cmpl	%ecx, %edi
  15e67f:      	jl	0x15e691 <symbol>
  15e681:      	cmpl	%ecx, %edi
  15e683:      	jne	0x15e779 <symbol>
  15e689:      	cmpl	%eax, %ebp
  15e68b:      	jge	0x15e779 <symbol>
  15e691:      	movl	%edi, %edx
  15e693:      	movl	%ebp, %r8d
  15e696:      	movl	0x10(%rbx), %r10d
  15e69a:      	leal	0x1(%r10), %r9d
  15e69e:      	movl	%r9d, 0x10(%rbx)
  15e6a2:      	cmpl	0x8(%rbx), %r10d
  15e6a6:      	jae	0x15e79d <symbol>
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
  15e6e5:      	callq	0xd740 <symbol>
  15e6ea:      	testl	%eax, %eax
  15e6ec:      	jne	0x15e737 <symbol>
  15e6ee:      	leaq	0xc8(%rsp), %rcx
  15e6f6:      	callq	0x154cc0 <symbol>
  15e6fb:      	testl	%eax, %eax
  15e6fd:      	je	0x15e51a <symbol>
  15e703:      	movl	$0xffffffff, 0xc0(%rsp)
  15e70e:      	leaq	0xc0(%rsp), %r9
  15e716:      	movq	0xd8(%rsp), %rcx
  15e71e:      	movq	0xe0(%rsp), %rdx
  15e726:      	movl	0xe8(%rsp), %r8d
  15e72e:      	callq	0xd740 <symbol>
  15e733:      	testl	%eax, %eax
  15e735:      	je	0x15e6ee <symbol>
  15e737:      	movl	0xc0(%rsp), %r15d
  15e73f:      	movq	0xe0(%rsp), %r8
  15e747:      	movq	%r8, %r13
  15e74a:      	cmpq	%r8, %r14
  15e74d:      	je	0x15e764 <symbol>
  15e74f:      	leaq	0xc0(%rsp), %rcx
  15e757:      	movq	0xd8(%rsp), %rdx
  15e75f:      	callq	0x161420 <symbol>
  15e764:      	cmpl	$0x0, 0x108(%rsp)
  15e76c:      	jne	0x15e632 <symbol>
  15e772:      	xorl	%eax, %eax
  15e774:      	jmp	0x15e644 <symbol>
  15e779:      	movl	%ecx, %edx
  15e77b:      	movl	%eax, %r8d
  15e77e:      	movl	%edi, %ecx
  15e780:      	movl	%ebp, %eax
  15e782:      	jmp	0x15e696 <symbol>
  15e787:      	movb	$0x1, 0x18(%rbx)
  15e78b:      	addq	$0x180, %rsp
  15e792:      	popq	%rbx
  15e793:      	popq	%rbp
  15e794:      	popq	%rsi
  15e795:      	popq	%rdi
  15e796:      	popq	%r13
  15e798:      	popq	%r14
  15e79a:      	popq	%r15
  15e79c:      	retq
  15e79d:      	callq	0x10c8f0 <symbol>
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

