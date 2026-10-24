// Four power-moment reconstruction by Muenstermann, Krumpen, Klein and Peters (2018).
// Original code: https://momentsingraphics.de/I3D2018.html ; CC0.
float ReconstructTransmittance(float b_0, float2 b_even, float2 b_odd, float depth, float bias, float overestimation, float4 bias_vector)
{
	float4 b = float4(b_odd.x, b_even.x, b_odd.y, b_even.y);
	// Bias input data to avoid artifacts
	b = lerp(b, bias_vector, bias);
	float3 z;
	z[0] = depth;

	// Compute a Cholesky factorization of the Hankel matrix B storing only non-
	// trivial entries or related products
	float L21D11=mad(-b[0],b[1],b[2]);
	float D11=mad(-b[0],b[0], b[1]);
	float InvD11=1.0f/D11;
	float L21=L21D11*InvD11;
	float SquaredDepthVariance=mad(-b[1],b[1], b[3]);
	float D22=mad(-L21D11,L21,SquaredDepthVariance);

	// Obtain a scaled inverse image of bz=(1,z[0],z[0]*z[0])^T
	float3 c=float3(1.0f,z[0],z[0]*z[0]);
	// Forward substitution to solve L*c1=bz
	c[1]-=b.x;
	c[2]-=b.y+L21*c[1];
	// Scaling to solve D*c2=c1
	c[1]*=InvD11;
	c[2]/=D22;
	// Backward substitution to solve L^T*c3=c2
	c[1]-=L21*c[2];
	c[0]-=dot(c.yz,b.xy);
	// Solve the quadratic equation c[0]+c[1]*z+c[2]*z^2 to obtain solutions 
	// z[1] and z[2]
	float InvC2=1.0f/c[2];
	float p=c[1]*InvC2;
	float q=c[0]*InvC2;
	float D=(p*p*0.25f)-q;
	float r=sqrt(D);
	z[1]=-p*0.5f-r;
	z[2]=-p*0.5f+r;
	// Compute the absorbance by summing the appropriate weights
	float3 polynomial;
	float3 weight_factor = float3(overestimation, (z[1] < z[0])?1.0f:0.0f, (z[2] < z[0])?1.0f:0.0f);
	float f0=weight_factor[0];
	float f1=weight_factor[1];
	float f2=weight_factor[2];
	float f01=(f1-f0)/(z[1]-z[0]);
	float f12=(f2-f1)/(z[2]-z[1]);
	float f012=(f12-f01)/(z[2]-z[0]);
	polynomial[0]=f012;
	polynomial[1]=polynomial[0];
	polynomial[0]=f01-polynomial[0]*z[1];
	polynomial[2]=polynomial[1];
	polynomial[1]=polynomial[0]-polynomial[1]*z[0];
	polynomial[0]=f0-polynomial[0]*z[0];
	float absorbance = polynomial[0] + dot(b.xy, polynomial.yz);;
	// Turn the normalized absorbance into transmittance
	return saturate(exp(-b_0 * absorbance));
}


float3 SolveCubic(float4 Coefficient) {
	// Normalize the polynomial
	Coefficient.xyz /= Coefficient.w;
	// Divide middle coefficients by three
	Coefficient.yz /= 3.0f;
	// Compute the Hessian and the discrimant
	float3 Delta = float3(
		mad(-Coefficient.z, Coefficient.z, Coefficient.y),
		mad(-Coefficient.y, Coefficient.z, Coefficient.x),
		dot(float2(Coefficient.z, -Coefficient.y), Coefficient.xy)
		);
	float Discriminant = dot(float2(4.0f*Delta.x, -Delta.y), Delta.zy);
	// Compute coefficients of the depressed cubic 
	// (third is zero, fourth is one)
	float2 Depressed = float2(
		mad(-2.0f*Coefficient.z, Delta.x, Delta.y),
		Delta.x
		);
	// Take the cubic root of a normalized complex number
	float Theta = atan2(sqrt(Discriminant), -Depressed.x) / 3.0f;
	float2 CubicRoot;
	sincos(Theta, CubicRoot.y, CubicRoot.x);
	// Compute the three roots, scale appropriately and 
	// revert the depression transform
	float3 Root = float3(
		CubicRoot.x,
		dot(float2(-0.5f, -0.5f*sqrt(3.0f)), CubicRoot),
		dot(float2(-0.5f, 0.5f*sqrt(3.0f)), CubicRoot)
		);
	Root = mad(2.0f*sqrt(-Depressed.y), Root, -Coefficient.z);
	return Root;
}

