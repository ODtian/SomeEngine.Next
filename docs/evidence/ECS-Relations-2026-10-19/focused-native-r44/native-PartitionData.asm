
F:\.codex\worktrees\ecs-relations-worker-3\SomeEngine.Next\benchmarks\SomeEngine.ECS.Relations.Benchmarks\obj\Release\net10.0\win-x64\native\SomeEngine.ECS.Relations.Benchmarks.obj:	file format coff-x86-64

Disassembly of section .managedcode$I:

00000000001a6900 <symbol>
  1a6900:      	pushq	%rdi
  1a6901:      	pushq	%rsi
  1a6902:      	pushq	%rbx
  1a6903:      	subq	$0xa0, %rsp
  1a690a:      	xorps	%xmm4, %xmm4
  1a690d:      	movaps	%xmm4, 0x30(%rsp)
  1a6912:      	movabsq	$-0x60, %rax
  1a691c:      	movaps	%xmm4, 0xa0(%rsp,%rax)
  1a6924:      	movaps	%xmm4, 0xb0(%rsp,%rax)
  1a692c:      	movaps	%xmm4, 0xc0(%rsp,%rax)
  1a6934:      	addq	$0x30, %rax
  1a6938:      	jne	0x1a691c <symbol>
  1a693a:      	movl	%edx, %ebx
  1a693c:      	movq	0x38(%rcx), %rsi
  1a6940:      	movq	0x40(%rcx), %r8
  1a6944:      	leaq	0x8(%rcx), %rdx
  1a6948:      	addq	$0x20, %rcx
  1a694c:      	movq	0x10(%rsi), %r9
  1a6950:      	cmpl	0x8(%r9), %ebx
  1a6954:      	jae	0x1a6a02 <symbol>
  1a695a:      	movl	%ebx, %edi
  1a695c:      	leaq	0x10(%r9,%rdi,8), %r9
  1a6961:      	movl	(%r9), %eax
  1a6964:      	movl	0x4(%r9), %r9d
  1a6968:      	movq	%rdx, 0x50(%rsp)
  1a696d:      	movq	%rcx, 0x58(%rsp)
  1a6972:      	xorl	%ecx, %ecx
  1a6974:      	movq	%rcx, 0x60(%rsp)
  1a6979:      	movq	%rcx, 0x68(%rsp)
  1a697e:      	movq	%rcx, 0x70(%rsp)
  1a6983:      	movq	%rcx, 0x78(%rsp)
  1a6988:      	movq	%rcx, 0x80(%rsp)
  1a6990:      	movq	%rcx, 0x88(%rsp)
  1a6998:      	movq	%rcx, 0x90(%rsp)
  1a69a0:      	movq	0x10(%r8), %rcx
  1a69a4:      	movups	0x28(%r8), %xmm0
  1a69a9:      	movups	%xmm0, 0x30(%rsp)
  1a69ae:      	movups	0x38(%r8), %xmm0
  1a69b3:      	movups	%xmm0, 0x40(%rsp)
  1a69b8:      	movl	%eax, 0x20(%rsp)
  1a69bc:      	movl	%r9d, 0x24(%rsp)
  1a69c1:      	leaq	0x98(%rsp), %r8
  1a69c9:      	movq	%r8, 0x28(%rsp)
  1a69ce:      	leaq	0x30(%rsp), %r8
  1a69d3:      	leaq	0x50(%rsp), %rdx
  1a69d8:      	movl	$0x1, %r9d
  1a69de:      	callq	0x225180 <symbol>
  1a69e3:      	movq	0x18(%rsi), %rax
  1a69e7:      	cmpl	0x8(%rax), %ebx
  1a69ea:      	jae	0x1a6a02 <symbol>
  1a69ec:      	movl	0x98(%rsp), %ecx
  1a69f3:      	movl	%ecx, 0x10(%rax,%rdi,4)
  1a69f7:      	addq	$0xa0, %rsp
  1a69fe:      	popq	%rbx
  1a69ff:      	popq	%rsi
  1a6a00:      	popq	%rdi
  1a6a01:      	retq
  1a6a02:      	callq	0x10c6d0 <symbol>
  1a6a07:      	int3
  1a6a08:      	nop
  1a6a09:      	nop
  1a6a0a:      	nop
  1a6a0b:      	nop
  1a6a0c:      	nop
  1a6a0d:      	nop
  1a6a0e:      	nop
  1a6a0f:      	nop
