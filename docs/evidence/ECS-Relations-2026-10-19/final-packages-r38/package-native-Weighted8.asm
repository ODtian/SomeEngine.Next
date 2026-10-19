0000000000020c30 <symbol>
   20c30:      	movl	(%rdx), %eax
   20c32:      	movl	(%r8), %edx
   20c35:      	addl	%edx, %edx
   20c37:      	addl	%edx, %eax
   20c39:      	movl	(%r9), %edx
   20c3c:      	leal	(%rdx,%rdx,2), %edx
   20c3f:      	addl	%edx, %eax
   20c41:      	movq	0x28(%rsp), %rdx
   20c46:      	movl	(%rdx), %edx
   20c48:      	shll	$0x2, %edx
   20c4b:      	addl	%edx, %eax
   20c4d:      	movq	0x30(%rsp), %rdx
   20c52:      	movl	(%rdx), %edx
   20c54:      	leal	(%rdx,%rdx,4), %edx
   20c57:      	addl	%edx, %eax
   20c59:      	movq	0x38(%rsp), %rdx
   20c5e:      	movl	(%rdx), %edx
   20c60:      	leal	(%rdx,%rdx,2), %edx
   20c63:      	addl	%edx, %edx
   20c65:      	addl	%edx, %eax
   20c67:      	movq	0x40(%rsp), %rdx
   20c6c:      	movl	(%rdx), %edx
   20c6e:      	leal	(,%rdx,8), %r8d
   20c76:      	subl	%edx, %r8d
   20c79:      	addl	%r8d, %eax
   20c7c:      	movq	0x48(%rsp), %rdx
   20c81:      	movl	(%rdx), %edx
   20c83:      	leal	(%rax,%rdx,8), %eax
   20c86:      	cmpl	(%rcx), %eax
   20c88:      	setge	%al
   20c8b:      	movzbl	%al, %eax
   20c8e:      	retq
   20c8f:      	nop

