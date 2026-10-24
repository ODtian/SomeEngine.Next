00000000001604a0 <symbol>
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
  1604ba:      	jle	0x16052c <symbol>
  1604bc:      	xorl	%r14d, %r14d
  1604bf:      	movups	(%rdi,%r14), %xmm0
  1604c4:      	movups	%xmm0, 0x28(%rsp)
  1604c9:      	movq	%rsi, %rcx
  1604cc:      	movq	0x28(%rsp), %rdx
  1604d1:      	callq	0x1d95c0 <symbol>
  1604d6:      	movq	%rax, %r15
  1604d9:      	testq	%r15, %r15
  1604dc:      	je	0x160524 <symbol>
  1604de:      	movq	%rsi, %rcx
  1604e1:      	movq	0x30(%rsp), %rdx
  1604e6:      	callq	0x1d95c0 <symbol>
  1604eb:      	testq	%rax, %rax
  1604ee:      	je	0x160524 <symbol>
  1604f0:      	movq	0x8(%r15), %rcx
  1604f4:      	testq	%rcx, (%rax)
  1604f7:      	je	0x160524 <symbol>
  1604f9:      	movq	0x8(%rax), %rax
  1604fd:      	testq	%rax, (%r15)
  160500:      	je	0x160524 <symbol>
  160502:      	leaq	0x10(%rbx), %rax
  160506:      	movl	0x10(%rax), %ecx
  160509:      	leal	0x1(%rcx), %edx
  16050c:      	movl	%edx, 0x10(%rax)
  16050f:      	cmpl	0x8(%rax), %ecx
  160512:      	jae	0x16053d <symbol>
  160514:      	movq	(%rax), %rax
  160517:      	shlq	$0x4, %rcx
  16051b:      	movups	0x28(%rsp), %xmm0
  160520:      	movups	%xmm0, (%rax,%rcx)
  160524:      	addq	$0x10, %r14
  160528:      	decl	%ebp
  16052a:      	jne	0x1604bf <symbol>
  16052c:      	movb	$0x1, 0x28(%rbx)
  160530:      	addq	$0x38, %rsp
  160534:      	popq	%rbx
  160535:      	popq	%rbp
  160536:      	popq	%rsi
  160537:      	popq	%rdi
  160538:      	popq	%r14
  16053a:      	popq	%r15
  16053c:      	retq
  16053d:      	callq	0x10c710 <symbol>
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

