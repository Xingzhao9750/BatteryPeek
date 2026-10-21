using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Minimal TCP-based static server (no HTTP.sys, so no URL ACL / admin needed).
// Serves index.html and appends every POST /log body to log.txt.
internal static class WebServer
{
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string LogPath = Path.Combine(Root, "log.txt");
    private static readonly object Gate = new object();

    private static int Main(string[] args)
    {
        int port = args.Length > 0 ? int.Parse(args[0]) : 8971;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine("serving http://127.0.0.1:" + port + "/");

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private static void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 5000;

                var header = new StringBuilder();
                var buffer = new byte[4096];
                int contentLength = 0;

                // Read headers byte by byte so we never swallow the body.
                while (true)
                {
                    int read = stream.Read(buffer, 0, 1);
                    if (read <= 0) return;
                    header.Append((char)buffer[0]);

                    string text = header.ToString();
                    if (text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                    if (text.Length > 16384) return;

                    if (text.EndsWith("\r\n", StringComparison.Ordinal))
                    {
                        string lastLine = text.Substring(0, text.Length - 2);
                        int start = lastLine.LastIndexOf("\r\n", StringComparison.Ordinal);
                        string line = start >= 0 ? lastLine.Substring(start + 2) : lastLine;
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                    }
                }

                string headerText = header.ToString();
                string requestLine = headerText.Substring(0, headerText.IndexOf("\r\n", StringComparison.Ordinal));
                string[] parts = requestLine.Split(' ');
                string method = parts[0];
                string path = parts.Length > 1 ? parts[1] : "/";
                int queryIndex = path.IndexOf('?');
                if (queryIndex >= 0) path = path.Substring(0, queryIndex);

                string body = string.Empty;
                if (contentLength > 0)
                {
                    var bodyBytes = new byte[contentLength];
                    int offset = 0;
                    while (offset < contentLength)
                    {
                        int read = stream.Read(bodyBytes, offset, contentLength - offset);
                        if (read <= 0) break;
                        offset += read;
                    }

                    body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
                }

                if (method == "POST" && path == "/log")
                {
                    lock (Gate)
                    {
                        File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " " + body + Environment.NewLine, Encoding.UTF8);
                    }

                    Write(stream, 204, "text/plain", string.Empty);
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    string file = Path.Combine(Root, "index.html");
                    Write(stream, 200, "text/html; charset=utf-8", File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "missing index.html");
                    return;
                }

                if (path == "/log")
                {
                    lock (Gate)
                    {
                        Write(stream, 200, "text/plain; charset=utf-8", File.Exists(LogPath) ? File.ReadAllText(LogPath, Encoding.UTF8) : string.Empty);
                    }
                    return;
                }

                Write(stream, 404, "text/plain", "not found");
            }
        }
        catch (Exception)
        {
            // A browser closing a socket must never take the server down.
        }
    }

    private static void Write(NetworkStream stream, int status, string contentType, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 404 ? "Not Found" : "Error";
        string head = "HTTP/1.1 " + status + " " + reason + "\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + payload.Length + "\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
