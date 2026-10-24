0000000000207e20 <symbol>
  207e20:      	pushq	%r15
  207e22:      	pushq	%r14
  207e24:      	pushq	%r13
  207e26:      	pushq	%r12
  207e28:      	pushq	%rdi
  207e29:      	pushq	%rsi
  207e2a:      	pushq	%rbp
  207e2b:      	pushq	%rbx
  207e2c:      	subq	$0xf8, %rsp
  207e33:      	xorl	%eax, %eax
  207e35:      	movq	%rax, 0x78(%rsp)
  207e3a:      	xorps	%xmm4, %xmm4
  207e3d:      	movaps	%xmm4, 0x80(%rsp)
  207e45:      	movabsq	$-0x60, %rax
  207e4f:      	movaps	%xmm4, 0xf0(%rsp,%rax)
  207e57:      	movaps	%xmm4, 0x100(%rsp,%rax)
  207e5f:      	movaps	%xmm4, 0x110(%rsp,%rax)
  207e67:      	addq	$0x30, %rax
  207e6b:      	jne	0x207e4f <symbol>
  207e6d:      	movq	%rax, 0xf0(%rsp)
  207e75:      	movq	%rcx, %rbp
  207e78:      	movl	%edx, %esi
  207e7a:      	movq	%r9, %rdi
  207e7d:      	movq	0x178(%rsp), %rbx
  207e85:      	leaq	(%rip), %rcx
		0000000000207e88:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_HashSet_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  207e8c:      	callq	0x207e91 <symbol>
		0000000000207e8d:  IMAGE_REL_AMD64_REL32	RhpNewFast
  207e91:      	movq	%rax, %r14
  207e94:      	leaq	(%rip), %rcx
		0000000000207e97:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  207e9b:      	callq	0x207ea0 <symbol>
		0000000000207e9c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  207ea0:      	movq	%rax, %r15
  207ea3:      	leaq	(%rip), %rcx
		0000000000207ea6:  IMAGE_REL_AMD64_REL32	__FrozenObj_S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>2
  207eaa:      	movq	%rcx, 0x8(%r15)
  207eae:      	leaq	0x160(%rsp), %rcx
  207eb6:      	movq	%r14, %rdx
  207eb9:      	movq	%r15, %r8
  207ebc:      	callq	0x1643e0 <symbol>
  207ec1:      	movq	%rbp, 0x48(%rsp)
  207ec6:      	movq	%r14, 0x40(%rsp)
  207ecb:      	movq	%r15, 0x38(%rsp)
  207ed0:      	movl	0x34(%r14), %ecx
  207ed4:      	movl	0x10(%r15), %eax
  207ed8:      	movq	0x8(%r15), %rdx
  207edc:      	cmpl	%eax, 0x8(%rdx)
  207edf:      	jb	0x20830a <symbol>
  207ee5:      	addq	$0x10, %rdx
  207ee9:      	movq	%rbp, 0xc8(%rsp)
  207ef1:      	movq	%r14, 0xd0(%rsp)
  207ef9:      	movl	%ecx, 0xd8(%rsp)
  207f00:      	xorl	%ecx, %ecx
  207f02:      	movl	%ecx, 0xdc(%rsp)
  207f09:      	movq	%rdx, 0xe8(%rsp)
  207f11:      	movl	%eax, 0xf0(%rsp)
  207f18:      	movq	%rcx, 0xe0(%rsp)
  207f20:      	jmp	0x207f27 <symbol>
  207f22:      	movq	0x48(%rsp), %rbp
  207f27:      	leaq	0xd0(%rsp), %rcx
  207f2f:      	callq	0x159f60 <symbol>
  207f34:      	testl	%eax, %eax
  207f36:      	jne	0x207f80 <symbol>
  207f38:      	cmpl	$0x0, 0xf0(%rsp)
  207f40:      	je	0x208245 <symbol>
  207f46:      	movq	0xe8(%rsp), %r13
  207f4e:      	movl	0xf0(%rsp), %r12d
  207f56:      	xorl	%eax, %eax
  207f58:      	xorl	%r10d, %r10d
  207f5b:      	xorl	%r9d, %r9d
  207f5e:      	xorps	%xmm0, %xmm0
  207f61:      	movups	%xmm0, 0xe8(%rsp)
  207f69:      	movl	%r12d, 0x60(%rsp)
  207f6e:      	movq	%r13, 0x30(%rsp)
  207f73:      	movl	%r9d, 0x5c(%rsp)
  207f78:      	movl	%r10d, %r12d
  207f7b:      	movl	%eax, %r13d
  207f7e:      	jmp	0x207fd4 <symbol>
  207f80:      	movl	0xe0(%rsp), %r13d
  207f88:      	movl	0xe4(%rsp), %r12d
  207f90:      	movl	%r12d, %r8d
  207f93:      	shlq	$0x20, %r8
  207f97:      	movl	%r13d, %edx
  207f9a:      	orq	%rdx, %r8
  207f9d:      	leaq	0x88(%rsp), %rdx
  207fa5:      	movq	0xc8(%rsp), %rcx
  207fad:      	cmpl	%ecx, (%rcx)
  207faf:      	callq	0x1d8dd0 <symbol>
  207fb4:      	movq	0x88(%rsp), %rcx
  207fbc:      	movl	0x90(%rsp), %eax
  207fc3:      	movq	%rcx, 0x30(%rsp)
  207fc8:      	movl	%eax, 0x60(%rsp)
  207fcc:      	movl	$0x1, 0x5c(%rsp)
  207fd4:      	movl	0x34(%r14), %ecx
  207fd8:      	movl	0x10(%r15), %r10d
  207fdc:      	movq	0x8(%r15), %r9
  207fe0:      	cmpl	%r10d, 0x8(%r9)
  207fe4:      	jb	0x20830a <symbol>
  207fea:      	addq	$0x10, %r9
  207fee:      	movq	%rbp, 0x98(%rsp)
  207ff6:      	movq	%r14, 0xa0(%rsp)
  207ffe:      	movl	%ecx, 0xa8(%rsp)
  208005:      	xorl	%ecx, %ecx
  208007:      	movl	%ecx, 0xac(%rsp)
  20800e:      	movq	%r9, 0xb8(%rsp)
  208016:      	movl	%r10d, 0xc0(%rsp)
  20801e:      	movq	%rcx, 0xb0(%rsp)
  208026:      	jmp	0x2081cf <symbol>
  20802b:      	movl	0xb0(%rsp), %r11d
  208033:      	movl	%r11d, %edx
  208036:      	movl	%edx, 0x70(%rsp)
  20803a:      	movl	0xb4(%rsp), %r11d
  208042:      	movl	%r11d, %r8d
  208045:      	movl	%r8d, 0x6c(%rsp)
  20804a:      	movl	%r8d, %ecx
  20804d:      	shlq	$0x20, %rcx
  208051:      	movl	%edx, %r11d
  208054:      	movq	%r11, %r8
  208057:      	orq	%rcx, %r8
  20805a:      	leaq	0x78(%rsp), %rdx
  20805f:      	movq	0x98(%rsp), %rcx
  208067:      	cmpl	%ecx, (%rcx)
  208069:      	callq	0x1d8dd0 <symbol>
  20806e:      	movq	0x78(%rsp), %r11
  208073:      	movl	0x80(%rsp), %ecx
  20807a:      	movl	0x70(%rsp), %edx
  20807e:      	movl	0x6c(%rsp), %r8d
  208083:      	movq	%r11, %rax
  208086:      	movl	%ecx, %r10d
  208089:      	movl	$0x1, %r9d
  20808f:      	movl	0x5c(%rsp), %r11d
  208094:      	testl	%r11d, %r11d
  208097:      	je	0x20809e <symbol>
  208099:      	testb	%r9b, %r9b
  20809c:      	jne	0x2080a3 <symbol>
  20809e:      	xorl	%r9d, %r9d
  2080a1:      	jmp	0x2080e4 <symbol>
  2080a3:      	movq	%rdi, 0x158(%rsp)
  2080ab:      	cmpb	%dil, (%rdi)
  2080ae:      	movl	%r13d, 0x68(%rsp)
  2080b3:      	cmpl	%edx, %r13d
  2080b6:      	je	0x2080c2 <symbol>
  2080b8:      	xorl	%r9d, %r9d
  2080bb:      	movl	%r12d, 0x64(%rsp)
  2080c0:      	jmp	0x2080d2 <symbol>
  2080c2:      	movl	%r12d, 0x64(%rsp)
  2080c7:      	cmpl	%r8d, %r12d
  2080ca:      	sete	%r9b
  2080ce:      	movzbl	%r9b, %r9d
  2080d2:      	movq	0x158(%rsp), %rdi
  2080da:      	movl	0x64(%rsp), %r12d
  2080df:      	movl	0x68(%rsp), %r13d
  2080e4:      	movzbl	%r9b, %r8d
  2080e8:      	movzbl	%sil, %r9d
  2080ec:      	cmpl	%r9d, %r8d
  2080ef:      	setne	%r8b
  2080f3:      	movzbl	%r8b, %r8d
  2080f7:      	testl	%r8d, %r8d
  2080fa:      	movl	%r10d, 0x58(%rsp)
  2080ff:      	movq	%rax, 0x28(%rsp)
  208104:      	je	0x2081cf <symbol>
  20810a:      	xorl	%ecx, %ecx
  20810c:      	movl	0x60(%rsp), %eax
  208110:      	cmpl	%eax, %ecx
  208112:      	jge	0x2081cf <symbol>
  208118:      	movq	%rdi, 0x158(%rsp)
  208120:      	movl	%r13d, 0x68(%rsp)
  208125:      	movl	%r12d, 0x64(%rsp)
  20812a:      	movq	0x30(%rsp), %rdx
  20812f:      	movq	%rcx, 0x50(%rsp)
  208134:      	leaq	(%rdx,%rcx,8), %r9
  208138:      	movl	(%r9), %ebp
  20813b:      	movl	0x4(%r9), %r9d
  20813f:      	movl	%r9d, 0x74(%rsp)
  208144:      	movq	0x160(%rsp), %r15
  20814c:      	movq	(%r15), %r15
  20814f:      	movq	0x160(%rsp), %r14
  208157:      	movq	0x8(%r14), %r14
  20815b:      	movq	0x160(%rsp), %rdi
  208163:      	movl	%r9d, %r12d
  208166:      	shlq	$0x20, %r12
  20816a:      	movl	%ebp, %r13d
  20816d:      	movq	%r13, %r8
  208170:      	orq	%r12, %r8
  208173:      	movl	0x10(%rdi), %r9d
  208177:      	movq	%r15, %rcx
  20817a:      	movq	%r14, %rdx
  20817d:      	cmpl	%ecx, (%rcx)
  20817f:      	callq	0x6a20 <symbol>
  208184:      	testl	%eax, %eax
  208186:      	jne	0x20822b <symbol>
  20818c:      	movl	0x58(%rsp), %r14d
  208191:      	movq	0x28(%rsp), %r15
  208196:      	movq	0x50(%rsp), %rdi
  20819b:      	incl	%edi
  20819d:      	movl	0x60(%rsp), %r12d
  2081a2:      	cmpl	%r12d, %edi
  2081a5:      	movl	%r14d, 0x58(%rsp)
  2081aa:      	movq	%r15, 0x28(%rsp)
  2081af:      	movl	%r12d, 0x60(%rsp)
  2081b4:      	movq	%rdi, %rcx
  2081b7:      	jl	0x20812a <symbol>
  2081bd:      	movq	0x158(%rsp), %rdi
  2081c5:      	movl	0x64(%rsp), %r12d
  2081ca:      	movl	0x68(%rsp), %r13d
  2081cf:      	leaq	0xa0(%rsp), %rcx
  2081d7:      	callq	0x159f60 <symbol>
  2081dc:      	testl	%eax, %eax
  2081de:      	jne	0x20802b <symbol>
  2081e4:      	cmpl	$0x0, 0xc0(%rsp)
  2081ec:      	movq	0x40(%rsp), %r14
  2081f1:      	movq	0x38(%rsp), %r15
  2081f6:      	je	0x207f22 <symbol>
  2081fc:      	movq	0xb8(%rsp), %r11
  208204:      	movl	0xc0(%rsp), %ecx
  20820b:      	xorl	%edx, %edx
  20820d:      	xorl	%r8d, %r8d
  208210:      	xorl	%ebp, %ebp
  208212:      	xorps	%xmm0, %xmm0
  208215:      	movups	%xmm0, 0xb8(%rsp)
  20821d:      	movq	%r11, %rax
  208220:      	movl	%ebp, %r9d
  208223:      	movl	%ecx, %r10d
  208226:      	jmp	0x20808f <symbol>
  20822b:      	xorl	%edi, %edi
  20822d:      	movl	0x58(%rsp), %r14d
  208232:      	cmpl	%r14d, %edi
  208235:      	jl	0x2082e2 <symbol>
  20823b:      	movq	0x28(%rsp), %r15
  208240:      	jmp	0x208196 <symbol>
  208245:      	movl	$0x1, %eax
  20824a:      	addq	$0xf8, %rsp
  208251:      	popq	%rbx
  208252:      	popq	%rbp
  208253:      	popq	%rsi
  208254:      	popq	%rdi
  208255:      	popq	%r12
  208257:      	popq	%r13
  208259:      	popq	%r14
  20825b:      	popq	%r15
  20825d:      	retq
  20825e:      	cmpl	%r12d, 0x74(%rsp)
  208263:      	jl	0x20826b <symbol>
  208265:      	movl	0x74(%rsp), %ecx
  208269:      	jmp	0x2082d2 <symbol>
  20826b:      	movq	0x160(%rsp), %r8
  208273:      	movq	(%r8), %rcx
  208276:      	movq	0x160(%rsp), %r8
  20827e:      	movq	0x8(%r8), %rdx
  208282:      	movq	0x160(%rsp), %r9
  20828a:      	movl	%r12d, %r8d
  20828d:      	shlq	$0x20, %r8
  208291:      	movl	%r13d, %r10d
  208294:      	orq	%r10, %r8
  208297:      	movl	0x10(%r9), %r9d
  20829b:      	cmpl	%ecx, (%rcx)
  20829d:      	callq	0x6a20 <symbol>
  2082a2:      	testl	%eax, %eax
  2082a4:      	jne	0x2082ac <symbol>
  2082a6:      	movl	0x74(%rsp), %ecx
  2082aa:      	jmp	0x2082d2 <symbol>
  2082ac:      	movl	0x10(%rbx), %eax
  2082af:      	leal	0x1(%rax), %ecx
  2082b2:      	movl	%ecx, 0x10(%rbx)
  2082b5:      	cmpl	0x8(%rbx), %eax
  2082b8:      	jae	0x208310 <symbol>
  2082ba:      	shlq	$0x4, %rax
  2082be:      	addq	(%rbx), %rax
  2082c1:      	movl	%ebp, (%rax)
  2082c3:      	movl	0x74(%rsp), %ecx
  2082c7:      	movl	%ecx, 0x4(%rax)
  2082ca:      	movl	%r13d, 0x8(%rax)
  2082ce:      	movl	%r12d, 0xc(%rax)
  2082d2:      	incl	%edi
  2082d4:      	cmpl	%r14d, %edi
  2082d7:      	jge	0x208196 <symbol>
  2082dd:      	movq	%r15, 0x28(%rsp)
  2082e2:      	movq	0x28(%rsp), %r15
  2082e7:      	leaq	(%r15,%rdi,8), %r8
  2082eb:      	movl	(%r8), %r13d
  2082ee:      	movl	0x4(%r8), %r12d
  2082f2:      	cmpl	%r13d, %ebp
  2082f5:      	jl	0x20826b <symbol>
  2082fb:      	cmpl	%r13d, %ebp
  2082fe:      	je	0x20825e <symbol>
  208304:      	movl	0x74(%rsp), %ecx
  208308:      	jmp	0x2082d2 <symbol>
  20830a:      	callq	0xa5940 <symbol>
  20830f:      	int3
  208310:      	callq	0x10c710 <symbol>
  208315:      	int3
  208316:      	nop
  208317:      	nop
  208318:      	nop
  208319:      	nop
  20831a:      	nop
  20831b:      	nop
  20831c:      	nop
  20831d:      	nop
  20831e:      	nop
  20831f:      	nop

