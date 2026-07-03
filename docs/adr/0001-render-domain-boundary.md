# Render domain boundary excludes legacy RHI execution

SomeEngine.Next treats the old RHI, old D3D12 backend, old RenderGraph assumptions, and legacy Render execution as reference material rather than accepted product code. The accepted Render boundary is backend-free domain/model code, with Cluster split into its own backend-free renderer domain; Render and Cluster execution will be implemented later only against accepted Next RHI and RenderGraph boundaries so migration cannot create temporary harness exceptions or fake green builds.
