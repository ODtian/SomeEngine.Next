using System.Net.WebSockets;
using System.Text.Json;

namespace SomeEngine.Clothing.Demo;

public static class DemoApplication
{
    public static async Task Main(string[] args)
    {
        if (args is ["--measure"])
        {
            MeasureClothing();
            return;
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:5784");
        builder.Logging.ClearProviders();
        var application = builder.Build();
        application.UseDefaultFiles();
        application.UseStaticFiles();
        application.UseWebSockets();
        application.Map("/motion", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            var scene = new ClothingScene();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(scene.Describe(), json),
                WebSocketMessageType.Text, true, context.RequestAborted);
            await socket.SendAsync(scene.Bytes, WebSocketMessageType.Binary, true, context.RequestAborted);
            byte[] input = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    int length = 0;
                    WebSocketReceiveResult message;
                    do
                    {
                        message = await socket.ReceiveAsync(new ArraySegment<byte>(input, length, input.Length - length), context.RequestAborted);
                        if (message.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            return;
                        }
                        length += message.Count;
                    } while (!message.EndOfMessage);
                    DemoControls controls = JsonSerializer.Deserialize<DemoControls>(input.AsSpan(0, length), json)!;
                    for (int step = 0; step < controls.FrameSteps; step++)
                        scene.Advance(step == 0 ? controls : controls with { Reset = false });
                    await socket.SendAsync(scene.Bytes, WebSocketMessageType.Binary, true, context.RequestAborted);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        Console.WriteLine("Clothing demo: http://127.0.0.1:5784");
        await application.RunAsync();
    }

    private static void MeasureClothing()
    {
        var scene = new ClothingScene();
        foreach (float progress in new[] { 0f, .35f, .75f })
        {
            var controls = new DemoControls { Running = .65f, Spread = .07f, Briefs = progress, Tights = progress };
            List<double> frameTimes = [], trajectoryTimes = [], clothingTimes = [], surfaceTimes = [];
            for (int frame = 0; frame < 360; frame++)
            {
                long frameTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                scene.Advance(controls);
                double frameTime = System.Diagnostics.Stopwatch.GetElapsedTime(frameTimestamp).TotalMilliseconds;
                if (frame < 240) continue;
                frameTimes.Add(frameTime); trajectoryTimes.Add(scene.SkinningMilliseconds); clothingTimes.Add(scene.SimulationMilliseconds);
                surfaceTimes.Add(scene.SurfaceMilliseconds);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Progress = progress, WarmupFrames = 240, Frames = frameTimes.Count,
                ClothingScene.StepSeconds, ClothingScene.Substeps, ClothingScene.SolverIterations,
                Vertices = scene.Briefs.Positions.Length + scene.Tights.Positions.Length,
                BodyVertices = scene.BodyGeometry.Positions.Length, BodyTriangles = scene.BodyGeometry.Triangles.Length / 3,
                BodyContact = "skinned body surface and 50-plane feet",
                FrameMilliseconds = CalculateMedian(frameTimes), TrajectoryMilliseconds = CalculateMedian(trajectoryTimes), ClothingMilliseconds = CalculateMedian(clothingTimes),
                SurfaceMilliseconds = CalculateMedian(surfaceTimes),
            }));
        }

        static double CalculateMedian(List<double> values)
        {
            values.Sort();
            int middle = values.Count / 2;
            return Math.Round((values[middle - 1] + values[middle]) * .5, 3);
        }
    }
}
