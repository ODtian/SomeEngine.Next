00000000001d3280 <symbol>
  1d3280:      	pushq	%r15
  1d3282:      	pushq	%r14
  1d3284:      	pushq	%r13
  1d3286:      	pushq	%rdi
  1d3287:      	pushq	%rsi
  1d3288:      	pushq	%rbp
  1d3289:      	pushq	%rbx
  1d328a:      	subq	$0x180, %rsp
  1d3291:      	xorps	%xmm4, %xmm4
  1d3294:      	movaps	%xmm4, 0x20(%rsp)
  1d3299:      	movabsq	$-0x150, %rax
  1d32a3:      	movaps	%xmm4, 0x180(%rsp,%rax)
  1d32ab:      	movaps	%xmm4, 0x190(%rsp,%rax)
  1d32b3:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  1d32bb:      	addq	$0x30, %rax
  1d32bf:      	jne	0x1d32a3 <symbol>
  1d32c1:      	movq	%rcx, 0x1c0(%rsp)
  1d32c9:      	movq	%r8, %rbx
  1d32cc:      	leaq	0x1c0(%rsp), %rcx
  1d32d4:      	leaq	0x20(%rsp), %rdx
  1d32d9:      	callq	0xb210 <symbol>
  1d32de:      	movups	0x20(%rsp), %xmm0
  1d32e3:      	movups	%xmm0, 0x60(%rsp)
  1d32e8:      	movups	0x30(%rsp), %xmm0
  1d32ed:      	movups	%xmm0, 0x70(%rsp)
  1d32f2:      	movups	0x40(%rsp), %xmm0
  1d32f7:      	movups	%xmm0, 0x80(%rsp)
  1d32ff:      	movups	0x50(%rsp), %xmm0
  1d3304:      	movups	%xmm0, 0x90(%rsp)
  1d330c:      	xorps	%xmm0, %xmm0
  1d330f:      	movups	%xmm0, 0xa0(%rsp)
  1d3317:      	xorps	%xmm0, %xmm0
  1d331a:      	movups	%xmm0, 0xb0(%rsp)
  1d3322:      	leaq	0x120(%rsp), %rcx
  1d332a:      	leaq	0x60(%rsp), %rdx
  1d332f:      	movl	$0x60, %r8d
  1d3335:      	callq	0x87eb0 <symbol>
  1d333a:      	movq	0x140(%rsp), %rdx
  1d3342:      	movq	%rdx, %rsi
  1d3345:      	leaq	0x120(%rsp), %r9
  1d334d:      	movq	0x138(%rsp), %rcx
  1d3355:      	movl	0x148(%rsp), %r8d
  1d335d:      	callq	0xd820 <symbol>
  1d3362:      	testl	%eax, %eax
  1d3364:      	jne	0x1d33af <symbol>
  1d3366:      	leaq	0x128(%rsp), %rcx
  1d336e:      	callq	0x154b60 <symbol>
  1d3373:      	testl	%eax, %eax
  1d3375:      	je	0x1d35be <symbol>
  1d337b:      	movl	$0xffffffff, 0x120(%rsp)
  1d3386:      	leaq	0x120(%rsp), %r9
  1d338e:      	movq	0x138(%rsp), %rcx
  1d3396:      	movq	0x140(%rsp), %rdx
  1d339e:      	movl	0x148(%rsp), %r8d
  1d33a6:      	callq	0xd820 <symbol>
  1d33ab:      	testl	%eax, %eax
  1d33ad:      	je	0x1d3366 <symbol>
  1d33af:      	movl	0x120(%rsp), %edi
  1d33b6:      	movq	0x140(%rsp), %rbp
  1d33be:      	cmpq	0x140(%rsp), %rsi
  1d33c6:      	je	0x1d33e5 <symbol>
  1d33c8:      	leaq	0x120(%rsp), %rcx
  1d33d0:      	movq	0x138(%rsp), %rdx
  1d33d8:      	movq	0x140(%rsp), %r8
  1d33e0:      	callq	0x1612c0 <symbol>
  1d33e5:      	cmpl	$0x0, 0x168(%rsp)
  1d33ed:      	jne	0x1d33f3 <symbol>
  1d33ef:      	xorl	%esi, %esi
  1d33f1:      	jmp	0x1d340e <symbol>
  1d33f3:      	cmpl	0x168(%rsp), %edi
  1d33fa:      	jae	0x1d35d5 <symbol>
  1d3400:      	movl	%edi, %ecx
  1d3402:      	imulq	$0x30, %rcx, %rsi
  1d3406:      	addq	0x160(%rsp), %rsi
  1d340e:      	movq	0x8(%rbp), %rcx
  1d3412:      	movq	0x8(%rcx), %rcx
  1d3416:      	testq	%rcx, %rcx
  1d3419:      	je	0x1d3424 <symbol>
  1d341b:      	leaq	0x10(%rcx), %rdx
  1d341f:      	movl	0x8(%rcx), %ecx
  1d3422:      	jmp	0x1d3428 <symbol>
  1d3424:      	xorl	%edx, %edx
  1d3426:      	xorl	%ecx, %ecx
  1d3428:      	cmpl	%ecx, %edi
  1d342a:      	jae	0x1d35d5 <symbol>
  1d3430:      	movl	%edi, %ecx
  1d3432:      	leaq	(%rdx,%rcx,8), %rcx
  1d3436:      	movl	(%rcx), %edi
  1d3438:      	movl	0x4(%rcx), %ebp
  1d343b:      	leaq	0xc0(%rsp), %rcx
  1d3443:      	leaq	0x120(%rsp), %rdx
  1d344b:      	movl	$0x60, %r8d
  1d3451:      	callq	0x87eb0 <symbol>
  1d3456:      	jmp	0x1d34f9 <symbol>
  1d345b:      	cmpl	0x108(%rsp), %r15d
  1d3463:      	jae	0x1d35d5 <symbol>
  1d3469:      	movl	%r15d, %eax
  1d346c:      	imulq	$0x30, %rax, %rax
  1d3470:      	addq	0x100(%rsp), %rax
  1d3478:      	cmpl	$0x0, (%rsi)
  1d347b:      	je	0x1d3482 <symbol>
  1d347d:      	cmpl	$0x0, (%rax)
  1d3480:      	jne	0x1d34f9 <symbol>
  1d3482:      	movq	0x8(%r13), %rax
  1d3486:      	movq	0x8(%rax), %rax
  1d348a:      	testq	%rax, %rax
  1d348d:      	je	0x1d3498 <symbol>
  1d348f:      	leaq	0x10(%rax), %rcx
  1d3493:      	movl	0x8(%rax), %eax
  1d3496:      	jmp	0x1d349c <symbol>
  1d3498:      	xorl	%ecx, %ecx
  1d349a:      	xorl	%eax, %eax
  1d349c:      	cmpl	%eax, %r15d
  1d349f:      	jae	0x1d35d5 <symbol>
  1d34a5:      	movl	%r15d, %eax
  1d34a8:      	leaq	(%rcx,%rax,8), %rax
  1d34ac:      	movl	(%rax), %ecx
  1d34ae:      	movl	0x4(%rax), %eax
  1d34b1:      	cmpl	%ecx, %edi
  1d34b3:      	jl	0x1d34c5 <symbol>
  1d34b5:      	cmpl	%ecx, %edi
  1d34b7:      	jne	0x1d35b0 <symbol>
  1d34bd:      	cmpl	%eax, %ebp
  1d34bf:      	jge	0x1d35b0 <symbol>
  1d34c5:      	movl	%edi, %edx
  1d34c7:      	movl	%ebp, %r8d
  1d34ca:      	movq	(%rbx), %r10
  1d34cd:      	movl	0x10(%r10), %r9d
  1d34d1:      	leal	0x1(%r9), %r11d
  1d34d5:      	movl	%r11d, 0x10(%r10)
  1d34d9:      	cmpl	0x8(%r10), %r9d
  1d34dd:      	jae	0x1d35d5 <symbol>
  1d34e3:      	shlq	$0x4, %r9
  1d34e7:      	addq	(%r10), %r9
  1d34ea:      	movl	%edx, (%r9)
  1d34ed:      	movl	%r8d, 0x4(%r9)
  1d34f1:      	movl	%ecx, 0x8(%r9)
  1d34f5:      	movl	%eax, 0xc(%r9)
  1d34f9:      	movq	0xe0(%rsp), %rdx
  1d3501:      	movq	%rdx, %r14
  1d3504:      	leaq	0xc0(%rsp), %r9
  1d350c:      	movq	0xd8(%rsp), %rcx
  1d3514:      	movl	0xe8(%rsp), %r8d
  1d351c:      	callq	0xd820 <symbol>
  1d3521:      	testl	%eax, %eax
  1d3523:      	jne	0x1d356e <symbol>
  1d3525:      	leaq	0xc8(%rsp), %rcx
  1d352d:      	callq	0x154b60 <symbol>
  1d3532:      	testl	%eax, %eax
  1d3534:      	je	0x1d333a <symbol>
  1d353a:      	movl	$0xffffffff, 0xc0(%rsp)
  1d3545:      	leaq	0xc0(%rsp), %r9
  1d354d:      	movq	0xd8(%rsp), %rcx
  1d3555:      	movq	0xe0(%rsp), %rdx
  1d355d:      	movl	0xe8(%rsp), %r8d
  1d3565:      	callq	0xd820 <symbol>
  1d356a:      	testl	%eax, %eax
  1d356c:      	je	0x1d3525 <symbol>
  1d356e:      	movl	0xc0(%rsp), %r15d
  1d3576:      	movq	0xe0(%rsp), %r8
  1d357e:      	movq	%r8, %r13
  1d3581:      	cmpq	%r8, %r14
  1d3584:      	je	0x1d359b <symbol>
  1d3586:      	leaq	0xc0(%rsp), %rcx
  1d358e:      	movq	0xd8(%rsp), %rdx
  1d3596:      	callq	0x1612c0 <symbol>
  1d359b:      	cmpl	$0x0, 0x108(%rsp)
  1d35a3:      	jne	0x1d345b <symbol>
  1d35a9:      	xorl	%eax, %eax
  1d35ab:      	jmp	0x1d3478 <symbol>
  1d35b0:      	movl	%ecx, %edx
  1d35b2:      	movl	%eax, %r8d
  1d35b5:      	movl	%edi, %ecx
  1d35b7:      	movl	%ebp, %eax
  1d35b9:      	jmp	0x1d34ca <symbol>
  1d35be:      	movl	$0x1, %eax
  1d35c3:      	addq	$0x180, %rsp
  1d35ca:      	popq	%rbx
  1d35cb:      	popq	%rbp
  1d35cc:      	popq	%rsi
  1d35cd:      	popq	%rdi
  1d35ce:      	popq	%r13
  1d35d0:      	popq	%r14
  1d35d2:      	popq	%r15
  1d35d4:      	retq
  1d35d5:      	callq	0x10c790 <symbol>
  1d35da:      	int3
  1d35db:      	nop
  1d35dc:      	nop
  1d35dd:      	nop
  1d35de:      	nop
  1d35df:      	nop
