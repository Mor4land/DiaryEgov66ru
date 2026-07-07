using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace MirageDiary.Server.Controllers;

/// <summary>
/// Reverse-proxy for dnevnik.egov66.ru.
/// Strips X-Frame-Options and CSP so the site can be embedded in an iframe.
/// Injects a polling script into every HTML page that extracts
/// Auth__token, Auth__refresh and studentId from localStorage/API
/// and posts them to the parent frame via postMessage.
/// </summary>
[Route("diary-proxy")]
public class DiaryProxyController : ControllerBase
{
    private const string DiaryBase = "https://dnevnik.egov66.ru";

    private readonly IHttpClientFactory _httpFactory;

    public DiaryProxyController(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    // Catch-all: GET diary-proxy  +  GET diary-proxy/{*path}
    [HttpGet]
    [HttpGet("{*path}")]
    public Task ProxyGet(string path = "") => Forward(HttpMethod.Get, path);

    [HttpPost("{*path}")]
    public Task ProxyPost(string path = "") => Forward(HttpMethod.Post, path);

    // ─────────────────────────────────────────────────────────────────────────

    private async Task Forward(HttpMethod method, string path)
    {
        var client = _httpFactory.CreateClient("diary-proxy");

        var targetUri = $"{DiaryBase}/{path}{Request.QueryString}";

        using var req = new HttpRequestMessage(method, targetUri);

        // Forward safe request headers (skip Host, which will be set by HttpClient)
        foreach (var h in Request.Headers)
        {
            var key = h.Key;
            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(key, h.Value.ToArray());
        }

        // Forward request body for POST
        if (method == HttpMethod.Post && Request.ContentLength > 0)
        {
            req.Content = new StreamContent(Request.Body);
            if (Request.ContentType != null)
                req.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
        }

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Response.StatusCode = (int)resp.StatusCode;

        // ── Copy response headers, blocking embedding-prevention ones ─────────
        foreach (var h in resp.Headers)
        {
            var key = h.Key;
            if (string.Equals(key, "X-Frame-Options", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            Response.Headers.TryAdd(key, h.Value.ToArray());
        }
        foreach (var h in resp.Content.Headers)
        {
            var key = h.Key;
            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            Response.Headers.TryAdd(key, h.Value.ToArray());
        }

        // ── Rewrite Location header on redirects ──────────────────────────────
        if (resp.Headers.Location != null)
        {
            var loc = resp.Headers.Location.ToString();
            // Only rewrite same-domain redirects through our proxy
            if (loc.StartsWith(DiaryBase, StringComparison.OrdinalIgnoreCase))
            {
                var relative = loc[DiaryBase.Length..].TrimStart('/');
                loc = $"/diary-proxy/{relative}";
            }
            Response.Headers["Location"] = loc;
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";

        if (contentType.Contains("text/html"))
        {
            // Read, inject script, return modified HTML
            var html = await resp.Content.ReadAsStringAsync();
            html = InjectTokenExtractor(html);
            Response.ContentType = "text/html; charset=utf-8";
            await Response.WriteAsync(html, Encoding.UTF8);
        }
        else
        {
            Response.ContentType = contentType;
            await resp.Content.CopyToAsync(Response.Body);
        }
    }

    // ─── Injected script ──────────────────────────────────────────────────────
    private static string InjectTokenExtractor(string html)
    {
        const string script = @"
<script id=""__mirage_extractor"">
(function() {
  'use strict';

  function tryExtract() {
    try {
      var token   = localStorage.getItem('Auth__token');
      var refresh = localStorage.getItem('Auth__refresh') || '';

      if (!token || token.length < 20) {
        setTimeout(tryExtract, 1500);
        return;
      }

      // Fetch student info from diary API (same origin — no CORS issues here)
      fetch('https://dnevnik.egov66.ru/api/students', {
        headers: {
          'Authorization': 'Bearer ' + token,
          'Accept': 'application/json'
        }
      })
      .then(function(r) { return r.json(); })
      .then(function(data) {
        var studentId = '';
        var className = '';
        var orgName   = '';

        if (data && data.students && data.students.length > 0) {
          var s = data.students[0];
          studentId = s.id        || '';
          className = s.className || '';
          orgName   = s.orgName   || '';
        }

        window.parent.postMessage({
          source:    'miragediary',
          token:     token,
          refresh:   refresh,
          studentId: studentId,
          className: className,
          orgName:   orgName
        }, '*');
      })
      .catch(function() {
        // API failed — send at least the tokens
        window.parent.postMessage({
          source:    'miragediary',
          token:     token,
          refresh:   refresh,
          studentId: '',
          className: '',
          orgName:   ''
        }, '*');
      });

    } catch(e) {
      setTimeout(tryExtract, 1500);
    }
  }

  // Start polling after a short delay (let the app initialize)
  setTimeout(tryExtract, 2000);
})();
</script>
";
        if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
            return html.Replace("</body>", script + "</body>", StringComparison.OrdinalIgnoreCase);

        return html + script;
    }
}
