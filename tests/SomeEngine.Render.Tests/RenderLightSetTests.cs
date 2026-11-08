using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderLightSetTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public void LightValuesCookiesAndMembershipChangeWithoutUnrelatedWorldChangesAdvancingTheSet()
    {
        using var world = new RenderWorld();
        var first = new RenderPointLight(Vector3.Zero, 10, Vector3.One, 1, uint.MaxValue);
        var second = first with { Position = Vector3.UnitX };
        var one = world.CreateEntity(first);
        var two = world.CreateEntity(second);
        var cookie = new RenderLightCookie(null!, 0.7f, Vector4.One, Matrix4x4.Identity);
        world.Add(two, cookie);
        var lights = new RenderLightSetSystem();
        using var extraction = new RenderExtractionSystems(world);
        using var systems = new RenderPrepareSystems(world, extraction);
        systems.Add(lights);

        try
        {
            Update();
            RenderLightSet values = lights.RequireCurrent();
            Assert.Equal(2, values.Points.Count);
            Assert.Contains(first, values.Points);
            Assert.Contains(second, values.Points);
            ulong revision = values.Revision;
            var unrelated = world.CreateEntity(new UnrelatedValue(1));
            Update();
            Assert.Equal(revision, values.Revision);

            second = second with { Intensity = 3 };
            world.AcquireSystemVersion();
            world.Replace(two, second);
            Update();
            Assert.Equal(revision + 1, values.Revision);
            Assert.Contains(first, values.Points);
            int secondIndex = values.Points.ToList().IndexOf(second);
            Assert.True(secondIndex >= 0);
            Assert.Equal(cookie, values.PointCookies[secondIndex]);

            world.Remove<RenderLightCookie>(two);
            world.DestroyEntity(one);
            world.Replace(unrelated, new UnrelatedValue(2));
            Update();
            Assert.Equal(second, Assert.Single(values.Points));
            Assert.Null(Assert.Single(values.PointCookies));

            world.Remove<RenderPointLight>(two);
            var spot = new RenderSpotLight(Vector3.Zero, 20, -Vector3.UnitZ, 0.9f, 0.7f,
                Vector3.One, 2, uint.MaxValue);
            world.Add(two, spot);
            Update();
            Assert.Empty(values.Points);
            Assert.Equal(spot, Assert.Single(values.Spots));
            revision = values.Revision;
            Update();
            Assert.Equal(revision, values.Revision);

            void Update()
            {
                Assert.True(graphics.Coordinator.TryBeginPrepare(out var scope));
                using (scope)
                {
                    systems.Update(scope);
                    scope.Commit();
                }
            }
        }
        finally
        {
            Assert.True(graphics.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                systems.Shutdown(scope);
                scope.Commit();
            }
        }
    }

    private readonly record struct UnrelatedValue(int Value) : IComponent;
}
