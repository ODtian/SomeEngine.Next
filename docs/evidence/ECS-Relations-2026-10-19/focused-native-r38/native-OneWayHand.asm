0000000000160480 <symbol>
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
  16049a:      	jle	0x160503 <symbol>
  16049c:      	xorl	%r14d, %r14d
  16049f:      	movups	(%rdi,%r14), %xmm0
  1604a4:      	movups	%xmm0, 0x28(%rsp)
  1604a9:      	movq	%rsi, %rcx
  1604ac:      	movq	0x28(%rsp), %rdx
  1604b1:      	callq	0x1db3a0 <symbol>
  1604b6:      	movq	%rax, %r15
  1604b9:      	testq	%r15, %r15
  1604bc:      	je	0x1604fb <symbol>
  1604be:      	movq	%rsi, %rcx
  1604c1:      	movq	0x30(%rsp), %rdx
  1604c6:      	callq	0x1db3a0 <symbol>
  1604cb:      	testq	%rax, %rax
  1604ce:      	je	0x1604fb <symbol>
  1604d0:      	movq	0x8(%r15), %rcx
  1604d4:      	testq	%rcx, (%rax)
  1604d7:      	je	0x1604fb <symbol>
  1604d9:      	leaq	0x10(%rbx), %rax
  1604dd:      	movl	0x10(%rax), %ecx
  1604e0:      	leal	0x1(%rcx), %edx
  1604e3:      	movl	%edx, 0x10(%rax)
  1604e6:      	cmpl	0x8(%rax), %ecx
  1604e9:      	jae	0x160514 <symbol>
  1604eb:      	movq	(%rax), %rax
  1604ee:      	shlq	$0x4, %rcx
  1604f2:      	movups	0x28(%rsp), %xmm0
  1604f7:      	movups	%xmm0, (%rax,%rcx)
  1604fb:      	addq	$0x10, %r14
  1604ff:      	decl	%ebp
  160501:      	jne	0x16049f <symbol>
  160503:      	movb	$0x1, 0x28(%rbx)
  160507:      	addq	$0x38, %rsp
  16050b:      	popq	%rbx
  16050c:      	popq	%rbp
  16050d:      	popq	%rsi
  16050e:      	popq	%rdi
  16050f:      	popq	%r14
  160511:      	popq	%r15
  160513:      	retq
  160514:      	callq	0x10c790 <symbol>
  160519:      	int3
  16051a:      	nop
  16051b:      	nop
  16051c:      	nop
  16051d:      	nop
  16051e:      	nop
  16051f:      	nop

