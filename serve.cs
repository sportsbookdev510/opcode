using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Serve {
  static string Root;
  static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
    { ".html", "text/html; charset=utf-8" },
    { ".css", "text/css; charset=utf-8" },
    { ".js", "text/javascript; charset=utf-8" },
    { ".mjs", "text/javascript; charset=utf-8" },
    { ".png", "image/png" },
    { ".jpg", "image/jpeg" },
    { ".jpeg", "image/jpeg" },
    { ".webp", "image/webp" },
    { ".gif", "image/gif" },
    { ".svg", "image/svg+xml" },
    { ".ico", "image/x-icon" },
    { ".woff2", "font/woff2" },
    { ".woff", "font/woff" },
    { ".ttf", "font/ttf" },
    { ".pdf", "application/pdf" },
    { ".json", "application/json; charset=utf-8" },
    { ".txt", "text/plain; charset=utf-8" },
    { ".xml", "application/xml; charset=utf-8" },
    { ".map", "application/json; charset=utf-8" }
  };

  static int Main(string[] args) {
    int port = 4173;
    if (args.Length > 0) int.TryParse(args[0], out port);
    Root = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    ServicePointManager.DefaultConnectionLimit = 64;
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Console.WriteLine("opcode mirror at http://127.0.0.1:" + port + "/");
    while (true) {
      TcpClient client = listener.AcceptTcpClient();
      ThreadPool.QueueUserWorkItem(delegate(object state) { Handle((TcpClient)state); }, client);
    }
  }

  static void Handle(TcpClient client) {
    try {
      client.ReceiveTimeout = 20000;
      client.SendTimeout = 120000;
      NetworkStream stream = client.GetStream();
      byte[] raw = ReadMessage(stream);
      if (raw == null) return;
      int split = IndexOf(raw, new byte[] { 13, 10, 13, 10 }, 0);
      if (split < 0) return;
      string head = Encoding.ASCII.GetString(raw, 0, split);
      string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
      if (lines.Length == 0) return;
      string[] parts = lines[0].Split(' ');
      if (parts.Length < 2) return;
      string method = parts[0].ToUpperInvariant();
      string target = parts[1];
      var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      for (int i = 1; i < lines.Length; i++) {
        int colon = lines[i].IndexOf(':');
        if (colon <= 0) continue;
        headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
      }
      int bodyStart = split + 4;
      int have = raw.Length - bodyStart;
      int need = 0;
      if (headers.ContainsKey("Content-Length")) int.TryParse(headers["Content-Length"], out need);
      if (need < 0 || need > 20 * 1024 * 1024) { WriteStatus(stream, 413, "Payload too large"); return; }
      byte[] body = new byte[need];
      if (have > 0 && need > 0) Buffer.BlockCopy(raw, bodyStart, body, 0, Math.Min(have, need));
      int filled = Math.Min(have, need);
      while (filled < need) {
        int n = stream.Read(body, filled, need - filled);
        if (n <= 0) break;
        filled += n;
      }
      string path = target;
      string query = "";
      int q = target.IndexOf('?');
      if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q); }
      path = Uri.UnescapeDataString(path);
      if (ShouldProxy(path, headers)) Proxy(stream, method, path + query, headers, body);
      else if (method != "GET" && method != "HEAD") WriteStatus(stream, 405, "Method not allowed");
      else ServeFile(stream, method, path);
    } catch (Exception ex) {
      try { WriteStatus(client.GetStream(), 500, ex.Message); } catch { }
    } finally {
      try { client.Close(); } catch { }
    }
  }

  static bool ShouldProxy(string path, Dictionary<string, string> headers) {
    if (path == "/ops" || path.StartsWith("/ops/") || path.StartsWith("/paper-source")) return false;
    if (path.StartsWith("/api/") && !path.StartsWith("/api/launch/images/")) return true;
    string[] names = new string[] { "RSC", "Next-Router-State-Tree", "Next-Router-Prefetch", "Next-Router-Segment-Prefetch" };
    foreach (string name in names) if (headers.ContainsKey(name)) return true;
    return false;
  }

  static void ServeFile(NetworkStream stream, string method, string urlPath) {
    string file = Resolve(urlPath);
    if (file == null) { WriteStatus(stream, 404, "Not found"); return; }
    string ext = Path.GetExtension(file).ToLowerInvariant();
    if (ext == ".ps1" || ext == ".cs" || ext == ".exe") { WriteStatus(stream, 404, "Not found"); return; }
    byte[] bytes = File.ReadAllBytes(file);
    string type = Types.ContainsKey(ext) ? Types[ext] : Sniff(bytes);
    var head = new StringBuilder();
    head.Append("HTTP/1.1 200 OK\r\n");
    head.Append("Content-Type: ").Append(type).Append("\r\n");
    head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
    head.Append("Cache-Control: no-cache\r\n");
    head.Append("Connection: close\r\n\r\n");
    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
    stream.Write(headBytes, 0, headBytes.Length);
    if (method != "HEAD") stream.Write(bytes, 0, bytes.Length);
  }

  static string Resolve(string urlPath) {
    string rel = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    if (rel.IndexOf("..") >= 0) return null;
    if (rel.Length == 0) rel = "index.html";
    string full = Path.GetFullPath(Path.Combine(Root, rel));
    string rootCheck = Root.TrimEnd('\\') + "\\";
    if (!full.StartsWith(rootCheck, StringComparison.OrdinalIgnoreCase)) return null;
    if (File.Exists(full)) return full;
    string index = Path.Combine(full, "index.html");
    if (File.Exists(index)) return index;
    if (File.Exists(full + ".html")) return full + ".html";
    string dir = Path.GetDirectoryName(full);
    string name = Path.GetFileName(full);
    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
      foreach (string candidate in Directory.GetFiles(dir)) {
        if (string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.OrdinalIgnoreCase))
          return candidate;
      }
    }
    return null;
  }

  static string Sniff(byte[] bytes) {
    if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50) return "image/png";
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
    if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[8] == 0x57) return "image/webp";
    if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50) return "application/pdf";
    if (bytes.Length >= 1 && (bytes[0] == '{' || bytes[0] == '[')) return "application/json; charset=utf-8";
    if (bytes.Length >= 1 && bytes[0] == '<') return "text/html; charset=utf-8";
    return "application/octet-stream";
  }

  static void Proxy(NetworkStream stream, string method, string pathAndQuery, Dictionary<string, string> headers, byte[] body) {
    var req = (HttpWebRequest)WebRequest.Create("https://opcode.so" + pathAndQuery);
    req.Method = method;
    req.AllowAutoRedirect = true;
    req.AutomaticDecompression = DecompressionMethods.None;
    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    req.Timeout = pathAndQuery.IndexOf("/stream") >= 0 ? System.Threading.Timeout.Infinite : 60000;
    req.ReadWriteTimeout = 120000;
    string[] forward = new string[] {
      "Accept", "Cookie", "RSC", "Next-Url", "Next-Router-State-Tree",
      "Next-Router-Prefetch", "Next-Router-Segment-Prefetch", "Accept-Language", "Content-Type"
    };
    foreach (string name in forward) {
      if (!headers.ContainsKey(name)) continue;
      try {
        if (name == "Accept") req.Accept = headers[name];
        else if (name == "Content-Type") req.ContentType = headers[name];
        else req.Headers[name] = headers[name];
      } catch { }
    }
    if (body != null && body.Length > 0 && method != "GET" && method != "HEAD") {
      req.ContentLength = body.Length;
      using (Stream outStream = req.GetRequestStream()) outStream.Write(body, 0, body.Length);
    }
    HttpWebResponse resp = null;
    try {
      try { resp = (HttpWebResponse)req.GetResponse(); }
      catch (WebException ex) { resp = (HttpWebResponse)ex.Response; }
      if (resp == null) { WriteStatus(stream, 502, "Upstream failed"); return; }
      var head = new StringBuilder();
      head.Append("HTTP/1.1 ").Append((int)resp.StatusCode).Append(" ").Append(resp.StatusDescription).Append("\r\n");
      foreach (string key in resp.Headers.AllKeys) {
        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
        if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
        if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (string value in resp.Headers.GetValues(key)) {
          head.Append(key).Append(": ").Append(value).Append("\r\n");
        }
      }
      head.Append("Connection: close\r\n\r\n");
      byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
      stream.Write(headBytes, 0, headBytes.Length);
      if (method != "HEAD") {
        using (Stream upstream = resp.GetResponseStream()) {
          byte[] buf = new byte[8192];
          int n;
          while ((n = upstream.Read(buf, 0, buf.Length)) > 0) stream.Write(buf, 0, n);
        }
      }
    } finally {
      if (resp != null) resp.Close();
    }
  }

  static void WriteStatus(NetworkStream stream, int code, string message) {
    byte[] body = Encoding.UTF8.GetBytes(message);
    string reason = code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : code == 413 ? "Payload Too Large" : "Error";
    string head = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
    byte[] headBytes = Encoding.ASCII.GetBytes(head);
    stream.Write(headBytes, 0, headBytes.Length);
    stream.Write(body, 0, body.Length);
  }

  static byte[] ReadMessage(NetworkStream stream) {
    var ms = new MemoryStream();
    byte[] buf = new byte[8192];
    while (ms.Length < 64 * 1024) {
      int n = stream.Read(buf, 0, buf.Length);
      if (n <= 0) break;
      ms.Write(buf, 0, n);
      if (IndexOf(ms.ToArray(), new byte[] { 13, 10, 13, 10 }, 0) >= 0) break;
    }
    if (ms.Length == 0) return null;
    return ms.ToArray();
  }

  static int IndexOf(byte[] data, byte[] needle, int start) {
    for (int i = start; i <= data.Length - needle.Length; i++) {
      bool ok = true;
      for (int j = 0; j < needle.Length; j++) if (data[i + j] != needle[j]) { ok = false; break; }
      if (ok) return i;
    }
    return -1;
  }
}
