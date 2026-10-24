00000000001d1660 <symbol>
  1d1660:      	pushq	%r15
  1d1662:      	pushq	%r14
  1d1664:      	pushq	%r13
  1d1666:      	pushq	%rdi
  1d1667:      	pushq	%rsi
  1d1668:      	pushq	%rbp
  1d1669:      	pushq	%rbx
  1d166a:      	subq	$0x180, %rsp
  1d1671:      	xorps	%xmm4, %xmm4
  1d1674:      	movaps	%xmm4, 0x20(%rsp)
  1d1679:      	movabsq	$-0x150, %rax
  1d1683:      	movaps	%xmm4, 0x180(%rsp,%rax)
  1d168b:      	movaps	%xmm4, 0x190(%rsp,%rax)
  1d1693:      	movaps	%xmm4, 0x1a0(%rsp,%rax)
  1d169b:      	addq	$0x30, %rax
  1d169f:      	jne	0x1d1683 <symbol>
  1d16a1:      	movq	%rcx, 0x1c0(%rsp)
  1d16a9:      	movq	%r8, %rbx
  1d16ac:      	leaq	0x1c0(%rsp), %rcx
  1d16b4:      	leaq	0x20(%rsp), %rdx
  1d16b9:      	callq	0xb1d0 <symbol>
  1d16be:      	movups	0x20(%rsp), %xmm0
  1d16c3:      	movups	%xmm0, 0x60(%rsp)
  1d16c8:      	movups	0x30(%rsp), %xmm0
  1d16cd:      	movups	%xmm0, 0x70(%rsp)
  1d16d2:      	movups	0x40(%rsp), %xmm0
  1d16d7:      	movups	%xmm0, 0x80(%rsp)
  1d16df:      	movups	0x50(%rsp), %xmm0
  1d16e4:      	movups	%xmm0, 0x90(%rsp)
  1d16ec:      	xorps	%xmm0, %xmm0
  1d16ef:      	movups	%xmm0, 0xa0(%rsp)
  1d16f7:      	xorps	%xmm0, %xmm0
  1d16fa:      	movups	%xmm0, 0xb0(%rsp)
  1d1702:      	leaq	0x120(%rsp), %rcx
  1d170a:      	leaq	0x60(%rsp), %rdx
  1d170f:      	movl	$0x60, %r8d
  1d1715:      	callq	0x87e30 <symbol>
  1d171a:      	movq	0x140(%rsp), %rsi
  1d1722:      	leaq	0x120(%rsp), %r9
  1d172a:      	movq	0x138(%rsp), %rcx
  1d1732:      	movq	0x140(%rsp), %rdx
  1d173a:      	movl	0x148(%rsp), %r8d
  1d1742:      	callq	0xd7e0 <symbol>
  1d1747:      	testl	%eax, %eax
  1d1749:      	jne	0x1d1794 <symbol>
  1d174b:      	leaq	0x128(%rsp), %rcx
  1d1753:      	callq	0x154ae0 <symbol>
  1d1758:      	testl	%eax, %eax
  1d175a:      	je	0x1d19a0 <symbol>
  1d1760:      	movl	$0xffffffff, 0x120(%rsp)
  1d176b:      	leaq	0x120(%rsp), %r9
  1d1773:      	movq	0x138(%rsp), %rcx
  1d177b:      	movq	0x140(%rsp), %rdx
  1d1783:      	movl	0x148(%rsp), %r8d
  1d178b:      	callq	0xd7e0 <symbol>
  1d1790:      	testl	%eax, %eax
  1d1792:      	je	0x1d174b <symbol>
  1d1794:      	movl	0x120(%rsp), %edi
  1d179b:      	movq	0x140(%rsp), %rbp
  1d17a3:      	cmpq	0x140(%rsp), %rsi
  1d17ab:      	je	0x1d17ca <symbol>
  1d17ad:      	leaq	0x120(%rsp), %rcx
  1d17b5:      	movq	0x138(%rsp), %rdx
  1d17bd:      	movq	0x140(%rsp), %r8
  1d17c5:      	callq	0x161240 <symbol>
  1d17ca:      	cmpl	$0x0, 0x168(%rsp)
  1d17d2:      	jne	0x1d17d8 <symbol>
  1d17d4:      	xorl	%esi, %esi
  1d17d6:      	jmp	0x1d17f3 <symbol>
  1d17d8:      	cmpl	0x168(%rsp), %edi
  1d17df:      	jae	0x1d19b7 <symbol>
  1d17e5:      	movl	%edi, %ecx
  1d17e7:      	imulq	$0x30, %rcx, %rsi
  1d17eb:      	addq	0x160(%rsp), %rsi
  1d17f3:      	movq	0x8(%rbp), %rcx
  1d17f7:      	movq	0x8(%rcx), %rcx
  1d17fb:      	testq	%rcx, %rcx
  1d17fe:      	je	0x1d1809 <symbol>
  1d1800:      	leaq	0x10(%rcx), %rdx
  1d1804:      	movl	0x8(%rcx), %ecx
  1d1807:      	jmp	0x1d180d <symbol>
  1d1809:      	xorl	%edx, %edx
  1d180b:      	xorl	%ecx, %ecx
  1d180d:      	cmpl	%ecx, %edi
  1d180f:      	jae	0x1d19b7 <symbol>
  1d1815:      	movl	%edi, %ecx
  1d1817:      	leaq	(%rdx,%rcx,8), %rcx
  1d181b:      	movl	(%rcx), %edi
  1d181d:      	movl	0x4(%rcx), %ebp
  1d1820:      	leaq	0xc0(%rsp), %rcx
  1d1828:      	leaq	0x120(%rsp), %rdx
  1d1830:      	movl	$0x60, %r8d
  1d1836:      	callq	0x87e30 <symbol>
  1d183b:      	jmp	0x1d18db <symbol>
  1d1840:      	cmpl	0x108(%rsp), %r15d
  1d1848:      	jae	0x1d19b7 <symbol>
  1d184e:      	movl	%r15d, %eax
  1d1851:      	imulq	$0x30, %rax, %rax
  1d1855:      	addq	0x100(%rsp), %rax
  1d185d:      	cmpl	$0x0, (%rsi)
  1d1860:      	je	0x1d1867 <symbol>
  1d1862:      	cmpl	$0x0, (%rax)
  1d1865:      	jne	0x1d18db <symbol>
  1d1867:      	movq	0x8(%r13), %rax
  1d186b:      	movq	0x8(%rax), %rax
  1d186f:      	testq	%rax, %rax
  1d1872:      	je	0x1d187d <symbol>
  1d1874:      	leaq	0x10(%rax), %rcx
  1d1878:      	movl	0x8(%rax), %eax
  1d187b:      	jmp	0x1d1881 <symbol>
  1d187d:      	xorl	%ecx, %ecx
  1d187f:      	xorl	%eax, %eax
  1d1881:      	cmpl	%eax, %r15d
  1d1884:      	jae	0x1d19b7 <symbol>
  1d188a:      	movl	%r15d, %eax
  1d188d:      	leaq	(%rcx,%rax,8), %rax
  1d1891:      	movl	(%rax), %ecx
  1d1893:      	movl	0x4(%rax), %eax
  1d1896:      	cmpl	%ecx, %edi
  1d1898:      	jl	0x1d18aa <symbol>
  1d189a:      	cmpl	%ecx, %edi
  1d189c:      	jne	0x1d1992 <symbol>
  1d18a2:      	cmpl	%eax, %ebp
  1d18a4:      	jge	0x1d1992 <symbol>
  1d18aa:      	movl	%edi, %edx
  1d18ac:      	movl	%ebp, %r8d
  1d18af:      	movl	0x10(%rbx), %r10d
  1d18b3:      	leal	0x1(%r10), %r9d
  1d18b7:      	movl	%r9d, 0x10(%rbx)
  1d18bb:      	cmpl	0x8(%rbx), %r10d
  1d18bf:      	jae	0x1d19b7 <symbol>
  1d18c5:      	shlq	$0x4, %r10
  1d18c9:      	addq	(%rbx), %r10
  1d18cc:      	movl	%edx, (%r10)
  1d18cf:      	movl	%r8d, 0x4(%r10)
  1d18d3:      	movl	%ecx, 0x8(%r10)
  1d18d7:      	movl	%eax, 0xc(%r10)
  1d18db:      	movq	0xe0(%rsp), %rdx
  1d18e3:      	movq	%rdx, %r14
  1d18e6:      	leaq	0xc0(%rsp), %r9
  1d18ee:      	movq	0xd8(%rsp), %rcx
  1d18f6:      	movl	0xe8(%rsp), %r8d
  1d18fe:      	callq	0xd7e0 <symbol>
  1d1903:      	testl	%eax, %eax
  1d1905:      	jne	0x1d1950 <symbol>
  1d1907:      	leaq	0xc8(%rsp), %rcx
  1d190f:      	callq	0x154ae0 <symbol>
  1d1914:      	testl	%eax, %eax
  1d1916:      	je	0x1d171a <symbol>
  1d191c:      	movl	$0xffffffff, 0xc0(%rsp)
  1d1927:      	leaq	0xc0(%rsp), %r9
  1d192f:      	movq	0xd8(%rsp), %rcx
  1d1937:      	movq	0xe0(%rsp), %rdx
  1d193f:      	movl	0xe8(%rsp), %r8d
  1d1947:      	callq	0xd7e0 <symbol>
  1d194c:      	testl	%eax, %eax
  1d194e:      	je	0x1d1907 <symbol>
  1d1950:      	movl	0xc0(%rsp), %r15d
  1d1958:      	movq	0xe0(%rsp), %r8
  1d1960:      	movq	%r8, %r13
  1d1963:      	cmpq	%r8, %r14
  1d1966:      	je	0x1d197d <symbol>
  1d1968:      	leaq	0xc0(%rsp), %rcx
  1d1970:      	movq	0xd8(%rsp), %rdx
  1d1978:      	callq	0x161240 <symbol>
  1d197d:      	cmpl	$0x0, 0x108(%rsp)
  1d1985:      	jne	0x1d1840 <symbol>
  1d198b:      	xorl	%eax, %eax
  1d198d:      	jmp	0x1d185d <symbol>
  1d1992:      	movl	%ecx, %edx
  1d1994:      	movl	%eax, %r8d
  1d1997:      	movl	%edi, %ecx
  1d1999:      	movl	%ebp, %eax
  1d199b:      	jmp	0x1d18af <symbol>
  1d19a0:      	movl	$0x1, %eax
  1d19a5:      	addq	$0x180, %rsp
  1d19ac:      	popq	%rbx
  1d19ad:      	popq	%rbp
  1d19ae:      	popq	%rsi
  1d19af:      	popq	%rdi
  1d19b0:      	popq	%r13
  1d19b2:      	popq	%r14
  1d19b4:      	popq	%r15
  1d19b6:      	retq
  1d19b7:      	callq	0x10c710 <symbol>
  1d19bc:      	int3
  1d19bd:      	nop
  1d19be:      	nop
  1d19bf:      	nop

