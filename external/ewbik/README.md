# EWBIK attribution

The weighted descendant-heading CCD, pin falloff and damped joint updates in
`SomeEngine.Animation.Pose/EwbikRig.cs` and `EwbikSamples.cs` are adapted from
[Everything-Will-Be-IK](https://github.com/EGjoni/Everything-Will-Be-IK),
commit `acc3383ed2603a53b92e272847d6a6af8c557013`, by Eron Gjoni.
The original files are `SegmentedArmature.java`, `AbstractIKPin.java` and `QCP.java`.
The MIT copyright and permission notice is retained in [LICENSE](LICENSE).

The engine's `EwbikRotationFitting` solves the weighted quaternion eigenproblem
with symmetric Jacobi rotations. The upstream QCP reference credits Douglas L.
Theobald and Pu Liu for the original C formulation, Peter Rose and Aleix Lafita
for the Java adaptation, and Eron Gjoni for the EWBIK adaptation.
