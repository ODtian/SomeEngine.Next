0000000000221cc0 <symbol>
  221cc0:      	pushq	%rbp
  221cc1:      	pushq	%r15
  221cc3:      	pushq	%r14
  221cc5:      	pushq	%r13
  221cc7:      	pushq	%rdi
  221cc8:      	pushq	%rsi
  221cc9:      	pushq	%rbx
  221cca:      	subq	$0xa0, %rsp
  221cd1:      	leaq	0xd0(%rsp), %rbp
  221cd9:      	xorps	%xmm4, %xmm4
  221cdc:      	movabsq	$-0x60, %rax
  221ce6:      	movaps	%xmm4, -0x30(%rbp,%rax)
  221ceb:      	movaps	%xmm4, -0x20(%rbp,%rax)
  221cf0:      	movaps	%xmm4, -0x10(%rbp,%rax)
  221cf5:      	addq	$0x30, %rax
  221cf9:      	jne	0x221ce6 <symbol>
  221cfb:      	movq	%rcx, 0x10(%rbp)
  221cff:      	movq	%rdx, 0x18(%rbp)
  221d03:      	movl	%r8d, %ebx
  221d06:      	movl	%r9d, %esi
  221d09:      	movl	0x30(%rbp), %eax
  221d0c:      	movq	0x38(%rbp), %rdi
  221d10:      	testb	%al, %al
  221d12:      	jne	0x221d35 <symbol>
  221d14:      	movq	0x38(%rcx), %rdx
  221d18:      	cmpl	$0x0, 0x10(%rdx)
  221d1c:      	jne	0x221f7b <symbol>
  221d22:      	leaq	0x14(%rdx), %r8
  221d26:      	lock
  221d27:      	incl	(%r8)
  221d2a:      	cmpl	$0x0, 0x10(%rdx)
  221d2e:      	je	0x221d97 <symbol>
  221d30:      	jmp	0x221fa2 <symbol>
  221d35:      	movq	0x38(%rcx), %r14
  221d39:      	cmpb	%r14b, (%r14)
  221d3c:      	callq	0xc7a80 <symbol>
  221d41:      	movl	%eax, %ecx
  221d43:      	movl	0x10(%r14), %edx
  221d47:      	testl	%edx, %edx
  221d49:      	jne	0x221d5b <symbol>
  221d4b:      	leaq	0x10(%r14), %rdx
  221d4f:      	xorl	%eax, %eax
  221d51:      	lock
  221d52:      	cmpxchgl	%ecx, (%rdx)
  221d55:      	movl	%eax, %edx
  221d57:      	testl	%edx, %edx
  221d59:      	je	0x221d63 <symbol>
  221d5b:      	cmpl	%ecx, %edx
  221d5d:      	jne	0x221f27 <symbol>
  221d63:      	cmpl	$0x0, 0x8(%r14)
  221d68:      	jne	0x221d80 <symbol>
  221d6a:      	cmpl	$0x0, 0x14(%r14)
  221d6f:      	jne	0x221f4e <symbol>
  221d75:      	cmpl	$0x0, 0x18(%r14)
  221d7a:      	jne	0x221f4e <symbol>
  221d80:      	movl	0x8(%r14), %ecx
  221d84:      	testl	%ecx, %ecx
  221d86:      	jne	0x221d8d <symbol>
  221d88:      	movb	$0x0, 0x1c(%r14)
  221d8d:      	incl	%ecx
  221d8f:      	movl	%ecx, 0x8(%r14)
  221d93:      	incl	0xc(%r14)
  221d97:      	movq	%gs:0x58, %rcx
  221da0:      	movl	(%rip), %eax
		0000000000221da2:  IMAGE_REL_AMD64_REL32	_tls_index
  221da6:      	movl	$0x0, %edx
		0000000000221da7:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  221dab:      	addq	(%rcx,%rax,8), %rdx
  221daf:      	movq	(%rdx), %r14
  221db2:      	testq	%r14, %r14
  221db5:      	je	0x221ec8 <symbol>
  221dbb:      	movq	0x18(%r14), %r14
  221dbf:      	testq	%r14, %r14
  221dc2:      	je	0x221ddf <symbol>
  221dc4:      	movq	0x10(%rbp), %rcx
  221dc8:      	cmpq	%rcx, 0x8(%r14)
  221dcc:      	je	0x221ded <symbol>
  221dce:      	movq	0x18(%r14), %rdx
  221dd2:      	callq	0x63c0 <symbol>
  221dd7:      	movq	%rax, %r14
  221dda:      	testq	%r14, %r14
  221ddd:      	jne	0x221ded <symbol>
  221ddf:      	movq	0x10(%rbp), %rcx
  221de3:      	movq	0x30(%rcx), %rdx
  221de7:      	movq	0x8(%rdx), %rdx
  221deb:      	jmp	0x221df5 <symbol>
  221ded:      	movq	0x10(%rbp), %rcx
  221df1:      	movq	0x10(%r14), %rdx
  221df5:      	movq	0x40(%rdx), %rcx
  221df9:      	movq	0x18(%rbp), %rdx
  221dfd:      	cmpl	%ecx, (%rcx)
  221dff:      	callq	0xce60 <symbol>
  221e04:      	movq	0x10(%rax), %rcx
  221e08:      	movq	0x10(%rbp), %rax
  221e0c:      	movq	%rax, -0x48(%rbp)
  221e10:      	movq	%rcx, -0x40(%rbp)
  221e14:      	movl	%ebx, -0x38(%rbp)
  221e17:      	movl	%esi, -0x34(%rbp)
  221e1a:      	cmpb	%dil, (%rdi)
  221e1d:      	leaq	0x8(%rdi), %rbx
  221e21:      	leaq	-0x90(%rbp), %rcx
  221e28:      	leaq	-0x48(%rbp), %rdx
  221e2c:      	callq	0x221f0 <symbol>
  221e31:      	movq	-0x90(%rbp), %rsi
  221e38:      	leaq	0x28(%rdi), %rcx
  221e3c:      	movq	(%rcx), %r14
  221e3f:      	movq	(%rbx), %rbx
  221e42:      	movl	0x8(%rbx), %r15d
  221e46:      	testl	%r15d, %r15d
  221e49:      	jle	0x221ec3 <symbol>
  221e4b:      	addq	$0x10, %rbx
  221e4f:      	movups	(%rbx), %xmm0
  221e52:      	movups	%xmm0, -0xa0(%rbp)
  221e59:      	movq	%rsi, %rcx
  221e5c:      	movq	-0xa0(%rbp), %rdx
  221e63:      	callq	0x1d95c0 <symbol>
  221e68:      	movq	%rax, %r13
  221e6b:      	testq	%r13, %r13
  221e6e:      	je	0x221eba <symbol>
  221e70:      	movq	%rsi, %rcx
  221e73:      	movq	-0x98(%rbp), %rdx
  221e7a:      	callq	0x1d95c0 <symbol>
  221e7f:      	testq	%rax, %rax
  221e82:      	je	0x221eba <symbol>
  221e84:      	movq	0x8(%r13), %rcx
  221e88:      	testq	%rcx, (%rax)
  221e8b:      	je	0x221eba <symbol>
  221e8d:      	movq	0x8(%rax), %rcx
  221e91:      	testq	%rcx, (%r13)
  221e95:      	je	0x221eba <symbol>
  221e97:      	movl	0x10(%r14), %ecx
  221e9b:      	leal	0x1(%rcx), %edx
  221e9e:      	movl	%edx, 0x10(%r14)
  221ea2:      	cmpl	0x8(%r14), %ecx
  221ea6:      	jae	0x221edc <symbol>
  221ea8:      	movq	(%r14), %rdx
  221eab:      	shlq	$0x4, %rcx
  221eaf:      	movups	-0xa0(%rbp), %xmm0
  221eb6:      	movups	%xmm0, (%rdx,%rcx)
  221eba:      	addq	$0x10, %rbx
  221ebe:      	decl	%r15d
  221ec1:      	jne	0x221e4f <symbol>
  221ec3:      	movb	$0x1, (%rdi)
  221ec6:      	jmp	0x221ee2 <symbol>
  221ec8:      	movq	%rdx, %rcx
  221ecb:      	leaq	-0x118c22(%rip), %rax
  221ed2:      	callq	*%rax
  221ed4:      	movq	%rax, %r14
  221ed7:      	jmp	0x221dbb <symbol>
  221edc:      	callq	0x10c710 <symbol>
  221ee1:      	int3
  221ee2:      	cmpb	$0x0, 0x30(%rbp)
  221ee6:      	jne	0x221f06 <symbol>
  221ee8:      	movq	0x10(%rbp), %rcx
  221eec:      	movq	0x38(%rcx), %rcx
  221ef0:      	leaq	0x14(%rcx), %rax
  221ef4:      	movl	$0xffffffff, %edx
  221ef9:      	lock
  221efa:      	xaddl	%edx, (%rax)
  221efd:      	decl	%edx
  221eff:      	jns	0x221f14 <symbol>
  221f01:      	jmp	0x221fd2 <symbol>
  221f06:      	movq	0x10(%rbp), %rcx
  221f0a:      	movl	$0x1, %edx
  221f0f:      	callq	0x72f0 <symbol>
  221f14:      	nop
  221f15:      	addq	$0xa0, %rsp
  221f1c:      	popq	%rbx
  221f1d:      	popq	%rsi
  221f1e:      	popq	%rdi
  221f1f:      	popq	%r13
  221f21:      	popq	%r14
  221f23:      	popq	%r15
  221f25:      	popq	%rbp
  221f26:      	retq
  221f27:      	leaq	(%rip), %rcx
		0000000000221f2a:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f2e:      	callq	0x221f33 <symbol>
		0000000000221f2f:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f33:      	movq	%rax, %rbx
  221f36:      	movq	%rbx, %rcx
  221f39:      	leaq	(%rip), %rdx
		0000000000221f3c:  IMAGE_REL_AMD64_REL32	__Str_World_query_ownership_is_alrea_2098F6CADE8577F47E8B9879DFDF43509DE50017E92D28F8CBE571B7FA98407E
  221f40:      	callq	0x9d1b0 <symbol>
  221f45:      	movq	%rbx, %rcx
  221f48:      	callq	0x221f4d <symbol>
		0000000000221f49:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221f4d:      	int3
  221f4e:      	xorl	%ecx, %ecx
  221f50:      	movl	%ecx, 0x10(%r14)
  221f54:      	leaq	(%rip), %rcx
		0000000000221f57:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f5b:      	callq	0x221f60 <symbol>
		0000000000221f5c:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f60:      	movq	%rax, %rbx
  221f63:      	movq	%rbx, %rcx
  221f66:      	leaq	(%rip), %rdx
		0000000000221f69:  IMAGE_REL_AMD64_REL32	__Str_World_storage_is_already_borro_C8DF9F04500DFBBCC18112166D11CF9A1EE405431D203F1D7FE5E5BECB1E6683
  221f6d:      	callq	0x9d1b0 <symbol>
  221f72:      	movq	%rbx, %rcx
  221f75:      	callq	0x221f7a <symbol>
		0000000000221f76:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221f7a:      	int3
  221f7b:      	leaq	(%rip), %rcx
		0000000000221f7e:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221f82:      	callq	0x221f87 <symbol>
		0000000000221f83:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221f87:      	movq	%rax, %rsi
  221f8a:      	movq	%rsi, %rcx
  221f8d:      	leaq	(%rip), %rdx
		0000000000221f90:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  221f94:      	callq	0x9d1b0 <symbol>
  221f99:      	movq	%rsi, %rcx
  221f9c:      	callq	0x221fa1 <symbol>
		0000000000221f9d:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221fa1:      	int3
  221fa2:      	leaq	0x14(%rdx), %rcx
  221fa6:      	callq	0xc9080 <symbol>
  221fab:      	leaq	(%rip), %rcx
		0000000000221fae:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221fb2:      	callq	0x221fb7 <symbol>
		0000000000221fb3:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221fb7:      	movq	%rax, %rbx
  221fba:      	movq	%rbx, %rcx
  221fbd:      	leaq	(%rip), %rdx
		0000000000221fc0:  IMAGE_REL_AMD64_REL32	__Str_World_relationship_query_owner_D35CBE587EB0E5D6E7589776EF071647229E94FA283105AC85804DEB0195E3EE
  221fc4:      	callq	0x9d1b0 <symbol>
  221fc9:      	movq	%rbx, %rcx
  221fcc:      	callq	0x221fd1 <symbol>
		0000000000221fcd:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  221fd1:      	int3
  221fd2:      	addq	$0x14, %rcx
  221fd6:      	callq	0xc9070 <symbol>
  221fdb:      	leaq	(%rip), %rcx
		0000000000221fde:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  221fe2:      	callq	0x221fe7 <symbol>
		0000000000221fe3:  IMAGE_REL_AMD64_REL32	RhpNewFast
  221fe7:      	movq	%rax, %rbx
  221fea:      	movq	%rbx, %rcx
  221fed:      	leaq	(%rip), %rdx
		0000000000221ff0:  IMAGE_REL_AMD64_REL32	__Str_World_query_borrow_depth_under_90DACFBADDC9B34DA346DBF85FBA345F03C992BAE16440A5D1346E9603E93AD6
  221ff4:      	callq	0x9d1b0 <symbol>
  221ff9:      	movq	%rbx, %rcx
  221ffc:      	callq	0x222001 <symbol>
		0000000000221ffd:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  222001:      	int3
  222002:      	subq	$0x28, %rsp
  222006:      	movq	%rcx, %rbx
  222009:      	leaq	(%rip), %rsi
		000000000022200c:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_relationship_to_130C43763341288758B88F73A8C62296D46726CD76929D57BAFA34147704E8B2
  222010:      	leaq	(%rip), %rcx
		0000000000222013:  IMAGE_REL_AMD64_REL32	__Str_Query_body_and_query_scope_rel_3877F3EF91DC7E525F34306211733354722E44CD5A5BE4235AF1EB19C4A21F9C
  222017:      	cmpb	$0x0, 0x30(%rbp)
  22201b:      	cmoveq	%rcx, %rsi
  22201f:      	leaq	(%rip), %rcx
		0000000000222022:  IMAGE_REL_AMD64_REL32	??_7__Array<S_P_CoreLib_System_Exception>@@6B@
  222026:      	movl	$0x2, %edx
  22202b:      	callq	0x222030 <symbol>
		000000000022202c:  IMAGE_REL_AMD64_REL32	RhpNewPtrArrayFast
  222030:      	movq	%rax, %rdi
  222033:      	leaq	0x10(%rdi), %rcx
  222037:      	movq	-0xa8(%rbp), %rdx
  22203e:      	callq	0x222043 <symbol>
		000000000022203f:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  222043:      	leaq	0x18(%rdi), %rcx
  222047:      	movq	%rbx, %rdx
  22204a:      	callq	0x22204f <symbol>
		000000000022204b:  IMAGE_REL_AMD64_REL32	RhpAssignRef
  22204f:      	leaq	(%rip), %rcx
		0000000000222052:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_AggregateException@@6B@
  222056:      	callq	0x22205b <symbol>
		0000000000222057:  IMAGE_REL_AMD64_REL32	RhpNewFast
  22205b:      	movq	%rax, %rbx
  22205e:      	movq	%rbx, %rcx
  222061:      	movq	%rsi, %rdx
  222064:      	movq	%rdi, %r8
  222067:      	callq	0x964b0 <symbol>
  22206c:      	movq	%rbx, %rcx
  22206f:      	callq	0x222074 <symbol>
		0000000000222070:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  222074:      	int3
  222075:      	subq	$0x28, %rsp
  222079:      	movq	%rcx, %rbx
  22207c:      	movq	%rbx, -0xa8(%rbp)
  222083:      	movzbl	0x30(%rbp), %edx
  222087:      	movq	0x10(%rbp), %rcx
  22208b:      	xorl	%r8d, %r8d
  22208e:      	callq	0x7630 <symbol>
  222093:      	nop
  222094:      	movq	%rbx, %rcx
  222097:      	callq	0xe7370 <symbol>
  22209c:      	movq	%rax, %rcx
  22209f:      	cmpl	%ecx, (%rcx)
  2220a1:      	callq	0xe73d0 <symbol>
  2220a6:      	callq	0x2220ab <symbol>
		00000000002220a7:  IMAGE_REL_AMD64_REL32	RhpRethrow
  2220ab:      	int3
  2220ac:      	nop
  2220ad:      	nop
  2220ae:      	nop
  2220af:      	nop
