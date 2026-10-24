0000000000160520 <symbol>
  160520:      	pushq	%r15
  160522:      	pushq	%r14
  160524:      	pushq	%rdi
  160525:      	pushq	%rsi
  160526:      	pushq	%rbp
  160527:      	pushq	%rbx
  160528:      	subq	$0x38, %rsp
  16052c:      	movq	%rcx, %rbx
  16052f:      	movq	%rdx, %rsi
  160532:      	movq	(%rbx), %rdi
  160535:      	movl	0x8(%rbx), %ebp
  160538:      	testl	%ebp, %ebp
  16053a:      	jle	0x1605ac <symbol>
  16053c:      	xorl	%r14d, %r14d
  16053f:      	movups	(%rdi,%r14), %xmm0
  160544:      	movups	%xmm0, 0x28(%rsp)
  160549:      	movq	%rsi, %rcx
  16054c:      	movq	0x28(%rsp), %rdx
  160551:      	callq	0x1db3a0 <symbol>
  160556:      	movq	%rax, %r15
  160559:      	testq	%r15, %r15
  16055c:      	je	0x1605a4 <symbol>
  16055e:      	movq	%rsi, %rcx
  160561:      	movq	0x30(%rsp), %rdx
  160566:      	callq	0x1db3a0 <symbol>
  16056b:      	testq	%rax, %rax
  16056e:      	je	0x1605a4 <symbol>
  160570:      	movq	0x8(%r15), %rcx
  160574:      	testq	%rcx, (%rax)
  160577:      	je	0x1605a4 <symbol>
  160579:      	movq	0x8(%rax), %rax
  16057d:      	testq	%rax, (%r15)
  160580:      	je	0x1605a4 <symbol>
  160582:      	leaq	0x10(%rbx), %rax
  160586:      	movl	0x10(%rax), %ecx
  160589:      	leal	0x1(%rcx), %edx
  16058c:      	movl	%edx, 0x10(%rax)
  16058f:      	cmpl	0x8(%rax), %ecx
  160592:      	jae	0x1605bd <symbol>
  160594:      	movq	(%rax), %rax
  160597:      	shlq	$0x4, %rcx
  16059b:      	movups	0x28(%rsp), %xmm0
  1605a0:      	movups	%xmm0, (%rax,%rcx)
  1605a4:      	addq	$0x10, %r14
  1605a8:      	decl	%ebp
  1605aa:      	jne	0x16053f <symbol>
  1605ac:      	movb	$0x1, 0x28(%rbx)
  1605b0:      	addq	$0x38, %rsp
  1605b4:      	popq	%rbx
  1605b5:      	popq	%rbp
  1605b6:      	popq	%rsi
  1605b7:      	popq	%rdi
  1605b8:      	popq	%r14
  1605ba:      	popq	%r15
  1605bc:      	retq
  1605bd:      	callq	0x10c790 <symbol>
  1605c2:      	int3
  1605c3:      	nop
  1605c4:      	nop
  1605c5:      	nop
  1605c6:      	nop
  1605c7:      	nop
  1605c8:      	nop
  1605c9:      	nop
  1605ca:      	nop
  1605cb:      	nop
  1605cc:      	nop
  1605cd:      	nop
  1605ce:      	nop
  1605cf:      	nop