float ReconstructTransmittance6(float b_0, float3 b_even, float3 b_odd, float depth, float bias, float overestimation, float bias_vector[6])
{
	float b[6] = { b_odd.x, b_even.x, b_odd.y, b_even.y, b_odd.z, b_even.z };
	// Bias input data to avoid artifacts
	[unroll] for (int i = 0; i != 6; ++i) {
		b[i] = lerp(b[i], bias_vector[i], bias);
	}

	float4 z;
	z[0] = depth;

	// Compute a Cholesky factorization of the Hankel matrix B storing only non-
	// trivial entries or related products
	float InvD11 = 1.0f / mad(-b[0], b[0], b[1]);
	float L21D11 = mad(-b[0], b[1], b[2]);
	float L21 = L21D11*InvD11;
	float D22 = mad(-L21D11, L21, mad(-b[1], b[1], b[3]));
	float L31D11 = mad(-b[0], b[2], b[3]);
	float L31 = L31D11*InvD11;
	float InvD22 = 1.0f / D22;
	float L32D22 = mad(-L21D11, L31, mad(-b[1], b[2], b[4]));
	float L32 = L32D22*InvD22;
	float D33 = mad(-b[2], b[2], b[5]) - dot(float2(L31D11, L32D22), float2(L31, L32));
	float InvD33 = 1.0f / D33;

	// Construct the polynomial whose roots have to be points of support of the 
	// distribution: bz=(1,z[0],z[0]*z[0],z[0]*z[0]*z[0])^T
	float4 c;
	c[0] = 1.0f;
	c[1] = z[0];
	c[2] = c[1] * z[0];
	c[3] = c[2] * z[0];
	// Forward substitution to solve L*c1=bz
	c[1] -= b[0];
	c[2] -= mad(L21, c[1], b[1]);
	c[3] -= b[2] + dot(float2(L31, L32), c.yz);
	// Scaling to solve D*c2=c1
	c.yzw *= float3(InvD11, InvD22, InvD33);
	// Backward substitution to solve L^T*c3=c2
	c[2] -= L32*c[3];
	c[1] -= dot(float2(L21, L31), c.zw);
	c[0] -= dot(float3(b[0], b[1], b[2]), c.yzw);

	// Solve the cubic equation
	z.yzw = SolveCubic(c);

	// Compute the absorbance by summing the appropriate weights
	float4 weigth_factor;
	weigth_factor[0] = overestimation;
	weigth_factor.yzw = (z.yzw > z.xxx) ? float3 (0.0f, 0.0f, 0.0f) : float3 (1.0f, 1.0f, 1.0f);
	// Construct an interpolation polynomial
	float f0 = weigth_factor[0];
	float f1 = weigth_factor[1];
	float f2 = weigth_factor[2];
	float f3 = weigth_factor[3];
	float f01 = (f1 - f0) / (z[1] - z[0]);
	float f12 = (f2 - f1) / (z[2] - z[1]);
	float f23 = (f3 - f2) / (z[3] - z[2]);
	float f012 = (f12 - f01) / (z[2] - z[0]);
	float f123 = (f23 - f12) / (z[3] - z[1]);
	float f0123 = (f123 - f012) / (z[3] - z[0]);
	float4 polynomial;
	// f012+f0123 *(z-z2)
	polynomial[0] = mad(-f0123, z[2], f012);
	polynomial[1] = f0123;
	// *(z-z1) +f01
	polynomial[2] = polynomial[1];
	polynomial[1] = mad(polynomial[1], -z[1], polynomial[0]);
	polynomial[0] = mad(polynomial[0], -z[1], f01);
	// *(z-z0) +f0
	polynomial[3] = polynomial[2];
	polynomial[2] = mad(polynomial[2], -z[0], polynomial[1]);
	polynomial[1] = mad(polynomial[1], -z[0], polynomial[0]);
	polynomial[0] = mad(polynomial[0], -z[0], f0);
	float absorbance = dot(polynomial, float4 (1.0, b[0], b[1], b[2]));
	// Turn the normalized absorbance into transmittance
	return saturate(exp(-b_0 * absorbance));
}


