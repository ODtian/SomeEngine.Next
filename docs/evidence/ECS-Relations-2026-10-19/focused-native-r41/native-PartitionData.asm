00000000001a7390 <symbol>
  1a7390:      	pushq	%r14
  1a7392:      	pushq	%rdi
  1a7393:      	pushq	%rsi
  1a7394:      	pushq	%rbp
  1a7395:      	pushq	%rbx
  1a7396:      	subq	$0x110, %rsp
  1a739d:      	xorl	%eax, %eax
  1a739f:      	movq	%rax, 0x38(%rsp)
  1a73a4:      	xorps	%xmm4, %xmm4
  1a73a7:      	movaps	%xmm4, 0x40(%rsp)
  1a73ac:      	movabsq	$-0xc0, %rax
  1a73b6:      	movaps	%xmm4, 0x110(%rsp,%rax)
  1a73be:      	movaps	%xmm4, 0x120(%rsp,%rax)
  1a73c6:      	movaps	%xmm4, 0x130(%rsp,%rax)
  1a73ce:      	addq	$0x30, %rax
  1a73d2:      	jne	0x1a73b6 <symbol>
  1a73d4:      	movq	%rcx, %rbx
  1a73d7:      	movl	%edx, %esi
  1a73d9:      	leaq	0x38(%rbx), %rdx
  1a73dd:      	movq	(%rdx), %rdi
  1a73e0:      	movq	0x10(%rdx), %rbp
  1a73e4:      	leaq	0xc8(%rsp), %rcx
  1a73ec:      	cmpb	%dl, (%rdx)
  1a73ee:      	movl	$0x48, %r8d
  1a73f4:      	callq	0x88010 <symbol>
  1a73f9:      	leaq	0x8(%rbx), %rdx
  1a73fd:      	addq	$0x20, %rbx
  1a7401:      	movups	0xd0(%rsp), %xmm0
  1a7409:      	movups	%xmm0, 0x80(%rsp)
  1a7411:      	movups	0xe0(%rsp), %xmm0
  1a7419:      	movups	%xmm0, 0x90(%rsp)
  1a7421:      	movups	0xf0(%rsp), %xmm0
  1a7429:      	movups	%xmm0, 0xa0(%rsp)
  1a7431:      	movups	0x100(%rsp), %xmm0
  1a7439:      	movups	%xmm0, 0xb0(%rsp)
  1a7441:      	movq	0x10(%rdi), %r8
  1a7445:      	cmpl	0x8(%r8), %esi
  1a7449:      	jae	0x1a74e1 <symbol>
  1a744f:      	movl	%esi, %r14d
  1a7452:      	leaq	0x10(%r8,%r14,8), %r8
  1a7457:      	movl	(%r8), %ecx
  1a745a:      	movl	0x4(%r8), %r8d
  1a745e:      	movq	%rdx, 0x38(%rsp)
  1a7463:      	movq	%rbx, 0x40(%rsp)
  1a7468:      	xorl	%edx, %edx
  1a746a:      	movq	%rdx, 0x48(%rsp)
  1a746f:      	movq	%rdx, 0x50(%rsp)
  1a7474:      	movq	%rdx, 0x58(%rsp)
  1a7479:      	movq	%rdx, 0x60(%rsp)
  1a747e:      	movq	%rdx, 0x68(%rsp)
  1a7483:      	movq	%rdx, 0x70(%rsp)
  1a7488:      	movq	%rdx, 0x78(%rsp)
  1a748d:      	movl	%ecx, 0x20(%rsp)
  1a7491:      	movl	%r8d, 0x24(%rsp)
  1a7496:      	leaq	0xc0(%rsp), %rdx
  1a749e:      	movq	%rdx, 0x28(%rsp)
  1a74a3:      	leaq	0x38(%rsp), %rdx
  1a74a8:      	leaq	0xa0(%rsp), %r8
  1a74b0:      	movq	%rbp, %rcx
  1a74b3:      	movl	$0x1, %r9d
  1a74b9:      	callq	0x226880 <symbol>
  1a74be:      	movq	0x18(%rdi), %rax
  1a74c2:      	cmpl	0x8(%rax), %esi
  1a74c5:      	jae	0x1a74e1 <symbol>
  1a74c7:      	movl	0xc0(%rsp), %ecx
  1a74ce:      	movl	%ecx, 0x10(%rax,%r14,4)
  1a74d3:      	addq	$0x110, %rsp
  1a74da:      	popq	%rbx
  1a74db:      	popq	%rbp
  1a74dc:      	popq	%rsi
  1a74dd:      	popq	%rdi
  1a74de:      	popq	%r14
  1a74e0:      	retq
  1a74e1:      	callq	0x10c8f0 <symbol>
  1a74e6:      	int3
  1a74e7:      	nop
  1a74e8:      	nop
  1a74e9:      	nop
  1a74ea:      	nop
  1a74eb:      	nop
  1a74ec:      	nop
  1a74ed:      	nop
  1a74ee:      	nop
  1a74ef:      	nop

