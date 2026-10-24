000000000019c1a0 <symbol>
  19c1a0:      	pushq	%r14
  19c1a2:      	pushq	%rdi
  19c1a3:      	pushq	%rsi
  19c1a4:      	pushq	%rbp
  19c1a5:      	pushq	%rbx
  19c1a6:      	subq	$0x60, %rsp
  19c1aa:      	xorl	%eax, %eax
  19c1ac:      	movq	%rax, 0x38(%rsp)
  19c1b1:      	xorps	%xmm4, %xmm4
  19c1b4:      	movaps	%xmm4, 0x40(%rsp)
  19c1b9:      	movaps	%xmm4, 0x50(%rsp)
  19c1be:      	movq	%rdx, 0x98(%rsp)
  19c1c6:      	movq	(%rcx), %rbx
  19c1c9:      	movq	0x8(%rcx), %rsi
  19c1cd:      	movl	0x10(%rcx), %edi
  19c1d0:      	cmpb	%bl, (%rbx)
  19c1d2:      	movq	%gs:0x58, %rcx
  19c1db:      	movl	(%rip), %eax
		000000000019c1dd:  IMAGE_REL_AMD64_REL32	_tls_index
  19c1e1:      	movl	$0x0, %edx
		000000000019c1e2:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  19c1e6:      	addq	(%rcx,%rax,8), %rdx
  19c1ea:      	movq	(%rdx), %rbp
  19c1ed:      	testq	%rbp, %rbp
  19c1f0:      	je	0x19c523 <symbol>
  19c1f6:      	movq	0x18(%rbp), %rbp
  19c1fa:      	testq	%rbp, %rbp
  19c1fd:      	je	0x19c219 <symbol>
  19c1ff:      	cmpq	%rbx, 0x8(%rbp)
  19c203:      	je	0x19c223 <symbol>
  19c205:      	movq	0x18(%rbp), %rdx
  19c209:      	movq	%rbx, %rcx
  19c20c:      	callq	0x4560 <symbol>
  19c211:      	movq	%rax, %rbp
  19c214:      	testq	%rbp, %rbp
  19c217:      	jne	0x19c223 <symbol>
  19c219:      	movq	0x30(%rbx), %rcx
  19c21d:      	movq	0x8(%rcx), %rcx
  19c221:      	jmp	0x19c227 <symbol>
  19c223:      	movq	0x10(%rbp), %rcx
  19c227:      	movq	0x18(%rcx), %rcx
  19c22b:      	movq	0x10(%rcx), %rbx
  19c22f:      	movq	%rbx, %rcx
  19c232:      	movq	0x98(%rsp), %rdx
  19c23a:      	cmpl	%ecx, (%rcx)
  19c23c:      	callq	0x19e40 <symbol>
  19c241:      	testl	%eax, %eax
  19c243:      	je	0x19c413 <symbol>
  19c249:      	leaq	0x40(%rsp), %rdx
  19c24e:      	movq	%rbx, %rcx
  19c251:      	movq	0x98(%rsp), %r8
  19c259:      	callq	0x1a110 <symbol>
  19c25e:      	movq	0x40(%rsp), %rbx
  19c263:      	movq	0x48(%rsp), %rbp
  19c268:      	movl	0x54(%rsp), %r14d
  19c26d:      	testq	%rbx, %rbx
  19c270:      	je	0x19c413 <symbol>
  19c276:      	movq	0x18(%rsi), %rcx
  19c27a:      	movq	0x70(%rbx), %rdx
  19c27e:      	leaq	0x38(%rsp), %r8
  19c283:      	cmpl	%ecx, (%rcx)
  19c285:      	callq	0xfe290 <symbol>
  19c28a:      	testl	%eax, %eax
  19c28c:      	je	0x19c413 <symbol>
  19c292:      	movq	0x38(%rsp), %rdx
  19c297:      	cmpq	%rbx, 0x30(%rdx)
  19c29b:      	jne	0x19c413 <symbol>
  19c2a1:      	movq	0x38(%rsp), %rcx
  19c2a6:      	cmpb	%cl, (%rcx)
  19c2a8:      	movq	0x10(%rcx), %rdx
  19c2ac:      	xorl	%r8d, %r8d
  19c2af:      	jmp	0x19c316 <symbol>
  19c2b1:      	xorl	%eax, %eax
  19c2b3:      	cmpl	%eax, %r8d
  19c2b6:      	jge	0x19c323 <symbol>
  19c2b8:      	movq	0x8(%rbp), %rax
  19c2bc:      	movq	0x18(%rax), %rax
  19c2c0:      	testq	%rax, %rax
  19c2c3:      	je	0x19c2ce <symbol>
  19c2c5:      	leaq	0x10(%rax), %r10
  19c2c9:      	movl	0x8(%rax), %eax
  19c2cc:      	jmp	0x19c2d3 <symbol>
  19c2ce:      	xorl	%r10d, %r10d
  19c2d1:      	xorl	%eax, %eax
  19c2d3:      	movq	0x10(%rcx), %r9
  19c2d7:      	testq	%r9, %r9
  19c2da:      	je	0x19c2e6 <symbol>
  19c2dc:      	leaq	0x10(%r9), %r11
  19c2e0:      	movl	0x8(%r9), %r9d
  19c2e4:      	jmp	0x19c2ec <symbol>
  19c2e6:      	xorl	%r11d, %r11d
  19c2e9:      	xorl	%r9d, %r9d
  19c2ec:      	cmpl	%r9d, %r8d
  19c2ef:      	jae	0x19c58d <symbol>
  19c2f5:      	movl	(%r11,%r8,4), %r9d
  19c2f9:      	cmpl	%eax, %r9d
  19c2fc:      	jae	0x19c58d <symbol>
  19c302:      	movl	%r9d, %eax
  19c305:      	movl	(%r10,%rax,4), %eax
  19c309:      	subl	%edi, %eax
  19c30b:      	testl	%eax, %eax
  19c30d:      	jle	0x19c413 <symbol>
  19c313:      	incl	%r8d
  19c316:      	movq	%rdx, %rax
  19c319:      	testq	%rax, %rax
  19c31c:      	je	0x19c2b1 <symbol>
  19c31e:      	movl	0x8(%rax), %eax
  19c321:      	jmp	0x19c2b3 <symbol>
  19c323:      	movq	0x8(%rcx), %rdx
  19c327:      	xorl	%r8d, %r8d
  19c32a:      	jmp	0x19c391 <symbol>
  19c32c:      	xorl	%eax, %eax
  19c32e:      	cmpl	%eax, %r8d
  19c331:      	jge	0x19c39e <symbol>
  19c333:      	movq	0x8(%rbp), %rax
  19c337:      	movq	0x18(%rax), %rax
  19c33b:      	testq	%rax, %rax
  19c33e:      	je	0x19c349 <symbol>
  19c340:      	leaq	0x10(%rax), %r10
  19c344:      	movl	0x8(%rax), %eax
  19c347:      	jmp	0x19c34e <symbol>
  19c349:      	xorl	%r10d, %r10d
  19c34c:      	xorl	%eax, %eax
  19c34e:      	movq	0x8(%rcx), %r9
  19c352:      	testq	%r9, %r9
  19c355:      	je	0x19c361 <symbol>
  19c357:      	leaq	0x10(%r9), %r11
  19c35b:      	movl	0x8(%r9), %r9d
  19c35f:      	jmp	0x19c367 <symbol>
  19c361:      	xorl	%r11d, %r11d
  19c364:      	xorl	%r9d, %r9d
  19c367:      	cmpl	%r9d, %r8d
  19c36a:      	jae	0x19c58d <symbol>
  19c370:      	movl	(%r11,%r8,8), %r9d
  19c374:      	cmpl	%eax, %r9d
  19c377:      	jae	0x19c58d <symbol>
  19c37d:      	movl	%r9d, %eax
  19c380:      	movl	(%r10,%rax,4), %eax
  19c384:      	subl	%edi, %eax
  19c386:      	testl	%eax, %eax
  19c388:      	jle	0x19c413 <symbol>
  19c38e:      	incl	%r8d
  19c391:      	movq	%rdx, %rax
  19c394:      	testq	%rax, %rax
  19c397:      	je	0x19c32c <symbol>
  19c399:      	movl	0x8(%rax), %eax
  19c39c:      	jmp	0x19c32e <symbol>
  19c39e:      	movq	0x18(%rcx), %rdx
  19c3a2:      	testq	%rdx, %rdx
  19c3a5:      	je	0x19c3ac <symbol>
  19c3a7:      	movl	0x8(%rdx), %edx
  19c3aa:      	jmp	0x19c3ae <symbol>
  19c3ac:      	xorl	%edx, %edx
  19c3ae:      	testl	%edx, %edx
  19c3b0:      	jne	0x19c3c6 <symbol>
  19c3b2:      	movq	0x20(%rcx), %rdx
  19c3b6:      	testq	%rdx, %rdx
  19c3b9:      	je	0x19c3c0 <symbol>
  19c3bb:      	movl	0x8(%rdx), %edx
  19c3be:      	jmp	0x19c3c2 <symbol>
  19c3c0:      	xorl	%edx, %edx
  19c3c2:      	testl	%edx, %edx
  19c3c4:      	je	0x19c3f4 <symbol>
  19c3c6:      	leaq	0x28(%rsp), %rdx
  19c3cb:      	movq	%rbp, %r8
  19c3ce:      	callq	0xdca0 <symbol>
  19c3d3:      	cmpq	$0x0, 0x28(%rsp)
  19c3d9:      	je	0x19c3e2 <symbol>
  19c3db:      	movl	$0x1, %ecx
  19c3e0:      	jmp	0x19c3ee <symbol>
  19c3e2:      	cmpq	$0x0, 0x30(%rsp)
  19c3e8:      	setne	%cl
  19c3eb:      	movzbl	%cl, %ecx
  19c3ee:      	testl	%ecx, %ecx
  19c3f0:      	je	0x19c413 <symbol>
  19c3f2:      	jmp	0x19c3fa <symbol>
  19c3f4:      	cmpl	$0x0, 0x30(%rbp)
  19c3f8:      	jle	0x19c413 <symbol>
  19c3fa:      	movq	0x38(%rsp), %rcx
  19c3ff:      	movq	%rbp, %rdx
  19c402:      	movl	%r14d, %r8d
  19c405:      	movl	%edi, %r9d
  19c408:      	cmpl	%ecx, (%rcx)
  19c40a:      	callq	0xda40 <symbol>
  19c40f:      	testl	%eax, %eax
  19c411:      	jne	0x19c41f <symbol>
  19c413:      	xorl	%eax, %eax
  19c415:      	movq	%rax, 0x38(%rsp)
  19c41a:      	jmp	0x19c516 <symbol>
  19c41f:      	movq	0x38(%rsp), %rbx
  19c424:      	leaq	(%rip), %rsi
		000000000019c427:  IMAGE_REL_AMD64_REL32	?__NONGCSTATICS@SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>>@@
  19c42b:      	cmpq	$0x0, -0x8(%rsi)
  19c430:      	jne	0x19c537 <symbol>
  19c436:      	movl	(%rsi), %edx
  19c438:      	cmpb	%bl, (%rbx)
  19c43a:      	movq	0x28(%rbx), %rax
  19c43e:      	xorl	%ecx, %ecx
  19c440:      	jmp	0x19c444 <symbol>
  19c442:      	incl	%ecx
  19c444:      	movq	%rax, %r8
  19c447:      	testq	%r8, %r8
  19c44a:      	je	0x19c452 <symbol>
  19c44c:      	movl	0x8(%r8), %r8d
  19c450:      	jmp	0x19c455 <symbol>
  19c452:      	xorl	%r8d, %r8d
  19c455:      	cmpl	%r8d, %ecx
  19c458:      	jge	0x19c507 <symbol>
  19c45e:      	movq	%rax, %r8
  19c461:      	testq	%r8, %r8
  19c464:      	je	0x19c470 <symbol>
  19c466:      	leaq	0x10(%r8), %r10
  19c46a:      	movl	0x8(%r8), %r8d
  19c46e:      	jmp	0x19c476 <symbol>
  19c470:      	xorl	%r10d, %r10d
  19c473:      	xorl	%r8d, %r8d
  19c476:      	cmpl	%r8d, %ecx
  19c479:      	jae	0x19c58d <symbol>
  19c47f:      	leaq	(%rcx,%rcx,2), %r8
  19c483:      	cmpl	%edx, (%r10,%r8,4)
  19c487:      	jne	0x19c442 <symbol>
  19c489:      	testq	%rax, %rax
  19c48c:      	je	0x19c497 <symbol>
  19c48e:      	leaq	0x10(%rax), %rdx
  19c492:      	movl	0x8(%rax), %eax
  19c495:      	jmp	0x19c49b <symbol>
  19c497:      	xorl	%edx, %edx
  19c499:      	xorl	%eax, %eax
  19c49b:      	cmpl	%eax, %ecx
  19c49d:      	jae	0x19c58d <symbol>
  19c4a3:      	movl	%ecx, %eax
  19c4a5:      	leaq	(%rax,%rax,2), %rax
  19c4a9:      	leaq	(%rdx,%rax,4), %rax
  19c4ad:      	movl	0x4(%rax), %ecx
  19c4b0:      	movzbl	0x8(%rax), %eax
  19c4b4:      	cmpl	$0x1, %eax
  19c4b7:      	je	0x19c4c2 <symbol>
  19c4b9:      	cmpl	$0x3, %eax
  19c4bc:      	jne	0x19c541 <symbol>
  19c4c2:      	movq	0x8(%rbp), %rax
  19c4c6:      	movq	0x10(%rax), %rax
  19c4ca:      	cmpl	0x8(%rax), %ecx
  19c4cd:      	jae	0x19c58d <symbol>
  19c4d3:      	movl	%ecx, %ecx
  19c4d5:      	movq	0x10(%rax,%rcx,8), %rax
  19c4da:      	testq	%rax, %rax
  19c4dd:      	je	0x19c4e8 <symbol>
  19c4df:      	leaq	0x10(%rax), %rcx
  19c4e3:      	movl	0x8(%rax), %eax
  19c4e6:      	jmp	0x19c4ec <symbol>
  19c4e8:      	xorl	%ecx, %ecx
  19c4ea:      	xorl	%eax, %eax
  19c4ec:      	cmpl	%eax, %r14d
  19c4ef:      	jae	0x19c58d <symbol>
  19c4f5:      	movl	%r14d, %eax
  19c4f8:      	leaq	(%rcx,%rax,4), %rax
  19c4fc:      	addq	$0x60, %rsp
  19c500:      	popq	%rbx
  19c501:      	popq	%rbp
  19c502:      	popq	%rsi
  19c503:      	popq	%rdi
  19c504:      	popq	%r14
  19c506:      	retq
  19c507:      	movq	0x30(%rbx), %rcx
  19c50b:      	cmpl	%ecx, (%rcx)
  19c50d:      	callq	0x1c8b0 <symbol>
  19c512:      	testl	%eax, %eax
  19c514:      	jne	0x19c57f <symbol>
  19c516:      	xorl	%eax, %eax
  19c518:      	addq	$0x60, %rsp
  19c51c:      	popq	%rbx
  19c51d:      	popq	%rbp
  19c51e:      	popq	%rsi
  19c51f:      	popq	%rdi
  19c520:      	popq	%r14
  19c522:      	retq
  19c523:      	movq	%rdx, %rcx
  19c526:      	leaq	-0x1076ad(%rip), %rax
  19c52d:      	callq	*%rax
  19c52f:      	movq	%rax, %rbp
  19c532:      	jmp	0x19c1f6 <symbol>
  19c537:      	callq	0x19c53c <symbol>
		000000000019c538:  IMAGE_REL_AMD64_REL32	__GetNonGCStaticBase_SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>>
  19c53c:      	jmp	0x19c436 <symbol>
  19c541:      	leaq	(%rip), %rcx
		000000000019c544:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  19c548:      	callq	0x19c54d <symbol>
		000000000019c549:  IMAGE_REL_AMD64_REL32	RhpNewFast
  19c54d:      	movq	%rax, %rbx
  19c550:      	leaq	(%rip), %rcx
		000000000019c553:  IMAGE_REL_AMD64_REL32	__RuntimeType_SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>
  19c557:      	callq	0x40980 <symbol>
  19c55c:      	movq	%rax, %rcx
  19c55f:      	leaq	(%rip), %rdx
		000000000019c562:  IMAGE_REL_AMD64_REL32	__Str__was_not_declared_for_query_re_3DAC58C3D425A8E368C52F9A8DF72803BD0D416C884BD906DC32246D69AA8921
  19c566:      	callq	0x42160 <symbol>
  19c56b:      	movq	%rax, %rdx
  19c56e:      	movq	%rbx, %rcx
  19c571:      	callq	0x4cc40 <symbol>
  19c576:      	movq	%rbx, %rcx
  19c579:      	callq	0x19c57e <symbol>
		000000000019c57a:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  19c57e:      	int3
  19c57f:      	callq	0x19c650 <symbol>
  19c584:      	movq	%rax, %rcx
  19c587:      	callq	0x19c58c <symbol>
		000000000019c588:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  19c58c:      	int3
  19c58d:      	callq	0x97d70 <symbol>
  19c592:      	int3
  19c593:      	nop
  19c594:      	nop
  19c595:      	nop
  19c596:      	nop
  19c597:      	nop
  19c598:      	nop
  19c599:      	nop
  19c59a:      	nop
  19c59b:      	nop
  19c59c:      	nop
  19c59d:      	nop
  19c59e:      	nop
  19c59f:      	nop

