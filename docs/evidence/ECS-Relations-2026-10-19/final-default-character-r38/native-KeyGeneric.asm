000000000020a900 <symbol>
  20a900:      	pushq	%r15
  20a902:      	pushq	%r14
  20a904:      	pushq	%r13
  20a906:      	pushq	%r12
  20a908:      	pushq	%rdi
  20a909:      	pushq	%rsi
  20a90a:      	pushq	%rbp
  20a90b:      	pushq	%rbx
  20a90c:      	subq	$0xf8, %rsp
  20a913:      	xorl	%eax, %eax
  20a915:      	movq	%rax, 0x78(%rsp)
  20a91a:      	xorps	%xmm4, %xmm4
  20a91d:      	movaps	%xmm4, 0x80(%rsp)
  20a925:      	movabsq	$-0x60, %rax
  20a92f:      	movaps	%xmm4, 0xf0(%rsp,%rax)
  20a937:      	movaps	%xmm4, 0x100(%rsp,%rax)
  20a93f:      	movaps	%xmm4, 0x110(%rsp,%rax)
  20a947:      	addq	$0x30, %rax
  20a94b:      	jne	0x20a92f <symbol>
  20a94d:      	movq	%rax, 0xf0(%rsp)
  20a955:      	movq	%rcx, %rbp
  20a958:      	movl	%edx, %esi
  20a95a:      	movq	%r9, %rdi
  20a95d:      	movq	0x178(%rsp), %rbx
  20a965:      	leaq	(%rip), %rcx
		000000000020a968:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_HashSet_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  20a96c:      	callq	0x20a971 <symbol>
		000000000020a96d:  IMAGE_REL_AMD64_REL32	RhpNewFast
  20a971:      	movq	%rax, %r14
  20a974:      	leaq	(%rip), %rcx
		000000000020a977:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>@@6B@
  20a97b:      	callq	0x20a980 <symbol>
		000000000020a97c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  20a980:      	movq	%rax, %r15
  20a983:      	leaq	(%rip), %rcx
		000000000020a986:  IMAGE_REL_AMD64_REL32	__FrozenObj_S_P_CoreLib_System_Collections_Generic_List_1<SomeEngine_ECS_SomeEngine_ECS_Entities_Entity>2
  20a98a:      	movq	%rcx, 0x8(%r15)
  20a98e:      	leaq	0x160(%rsp), %rcx
  20a996:      	movq	%r14, %rdx
  20a999:      	movq	%r15, %r8
  20a99c:      	callq	0x164460 <symbol>
  20a9a1:      	movq	%rbp, 0x48(%rsp)
  20a9a6:      	movq	%r14, 0x40(%rsp)
  20a9ab:      	movq	%r15, 0x38(%rsp)
  20a9b0:      	movl	0x34(%r14), %ecx
  20a9b4:      	movl	0x10(%r15), %eax
  20a9b8:      	movq	0x8(%r15), %rdx
  20a9bc:      	cmpl	%eax, 0x8(%rdx)
  20a9bf:      	jb	0x20aded <symbol>
  20a9c5:      	addq	$0x10, %rdx
  20a9c9:      	movq	%rbp, 0xc8(%rsp)
  20a9d1:      	movq	%r14, 0xd0(%rsp)
  20a9d9:      	movl	%ecx, 0xd8(%rsp)
  20a9e0:      	xorl	%ecx, %ecx
  20a9e2:      	movl	%ecx, 0xdc(%rsp)
  20a9e9:      	movq	%rdx, 0xe8(%rsp)
  20a9f1:      	movl	%eax, 0xf0(%rsp)
  20a9f8:      	movq	%rcx, 0xe0(%rsp)
  20aa00:      	jmp	0x20aa07 <symbol>
  20aa02:      	movq	0x48(%rsp), %rbp
  20aa07:      	leaq	0xd0(%rsp), %rcx
  20aa0f:      	callq	0x159fe0 <symbol>
  20aa14:      	testl	%eax, %eax
  20aa16:      	jne	0x20aa60 <symbol>
  20aa18:      	cmpl	$0x0, 0xf0(%rsp)
  20aa20:      	je	0x20ad25 <symbol>
  20aa26:      	movq	0xe8(%rsp), %r13
  20aa2e:      	movl	0xf0(%rsp), %r12d
  20aa36:      	xorl	%eax, %eax
  20aa38:      	xorl	%r10d, %r10d
  20aa3b:      	xorl	%r9d, %r9d
  20aa3e:      	xorps	%xmm0, %xmm0
  20aa41:      	movups	%xmm0, 0xe8(%rsp)
  20aa49:      	movl	%r12d, 0x60(%rsp)
  20aa4e:      	movq	%r13, 0x30(%rsp)
  20aa53:      	movl	%r9d, 0x5c(%rsp)
  20aa58:      	movl	%r10d, %r12d
  20aa5b:      	movl	%eax, %r13d
  20aa5e:      	jmp	0x20aab4 <symbol>
  20aa60:      	movl	0xe0(%rsp), %r13d
  20aa68:      	movl	0xe4(%rsp), %r12d
  20aa70:      	movl	%r12d, %r8d
  20aa73:      	shlq	$0x20, %r8
  20aa77:      	movl	%r13d, %edx
  20aa7a:      	orq	%rdx, %r8
  20aa7d:      	leaq	0x88(%rsp), %rdx
  20aa85:      	movq	0xc8(%rsp), %rcx
  20aa8d:      	cmpl	%ecx, (%rcx)
  20aa8f:      	callq	0x1dabb0 <symbol>
  20aa94:      	movq	0x88(%rsp), %rcx
  20aa9c:      	movl	0x90(%rsp), %eax
  20aaa3:      	movq	%rcx, 0x30(%rsp)
  20aaa8:      	movl	%eax, 0x60(%rsp)
  20aaac:      	movl	$0x1, 0x5c(%rsp)
  20aab4:      	movl	0x34(%r14), %ecx
  20aab8:      	movl	0x10(%r15), %r10d
  20aabc:      	movq	0x8(%r15), %r9
  20aac0:      	cmpl	%r10d, 0x8(%r9)
  20aac4:      	jb	0x20aded <symbol>
  20aaca:      	addq	$0x10, %r9
  20aace:      	movq	%rbp, 0x98(%rsp)
  20aad6:      	movq	%r14, 0xa0(%rsp)
  20aade:      	movl	%ecx, 0xa8(%rsp)
  20aae5:      	xorl	%ecx, %ecx
  20aae7:      	movl	%ecx, 0xac(%rsp)
  20aaee:      	movq	%r9, 0xb8(%rsp)
  20aaf6:      	movl	%r10d, 0xc0(%rsp)
  20aafe:      	movq	%rcx, 0xb0(%rsp)
  20ab06:      	jmp	0x20acaf <symbol>
  20ab0b:      	movl	0xb0(%rsp), %r11d
  20ab13:      	movl	%r11d, %edx
  20ab16:      	movl	%edx, 0x70(%rsp)
  20ab1a:      	movl	0xb4(%rsp), %r11d
  20ab22:      	movl	%r11d, %r8d
  20ab25:      	movl	%r8d, 0x6c(%rsp)
  20ab2a:      	movl	%r8d, %ecx
  20ab2d:      	shlq	$0x20, %rcx
  20ab31:      	movl	%edx, %r11d
  20ab34:      	movq	%r11, %r8
  20ab37:      	orq	%rcx, %r8
  20ab3a:      	leaq	0x78(%rsp), %rdx
  20ab3f:      	movq	0x98(%rsp), %rcx
  20ab47:      	cmpl	%ecx, (%rcx)
  20ab49:      	callq	0x1dabb0 <symbol>
  20ab4e:      	movq	0x78(%rsp), %r11
  20ab53:      	movl	0x80(%rsp), %ecx
  20ab5a:      	movl	0x70(%rsp), %edx
  20ab5e:      	movl	0x6c(%rsp), %r8d
  20ab63:      	movq	%r11, %rax
  20ab66:      	movl	%ecx, %r10d
  20ab69:      	movl	$0x1, %r9d
  20ab6f:      	movl	0x5c(%rsp), %r11d
  20ab74:      	testl	%r11d, %r11d
  20ab77:      	je	0x20ab7e <symbol>
  20ab79:      	testb	%r9b, %r9b
  20ab7c:      	jne	0x20ab83 <symbol>
  20ab7e:      	xorl	%r9d, %r9d
  20ab81:      	jmp	0x20abc4 <symbol>
  20ab83:      	movq	%rdi, 0x158(%rsp)
  20ab8b:      	cmpb	%dil, (%rdi)
  20ab8e:      	movl	%r13d, 0x68(%rsp)
  20ab93:      	cmpl	%edx, %r13d
  20ab96:      	je	0x20aba2 <symbol>
  20ab98:      	xorl	%r9d, %r9d
  20ab9b:      	movl	%r12d, 0x64(%rsp)
  20aba0:      	jmp	0x20abb2 <symbol>
  20aba2:      	movl	%r12d, 0x64(%rsp)
  20aba7:      	cmpl	%r8d, %r12d
  20abaa:      	sete	%r9b
  20abae:      	movzbl	%r9b, %r9d
  20abb2:      	movq	0x158(%rsp), %rdi
  20abba:      	movl	0x64(%rsp), %r12d
  20abbf:      	movl	0x68(%rsp), %r13d
  20abc4:      	movzbl	%r9b, %r8d
  20abc8:      	movzbl	%sil, %r9d
  20abcc:      	cmpl	%r9d, %r8d
  20abcf:      	setne	%r8b
  20abd3:      	movzbl	%r8b, %r8d
  20abd7:      	testl	%r8d, %r8d
  20abda:      	movl	%r10d, 0x58(%rsp)
  20abdf:      	movq	%rax, 0x28(%rsp)
  20abe4:      	je	0x20acaf <symbol>
  20abea:      	xorl	%ecx, %ecx
  20abec:      	movl	0x60(%rsp), %eax
  20abf0:      	cmpl	%eax, %ecx
  20abf2:      	jge	0x20acaf <symbol>
  20abf8:      	movq	%rdi, 0x158(%rsp)
  20ac00:      	movl	%r13d, 0x68(%rsp)
  20ac05:      	movl	%r12d, 0x64(%rsp)
  20ac0a:      	movq	0x30(%rsp), %rdx
  20ac0f:      	movq	%rcx, 0x50(%rsp)
  20ac14:      	leaq	(%rdx,%rcx,8), %r9
  20ac18:      	movl	(%r9), %ebp
  20ac1b:      	movl	0x4(%r9), %r9d
  20ac1f:      	movl	%r9d, 0x74(%rsp)
  20ac24:      	movq	0x160(%rsp), %r15
  20ac2c:      	movq	(%r15), %r15
  20ac2f:      	movq	0x160(%rsp), %r14
  20ac37:      	movq	0x8(%r14), %r14
  20ac3b:      	movq	0x160(%rsp), %rdi
  20ac43:      	movl	%r9d, %r12d
  20ac46:      	shlq	$0x20, %r12
  20ac4a:      	movl	%ebp, %r13d
  20ac4d:      	movq	%r13, %r8
  20ac50:      	orq	%r12, %r8
  20ac53:      	movl	0x10(%rdi), %r9d
  20ac57:      	movq	%r15, %rcx
  20ac5a:      	movq	%r14, %rdx
  20ac5d:      	cmpl	%ecx, (%rcx)
  20ac5f:      	callq	0x6a60 <symbol>
  20ac64:      	testl	%eax, %eax
  20ac66:      	jne	0x20ad0b <symbol>
  20ac6c:      	movl	0x58(%rsp), %r14d
  20ac71:      	movq	0x28(%rsp), %r15
  20ac76:      	movq	0x50(%rsp), %rdi
  20ac7b:      	incl	%edi
  20ac7d:      	movl	0x60(%rsp), %r12d
  20ac82:      	cmpl	%r12d, %edi
  20ac85:      	movl	%r14d, 0x58(%rsp)
  20ac8a:      	movq	%r15, 0x28(%rsp)
  20ac8f:      	movl	%r12d, 0x60(%rsp)
  20ac94:      	movq	%rdi, %rcx
  20ac97:      	jl	0x20ac0a <symbol>
  20ac9d:      	movq	0x158(%rsp), %rdi
  20aca5:      	movl	0x64(%rsp), %r12d
  20acaa:      	movl	0x68(%rsp), %r13d
  20acaf:      	leaq	0xa0(%rsp), %rcx
  20acb7:      	callq	0x159fe0 <symbol>
  20acbc:      	testl	%eax, %eax
  20acbe:      	jne	0x20ab0b <symbol>
  20acc4:      	cmpl	$0x0, 0xc0(%rsp)
  20accc:      	movq	0x40(%rsp), %r14
  20acd1:      	movq	0x38(%rsp), %r15
  20acd6:      	je	0x20aa02 <symbol>
  20acdc:      	movq	0xb8(%rsp), %r11
  20ace4:      	movl	0xc0(%rsp), %ecx
  20aceb:      	xorl	%edx, %edx
  20aced:      	xorl	%r8d, %r8d
  20acf0:      	xorl	%ebp, %ebp
  20acf2:      	xorps	%xmm0, %xmm0
  20acf5:      	movups	%xmm0, 0xb8(%rsp)
  20acfd:      	movq	%r11, %rax
  20ad00:      	movl	%ebp, %r9d
  20ad03:      	movl	%ecx, %r10d
  20ad06:      	jmp	0x20ab6f <symbol>
  20ad0b:      	xorl	%edi, %edi
  20ad0d:      	movl	0x58(%rsp), %r14d
  20ad12:      	cmpl	%r14d, %edi
  20ad15:      	jl	0x20adc5 <symbol>
  20ad1b:      	movq	0x28(%rsp), %r15
  20ad20:      	jmp	0x20ac76 <symbol>
  20ad25:      	movl	$0x1, %eax
  20ad2a:      	addq	$0xf8, %rsp
  20ad31:      	popq	%rbx
  20ad32:      	popq	%rbp
  20ad33:      	popq	%rsi
  20ad34:      	popq	%rdi
  20ad35:      	popq	%r12
  20ad37:      	popq	%r13
  20ad39:      	popq	%r14
  20ad3b:      	popq	%r15
  20ad3d:      	retq
  20ad3e:      	cmpl	%r12d, 0x74(%rsp)
  20ad43:      	jl	0x20ad4b <symbol>
  20ad45:      	movl	0x74(%rsp), %eax
  20ad49:      	jmp	0x20adb5 <symbol>
  20ad4b:      	movq	0x160(%rsp), %r8
  20ad53:      	movq	(%r8), %rcx
  20ad56:      	movq	0x160(%rsp), %r8
  20ad5e:      	movq	0x8(%r8), %rdx
  20ad62:      	movq	0x160(%rsp), %r9
  20ad6a:      	movl	%r12d, %r8d
  20ad6d:      	shlq	$0x20, %r8
  20ad71:      	movl	%r13d, %r10d
  20ad74:      	orq	%r10, %r8
  20ad77:      	movl	0x10(%r9), %r9d
  20ad7b:      	cmpl	%ecx, (%rcx)
  20ad7d:      	callq	0x6a60 <symbol>
  20ad82:      	testl	%eax, %eax
  20ad84:      	jne	0x20ad8c <symbol>
  20ad86:      	movl	0x74(%rsp), %eax
  20ad8a:      	jmp	0x20adb5 <symbol>
  20ad8c:      	movq	(%rbx), %rax
  20ad8f:      	movl	0x10(%rax), %ecx
  20ad92:      	leal	0x1(%rcx), %edx
  20ad95:      	movl	%edx, 0x10(%rax)
  20ad98:      	cmpl	0x8(%rax), %ecx
  20ad9b:      	jae	0x20adf3 <symbol>
  20ad9d:      	shlq	$0x4, %rcx
  20ada1:      	addq	(%rax), %rcx
  20ada4:      	movl	%ebp, (%rcx)
  20ada6:      	movl	0x74(%rsp), %eax
  20adaa:      	movl	%eax, 0x4(%rcx)
  20adad:      	movl	%r13d, 0x8(%rcx)
  20adb1:      	movl	%r12d, 0xc(%rcx)
  20adb5:      	incl	%edi
  20adb7:      	cmpl	%r14d, %edi
  20adba:      	jge	0x20ac76 <symbol>
  20adc0:      	movq	%r15, 0x28(%rsp)
  20adc5:      	movq	0x28(%rsp), %r15
  20adca:      	leaq	(%r15,%rdi,8), %r8
  20adce:      	movl	(%r8), %r13d
  20add1:      	movl	0x4(%r8), %r12d
  20add5:      	cmpl	%r13d, %ebp
  20add8:      	jl	0x20ad4b <symbol>
  20adde:      	cmpl	%r13d, %ebp
  20ade1:      	je	0x20ad3e <symbol>
  20ade7:      	movl	0x74(%rsp), %eax
  20adeb:      	jmp	0x20adb5 <symbol>
  20aded:      	callq	0xa59c0 <symbol>
  20adf2:      	int3
  20adf3:      	callq	0x10c790 <symbol>
  20adf8:      	int3
  20adf9:      	nop
  20adfa:      	nop
  20adfb:      	nop
  20adfc:      	nop
  20adfd:      	nop
  20adfe:      	nop
  20adff:      	nop
