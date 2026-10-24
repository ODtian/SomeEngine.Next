0000000000160680 <symbol>
  160680:      	pushq	%r15
  160682:      	pushq	%r14
  160684:      	pushq	%rdi
  160685:      	pushq	%rsi
  160686:      	pushq	%rbp
  160687:      	pushq	%rbx
  160688:      	subq	$0x38, %rsp
  16068c:      	movq	%rcx, %rbx
  16068f:      	movq	%rdx, %rsi
  160692:      	movq	(%rbx), %rdi
  160695:      	movl	0x8(%rbx), %ebp
  160698:      	testl	%ebp, %ebp
  16069a:      	jle	0x16070c <symbol>
  16069c:      	xorl	%r14d, %r14d
  16069f:      	movups	(%rdi,%r14), %xmm0
  1606a4:      	movups	%xmm0, 0x28(%rsp)
  1606a9:      	movq	%rsi, %rcx
  1606ac:      	movq	0x28(%rsp), %rdx
  1606b1:      	callq	0x1d8a40 <symbol>
  1606b6:      	movq	%rax, %r15
  1606b9:      	testq	%r15, %r15
  1606bc:      	je	0x160704 <symbol>
  1606be:      	movq	%rsi, %rcx
  1606c1:      	movq	0x30(%rsp), %rdx
  1606c6:      	callq	0x1d8a40 <symbol>
  1606cb:      	testq	%rax, %rax
  1606ce:      	je	0x160704 <symbol>
  1606d0:      	movq	0x8(%r15), %rcx
  1606d4:      	testq	%rcx, (%rax)
  1606d7:      	je	0x160704 <symbol>
  1606d9:      	movq	0x8(%rax), %rax
  1606dd:      	testq	%rax, (%r15)
  1606e0:      	je	0x160704 <symbol>
  1606e2:      	leaq	0x10(%rbx), %rax
  1606e6:      	movl	0x10(%rax), %ecx
  1606e9:      	leal	0x1(%rcx), %edx
  1606ec:      	movl	%edx, 0x10(%rax)
  1606ef:      	cmpl	0x8(%rax), %ecx
  1606f2:      	jae	0x16071d <symbol>
  1606f4:      	movq	(%rax), %rax
  1606f7:      	shlq	$0x4, %rcx
  1606fb:      	movups	0x28(%rsp), %xmm0
  160700:      	movups	%xmm0, (%rax,%rcx)
  160704:      	addq	$0x10, %r14
  160708:      	decl	%ebp
  16070a:      	jne	0x16069f <symbol>
  16070c:      	movb	$0x1, 0x28(%rbx)
  160710:      	addq	$0x38, %rsp
  160714:      	popq	%rbx
  160715:      	popq	%rbp
  160716:      	popq	%rsi
  160717:      	popq	%rdi
  160718:      	popq	%r14
  16071a:      	popq	%r15
  16071c:      	retq
  16071d:      	callq	0x10c8f0 <symbol>
  160722:      	int3
  160723:      	nop
  160724:      	nop
  160725:      	nop
  160726:      	nop
  160727:      	nop
  160728:      	nop
  160729:      	nop
  16072a:      	nop
  16072b:      	nop
  16072c:      	nop
  16072d:      	nop
  16072e:      	nop
  16072f:      	nop

