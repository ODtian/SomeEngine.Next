0000000000020dd0 <symbol>
   20dd0:      	movl	(%rdx), %eax
   20dd2:      	movl	(%r8), %edx
   20dd5:      	addl	%edx, %edx
   20dd7:      	addl	%edx, %eax
   20dd9:      	movl	(%r9), %edx
   20ddc:      	leal	(%rdx,%rdx,2), %edx
   20ddf:      	addl	%edx, %eax
   20de1:      	movq	0x28(%rsp), %rdx
   20de6:      	movl	(%rdx), %edx
   20de8:      	shll	$0x2, %edx
   20deb:      	addl	%edx, %eax
   20ded:      	movq	0x30(%rsp), %rdx
   20df2:      	movl	(%rdx), %edx
   20df4:      	leal	(%rdx,%rdx,4), %edx
   20df7:      	addl	%edx, %eax
   20df9:      	movq	0x38(%rsp), %rdx
   20dfe:      	movl	(%rdx), %edx
   20e00:      	leal	(%rdx,%rdx,2), %edx
   20e03:      	addl	%edx, %edx
   20e05:      	addl	%edx, %eax
   20e07:      	movq	0x40(%rsp), %rdx
   20e0c:      	movl	(%rdx), %edx
   20e0e:      	leal	(,%rdx,8), %r8d
   20e16:      	subl	%edx, %r8d
   20e19:      	addl	%r8d, %eax
   20e1c:      	movq	0x48(%rsp), %rdx
   20e21:      	movl	(%rdx), %edx
   20e23:      	leal	(%rax,%rdx,8), %eax
   20e26:      	cmpl	(%rcx), %eax
   20e28:      	setge	%al
   20e2b:      	movzbl	%al, %eax
   20e2e:      	retq
   20e2f:      	nop

