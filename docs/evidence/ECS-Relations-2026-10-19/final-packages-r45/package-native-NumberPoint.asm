0000000000197ea0 <symbol>
  197ea0:      	pushq	%r14
  197ea2:      	pushq	%rdi
  197ea3:      	pushq	%rsi
  197ea4:      	pushq	%rbp
  197ea5:      	pushq	%rbx
  197ea6:      	subq	$0x60, %rsp
  197eaa:      	xorl	%eax, %eax
  197eac:      	movq	%rax, 0x38(%rsp)
  197eb1:      	xorps	%xmm4, %xmm4
  197eb4:      	movaps	%xmm4, 0x40(%rsp)
  197eb9:      	movaps	%xmm4, 0x50(%rsp)
  197ebe:      	movq	%rdx, 0x98(%rsp)
  197ec6:      	movq	(%rcx), %rbx
  197ec9:      	movq	0x8(%rcx), %rsi
  197ecd:      	movl	0x10(%rcx), %edi
  197ed0:      	cmpb	%bl, (%rbx)
  197ed2:      	movq	%gs:0x58, %rcx
  197edb:      	movl	(%rip), %eax
		0000000000197edd:  IMAGE_REL_AMD64_REL32	_tls_index
  197ee1:      	movl	$0x0, %edx
		0000000000197ee2:  IMAGE_REL_AMD64_SECREL	tls_InlinedThreadStatics
  197ee6:      	addq	(%rcx,%rax,8), %rdx
  197eea:      	movq	(%rdx), %rbp
  197eed:      	testq	%rbp, %rbp
  197ef0:      	je	0x198223 <symbol>
  197ef6:      	movq	0x18(%rbp), %rbp
  197efa:      	testq	%rbp, %rbp
  197efd:      	je	0x197f19 <symbol>
  197eff:      	cmpq	%rbx, 0x8(%rbp)
  197f03:      	je	0x197f23 <symbol>
  197f05:      	movq	0x18(%rbp), %rdx
  197f09:      	movq	%rbx, %rcx
  197f0c:      	callq	0x44c0 <symbol>
  197f11:      	movq	%rax, %rbp
  197f14:      	testq	%rbp, %rbp
  197f17:      	jne	0x197f23 <symbol>
  197f19:      	movq	0x30(%rbx), %rcx
  197f1d:      	movq	0x8(%rcx), %rcx
  197f21:      	jmp	0x197f27 <symbol>
  197f23:      	movq	0x10(%rbp), %rcx
  197f27:      	movq	0x18(%rcx), %rcx
  197f2b:      	movq	0x10(%rcx), %rbx
  197f2f:      	movq	%rbx, %rcx
  197f32:      	movq	0x98(%rsp), %rdx
  197f3a:      	cmpl	%ecx, (%rcx)
  197f3c:      	callq	0x19fb0 <symbol>
  197f41:      	testl	%eax, %eax
  197f43:      	je	0x198113 <symbol>
  197f49:      	leaq	0x40(%rsp), %rdx
  197f4e:      	movq	%rbx, %rcx
  197f51:      	movq	0x98(%rsp), %r8
  197f59:      	callq	0x1a280 <symbol>
  197f5e:      	movq	0x40(%rsp), %rbx
  197f63:      	movq	0x48(%rsp), %rbp
  197f68:      	movl	0x54(%rsp), %r14d
  197f6d:      	testq	%rbx, %rbx
  197f70:      	je	0x198113 <symbol>
  197f76:      	movq	0x18(%rsi), %rcx
  197f7a:      	movq	0x70(%rbx), %rdx
  197f7e:      	leaq	0x38(%rsp), %r8
  197f83:      	cmpl	%ecx, (%rcx)
  197f85:      	callq	0xfe110 <symbol>
  197f8a:      	testl	%eax, %eax
  197f8c:      	je	0x198113 <symbol>
  197f92:      	movq	0x38(%rsp), %rdx
  197f97:      	cmpq	%rbx, 0x30(%rdx)
  197f9b:      	jne	0x198113 <symbol>
  197fa1:      	movq	0x38(%rsp), %rcx
  197fa6:      	cmpb	%cl, (%rcx)
  197fa8:      	movq	0x10(%rcx), %rdx
  197fac:      	xorl	%r8d, %r8d
  197faf:      	jmp	0x198016 <symbol>
  197fb1:      	xorl	%eax, %eax
  197fb3:      	cmpl	%eax, %r8d
  197fb6:      	jge	0x198023 <symbol>
  197fb8:      	movq	0x8(%rbp), %rax
  197fbc:      	movq	0x18(%rax), %rax
  197fc0:      	testq	%rax, %rax
  197fc3:      	je	0x197fce <symbol>
  197fc5:      	leaq	0x10(%rax), %r10
  197fc9:      	movl	0x8(%rax), %eax
  197fcc:      	jmp	0x197fd3 <symbol>
  197fce:      	xorl	%r10d, %r10d
  197fd1:      	xorl	%eax, %eax
  197fd3:      	movq	0x10(%rcx), %r9
  197fd7:      	testq	%r9, %r9
  197fda:      	je	0x197fe6 <symbol>
  197fdc:      	leaq	0x10(%r9), %r11
  197fe0:      	movl	0x8(%r9), %r9d
  197fe4:      	jmp	0x197fec <symbol>
  197fe6:      	xorl	%r11d, %r11d
  197fe9:      	xorl	%r9d, %r9d
  197fec:      	cmpl	%r9d, %r8d
  197fef:      	jae	0x19828d <symbol>
  197ff5:      	movl	(%r11,%r8,4), %r9d
  197ff9:      	cmpl	%eax, %r9d
  197ffc:      	jae	0x19828d <symbol>
  198002:      	movl	%r9d, %eax
  198005:      	movl	(%r10,%rax,4), %eax
  198009:      	subl	%edi, %eax
  19800b:      	testl	%eax, %eax
  19800d:      	jle	0x198113 <symbol>
  198013:      	incl	%r8d
  198016:      	movq	%rdx, %rax
  198019:      	testq	%rax, %rax
  19801c:      	je	0x197fb1 <symbol>
  19801e:      	movl	0x8(%rax), %eax
  198021:      	jmp	0x197fb3 <symbol>
  198023:      	movq	0x8(%rcx), %rdx
  198027:      	xorl	%r8d, %r8d
  19802a:      	jmp	0x198091 <symbol>
  19802c:      	xorl	%eax, %eax
  19802e:      	cmpl	%eax, %r8d
  198031:      	jge	0x19809e <symbol>
  198033:      	movq	0x8(%rbp), %rax
  198037:      	movq	0x18(%rax), %rax
  19803b:      	testq	%rax, %rax
  19803e:      	je	0x198049 <symbol>
  198040:      	leaq	0x10(%rax), %r10
  198044:      	movl	0x8(%rax), %eax
  198047:      	jmp	0x19804e <symbol>
  198049:      	xorl	%r10d, %r10d
  19804c:      	xorl	%eax, %eax
  19804e:      	movq	0x8(%rcx), %r9
  198052:      	testq	%r9, %r9
  198055:      	je	0x198061 <symbol>
  198057:      	leaq	0x10(%r9), %r11
  19805b:      	movl	0x8(%r9), %r9d
  19805f:      	jmp	0x198067 <symbol>
  198061:      	xorl	%r11d, %r11d
  198064:      	xorl	%r9d, %r9d
  198067:      	cmpl	%r9d, %r8d
  19806a:      	jae	0x19828d <symbol>
  198070:      	movl	(%r11,%r8,8), %r9d
  198074:      	cmpl	%eax, %r9d
  198077:      	jae	0x19828d <symbol>
  19807d:      	movl	%r9d, %eax
  198080:      	movl	(%r10,%rax,4), %eax
  198084:      	subl	%edi, %eax
  198086:      	testl	%eax, %eax
  198088:      	jle	0x198113 <symbol>
  19808e:      	incl	%r8d
  198091:      	movq	%rdx, %rax
  198094:      	testq	%rax, %rax
  198097:      	je	0x19802c <symbol>
  198099:      	movl	0x8(%rax), %eax
  19809c:      	jmp	0x19802e <symbol>
  19809e:      	movq	0x18(%rcx), %rdx
  1980a2:      	testq	%rdx, %rdx
  1980a5:      	je	0x1980ac <symbol>
  1980a7:      	movl	0x8(%rdx), %edx
  1980aa:      	jmp	0x1980ae <symbol>
  1980ac:      	xorl	%edx, %edx
  1980ae:      	testl	%edx, %edx
  1980b0:      	jne	0x1980c6 <symbol>
  1980b2:      	movq	0x20(%rcx), %rdx
  1980b6:      	testq	%rdx, %rdx
  1980b9:      	je	0x1980c0 <symbol>
  1980bb:      	movl	0x8(%rdx), %edx
  1980be:      	jmp	0x1980c2 <symbol>
  1980c0:      	xorl	%edx, %edx
  1980c2:      	testl	%edx, %edx
  1980c4:      	je	0x1980f4 <symbol>
  1980c6:      	leaq	0x28(%rsp), %rdx
  1980cb:      	movq	%rbp, %r8
  1980ce:      	callq	0xdbe0 <symbol>
  1980d3:      	cmpq	$0x0, 0x28(%rsp)
  1980d9:      	je	0x1980e2 <symbol>
  1980db:      	movl	$0x1, %ecx
  1980e0:      	jmp	0x1980ee <symbol>
  1980e2:      	cmpq	$0x0, 0x30(%rsp)
  1980e8:      	setne	%cl
  1980eb:      	movzbl	%cl, %ecx
  1980ee:      	testl	%ecx, %ecx
  1980f0:      	je	0x198113 <symbol>
  1980f2:      	jmp	0x1980fa <symbol>
  1980f4:      	cmpl	$0x0, 0x30(%rbp)
  1980f8:      	jle	0x198113 <symbol>
  1980fa:      	movq	0x38(%rsp), %rcx
  1980ff:      	movq	%rbp, %rdx
  198102:      	movl	%r14d, %r8d
  198105:      	movl	%edi, %r9d
  198108:      	cmpl	%ecx, (%rcx)
  19810a:      	callq	0xd980 <symbol>
  19810f:      	testl	%eax, %eax
  198111:      	jne	0x19811f <symbol>
  198113:      	xorl	%eax, %eax
  198115:      	movq	%rax, 0x38(%rsp)
  19811a:      	jmp	0x198216 <symbol>
  19811f:      	movq	0x38(%rsp), %rbx
  198124:      	leaq	(%rip), %rsi
		0000000000198127:  IMAGE_REL_AMD64_REL32	?__NONGCSTATICS@SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>>@@
  19812b:      	cmpq	$0x0, -0x8(%rsi)
  198130:      	jne	0x198237 <symbol>
  198136:      	movl	(%rsi), %edx
  198138:      	cmpb	%bl, (%rbx)
  19813a:      	movq	0x28(%rbx), %rax
  19813e:      	xorl	%ecx, %ecx
  198140:      	jmp	0x198144 <symbol>
  198142:      	incl	%ecx
  198144:      	movq	%rax, %r8
  198147:      	testq	%r8, %r8
  19814a:      	je	0x198152 <symbol>
  19814c:      	movl	0x8(%r8), %r8d
  198150:      	jmp	0x198155 <symbol>
  198152:      	xorl	%r8d, %r8d
  198155:      	cmpl	%r8d, %ecx
  198158:      	jge	0x198207 <symbol>
  19815e:      	movq	%rax, %r8
  198161:      	testq	%r8, %r8
  198164:      	je	0x198170 <symbol>
  198166:      	leaq	0x10(%r8), %r10
  19816a:      	movl	0x8(%r8), %r8d
  19816e:      	jmp	0x198176 <symbol>
  198170:      	xorl	%r10d, %r10d
  198173:      	xorl	%r8d, %r8d
  198176:      	cmpl	%r8d, %ecx
  198179:      	jae	0x19828d <symbol>
  19817f:      	leaq	(%rcx,%rcx,2), %r8
  198183:      	cmpl	%edx, (%r10,%r8,4)
  198187:      	jne	0x198142 <symbol>
  198189:      	testq	%rax, %rax
  19818c:      	je	0x198197 <symbol>
  19818e:      	leaq	0x10(%rax), %rdx
  198192:      	movl	0x8(%rax), %eax
  198195:      	jmp	0x19819b <symbol>
  198197:      	xorl	%edx, %edx
  198199:      	xorl	%eax, %eax
  19819b:      	cmpl	%eax, %ecx
  19819d:      	jae	0x19828d <symbol>
  1981a3:      	movl	%ecx, %eax
  1981a5:      	leaq	(%rax,%rax,2), %rax
  1981a9:      	leaq	(%rdx,%rax,4), %rax
  1981ad:      	movl	0x4(%rax), %ecx
  1981b0:      	movzbl	0x8(%rax), %eax
  1981b4:      	cmpl	$0x1, %eax
  1981b7:      	je	0x1981c2 <symbol>
  1981b9:      	cmpl	$0x3, %eax
  1981bc:      	jne	0x198241 <symbol>
  1981c2:      	movq	0x8(%rbp), %rax
  1981c6:      	movq	0x10(%rax), %rax
  1981ca:      	cmpl	0x8(%rax), %ecx
  1981cd:      	jae	0x19828d <symbol>
  1981d3:      	movl	%ecx, %ecx
  1981d5:      	movq	0x10(%rax,%rcx,8), %rax
  1981da:      	testq	%rax, %rax
  1981dd:      	je	0x1981e8 <symbol>
  1981df:      	leaq	0x10(%rax), %rcx
  1981e3:      	movl	0x8(%rax), %eax
  1981e6:      	jmp	0x1981ec <symbol>
  1981e8:      	xorl	%ecx, %ecx
  1981ea:      	xorl	%eax, %eax
  1981ec:      	cmpl	%eax, %r14d
  1981ef:      	jae	0x19828d <symbol>
  1981f5:      	movl	%r14d, %eax
  1981f8:      	leaq	(%rcx,%rax,4), %rax
  1981fc:      	addq	$0x60, %rsp
  198200:      	popq	%rbx
  198201:      	popq	%rbp
  198202:      	popq	%rsi
  198203:      	popq	%rdi
  198204:      	popq	%r14
  198206:      	retq
  198207:      	movq	0x30(%rbx), %rcx
  19820b:      	cmpl	%ecx, (%rcx)
  19820d:      	callq	0x1ca30 <symbol>
  198212:      	testl	%eax, %eax
  198214:      	jne	0x19827f <symbol>
  198216:      	xorl	%eax, %eax
  198218:      	addq	$0x60, %rsp
  19821c:      	popq	%rbx
  19821d:      	popq	%rbp
  19821e:      	popq	%rsi
  19821f:      	popq	%rdi
  198220:      	popq	%r14
  198222:      	retq
  198223:      	movq	%rdx, %rcx
  198226:      	leaq	-0x10328d(%rip), %rax
  19822d:      	callq	*%rax
  19822f:      	movq	%rax, %rbp
  198232:      	jmp	0x197ef6 <symbol>
  198237:      	callq	0x19823c <symbol>
		0000000000198238:  IMAGE_REL_AMD64_REL32	__GetNonGCStaticBase_SomeEngine_ECS_SomeEngine_ECS_Registry_ComponentMetadata_1<SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>>
  19823c:      	jmp	0x198136 <symbol>
  198241:      	leaq	(%rip), %rcx
		0000000000198244:  IMAGE_REL_AMD64_REL32	??_7S_P_CoreLib_System_InvalidOperationException@@6B@
  198248:      	callq	0x19824d <symbol>
		0000000000198249:  IMAGE_REL_AMD64_REL32	RhpNewFast
  19824d:      	movq	%rax, %rbx
  198250:      	leaq	(%rip), %rcx
		0000000000198253:  IMAGE_REL_AMD64_REL32	__RuntimeType_SomeEngine_ECS_Relations_AotSmoke_SomeEngine_Tools_RelationsSmoke_Number_1<Int32>
  198257:      	callq	0x40aa0 <symbol>
  19825c:      	movq	%rax, %rcx
  19825f:      	leaq	(%rip), %rdx
		0000000000198262:  IMAGE_REL_AMD64_REL32	__Str__was_not_declared_for_query_re_3DAC58C3D425A8E368C52F9A8DF72803BD0D416C884BD906DC32246D69AA8921
  198266:      	callq	0x42280 <symbol>
  19826b:      	movq	%rax, %rdx
  19826e:      	movq	%rbx, %rcx
  198271:      	callq	0x4cd60 <symbol>
  198276:      	movq	%rbx, %rcx
  198279:      	callq	0x19827e <symbol>
		000000000019827a:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  19827e:      	int3
  19827f:      	callq	0x198350 <symbol>
  198284:      	movq	%rax, %rcx
  198287:      	callq	0x19828c <symbol>
		0000000000198288:  IMAGE_REL_AMD64_REL32	RhpThrowEx
  19828c:      	int3
  19828d:      	callq	0x97e90 <symbol>
  198292:      	int3
  198293:      	nop
  198294:      	nop
  198295:      	nop
  198296:      	nop
  198297:      	nop
  198298:      	nop
  198299:      	nop
  19829a:      	nop
  19829b:      	nop
  19829c:      	nop
  19829d:      	nop
  19829e:      	nop
  19829f:      	nop

