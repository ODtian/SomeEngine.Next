using System.Net;

namespace SomeEngine.Physics.Research;

internal static class ViewerServer
{
    public static void Run(string directory, int port)
    {
        string json = File.ReadAllText(Path.Combine(directory, "comparison.json"));
        string viewer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Viewer.html"));
        File.WriteAllText(Path.Combine(directory, "comparison.html"), viewer.Replace("/*COMPARISON_DATA*/null", json));
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"http://127.0.0.1:{port}/");
        while (true)
        {
            HttpListenerContext context = listener.GetContext();
            string path = context.Request.Url!.AbsolutePath;
            string? file = path switch { "/" or "/comparison.html" => "comparison.html", "/comparison.json" => "comparison.json", _ => null };
            if (file is null) context.Response.StatusCode = 404;
            else
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file));
                context.Response.ContentType = file.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes);
            }
            context.Response.Close();
        }
    }
}
