using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Whikiey.SiteClone
{
    public class SiteCrawler
    {
        #region Private Fields
        private readonly ConcurrentDictionary<string, int> downloadingUrls = new();
        private readonly ConcurrentQueue<string> downloadQueue = new();
        private readonly Regex safeFileNameRegex = new(@"[\?\&\|\:\/\;\*\""\\\#\<\>]");
        private readonly Regex regexStyle = new(@"\burl\s*\(\s*(\'|\""|)([^\'\""\)]+)\1\s*\)\s*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private readonly Dictionary<string, string> mimeTypeExtensions = new()
        {
            { "text/html", ".html" },
            { "text/javascript", ".js" },
            { "text/css", ".css" },
            { "image/png", ".png" },
            { "image/jpeg", ".jpg" },
            { "image/gif", ".gif" },
        };
        #endregion

        public Uri SiteRoot { get; }
        public string DestSiteRoot { get; }

        public SiteCrawler(string siteRoot, string destRoot)
        {
            siteRoot = siteRoot.EndsWith('/') ? siteRoot : siteRoot + "/";
            SiteRoot = new Uri(siteRoot);
            DestSiteRoot = Path.Combine(destRoot, GetSafeFileNameWithoutExtention(siteRoot));
        }

        public async Task DownloadAsync()
        {
            downloadQueue.Enqueue(SiteRoot.ToString());
            while (!downloadQueue.IsEmpty)
            {
                downloadQueue.TryDequeue(out var url);
                await DownloadUrlAsync(url);
            }
        }

        private string GetSafeFileNameWithoutExtention(string siteRoot) => safeFileNameRegex.Replace(siteRoot, "_");

        private async Task DownloadUrlAsync(string url)
        {
            downloadingUrls.TryAdd(url, 0);
            downloadingUrls[url]++;
            Uri uri = new(url);
            using HttpClient client = new();
            try
            {
                using var respMsg = await client.GetAsync(uri);

                if (respMsg.IsSuccessStatusCode)
                {
                    var localPath = GetLocalPath(uri).Replace('/', Path.DirectorySeparatorChar);
                    var fileName = Path.Combine(DestSiteRoot, localPath);
                    var dir = Path.GetDirectoryName(fileName);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var modTime = respMsg.Headers.Date;

                    ParseContentType(respMsg.Content, out var mimeType, out var encoding);

                    using var stream = await respMsg.Content.ReadAsStreamAsync();
                    var urls = new List<string>();
                    if (mimeType == "text/html" || fileName.EndsWith(".html") || fileName.EndsWith(".htm"))
                        await SaveHtmlAsync(url, stream, urls, fileName, modTime);
                    else if (mimeType == "text/css" || fileName.EndsWith(".css"))
                        await SaveCssAsync(url, stream, encoding, urls, fileName, modTime);
                    else
                        await SaveOthersAsync(stream, fileName, modTime);

                    foreach (var u in urls)
                        if (IsSiteUrl(u))
                            downloadQueue.Enqueue(u);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                if (downloadingUrls[url] <= 3)
                    downloadQueue.Enqueue(url);
            }
        }

        private async Task SaveOthersAsync(Stream stream, string fileName, DateTimeOffset? modTime)
        {
            var buf = new byte[4096];
            using var fs = File.Create(fileName);
            int readCount = 0;
            while ((readCount = await stream.ReadAsync(buf.AsMemory(0, buf.Length))) > 0)
                await fs.WriteAsync(buf.AsMemory(0, readCount));

            await fs.FlushAsync();
            fs.Close();
            if (modTime != null)
                new FileInfo(fileName).LastWriteTime = modTime.Value.DateTime;
        }

        private async Task SaveCssAsync(string url, Stream stream, Encoding encoding, List<string> urls, string fileName, DateTimeOffset? modTime)
        {
            using StreamReader sr = new(stream, encoding);
            var style = await sr.ReadToEndAsync();
            var newStyle = ReplaceStyleUrl(url, urls, style);
            using StreamWriter writer = new(fileName);
            await writer.WriteAsync(newStyle);
            await writer.FlushAsync();
            writer.Close();
            if (modTime != null)
                new FileInfo(fileName).LastWriteTime = modTime.Value.DateTime;
        }

        private async Task SaveHtmlAsync(string url, Stream stream, List<string> urls, string fileName, DateTimeOffset? modTime)
        {
            var buf = new byte[4096];
            using var ms = new MemoryStream();
            int readCount = 0;
            while ((readCount = await stream.ReadAsync(buf.AsMemory(0, buf.Length))) > 0)
                await ms.WriteAsync(buf.AsMemory(0, readCount));
            ms.Seek(0, SeekOrigin.Begin);

            HtmlDocument doc = new();
            var encoding = doc.DetectEncoding(ms);
            var html = encoding.GetString(ms.ToArray());
            doc.LoadHtml(html);
            var d = doc.DocumentNode;
            var links = d.SelectNodes("//link");
            var anchors = d.SelectNodes("//a");
            var imgs = d.SelectNodes("//img");
            var scripts = d.SelectNodes("//script");
            var styles = d.SelectNodes("//@style");

            TakeUrl(url, urls, "href", links, anchors);
            TakeUrl(url, urls, "src", scripts, imgs);
            TakeStyleUrl(url, urls, styles);

            var newHtml = doc.DocumentNode.OuterHtml;
            using StreamWriter writer = new(fileName);
            await writer.WriteAsync(newHtml);
            await writer.FlushAsync();
            writer.Close();
            if (modTime != null)
                new FileInfo(fileName).LastWriteTime = modTime.Value.DateTime;
        }

        private static void ParseContentType(HttpContent content, out string mimeType, out Encoding encoding)
        {
            mimeType = null;
            encoding = Encoding.UTF8;
            var contentType = content.Headers.GetValues("Content-Type").FirstOrDefault();
            if (contentType == null)
                return;
            var cts = contentType.Split(";", StringSplitOptions.RemoveEmptyEntries);
            if (cts.Length == 0)
                return;

            mimeType = cts[0];
            if (cts.Length < 2)
                return;

            var addtional = cts[1].Trim().Split("=", StringSplitOptions.RemoveEmptyEntries);
            if (addtional.Length != 2)
                return;

            if (addtional[0] != "charset")
                return;

            var charset = addtional[1];
            try { encoding = Encoding.GetEncoding(charset); } catch { }
        }

        private void TakeStyleUrl(string thisUrl, List<string> urls, HtmlNodeCollection nodes)
        {
            if (nodes == null)
                return;
            Array.ForEach(nodes.ToArray(), n =>
            {
                var attr = n.Attributes["style"];
                if (attr != null && attr.Value != null)
                    attr.Value = ReplaceStyleUrl(thisUrl, urls, attr.Value);
            });
        }

        private string ReplaceStyleUrl(string thisUrl, List<string> urls, string style)
        {
            var newStyle = regexStyle.Replace(style, m =>
            {
                var uri = new Uri(SiteRoot, m.Groups[2].Value);
                var url = uri.ToString();
                if (!IsSiteUrl(url))
                    return m.Value;

                var relUri = new Uri(SiteRoot, GetLocalPath(new Uri(thisUrl))).MakeRelativeUri(new Uri(SiteRoot, GetLocalPath(uri)));
                urls.Add(url);
                var u = relUri.ToString();
                return $"url('{u}')";
            });
            return newStyle;
        }

        private void TakeUrl(string thisUrl, List<string> urls, string attrName, params HtmlNodeCollection[] nodeArrs)
        {
            foreach (var nodes in nodeArrs)
            {
                if (nodes == null)
                    continue;
                Array.ForEach(nodes.ToArray(), n =>
                {
                    var attr = n.Attributes[attrName];
                    if (attr != null && attr.Value != null)
                    {
                        var uri = new Uri(SiteRoot, attr.Value);
                        var relUri = new Uri(SiteRoot, GetLocalPath(new Uri(thisUrl))).MakeRelativeUri(new Uri(SiteRoot, GetLocalPath(uri)));
                        attr.Value = relUri.ToString();
                        urls.Add(uri.ToString());
                    }
                });
            }
        }

        private string GetLocalPath(Uri uri)
        {
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return uri.ToString();
            var segs = uri.Segments.Select(s => HttpUtility.UrlDecode(s.TrimEnd('/'))).ToArray();
            var fileName = segs[^1];
            if (fileName.EndsWith("/") || Path.GetExtension(fileName) == string.Empty)
            {
                fileName = "index.html";
                var newSegs = new string[segs.Length + 1];
                Array.Copy(segs, newSegs, segs.Length);
                newSegs[^1] = fileName;
                segs = newSegs;
            }

            var extName = Path.GetExtension(fileName);
            var fileWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            segs[^1] = GetSafeFileNameWithoutExtention(fileWithoutExt + uri.Query) + extName;
            return string.Join("/", segs)[SiteRoot.LocalPath.Length..];
        }

        private bool IsSiteUrl(string url) => url.StartsWith(SiteRoot.ToString());
    }
}