using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Data.Json;

namespace WpBlueBubbles.Services
{
    public sealed class GitHubReleaseInfo
    {
        public Version Version { get; set; }
        public string TagName { get; set; }
        public string ReleaseUrl { get; set; }
        public string BundleUrl { get; set; }
        public string BundleName { get; set; }
        public long BundleSize { get; set; }
    }

    public sealed class GitHubUpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/RetiredLake/LiveBubbles/releases/latest";

        public async Task<GitHubReleaseInfo> GetLatestReleaseAsync()
        {
            using (var client = CreateClient())
            using (var response = await client.GetAsync(LatestReleaseUrl))
            {
                response.EnsureSuccessStatusCode();
                var root = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                var tag = root.GetNamedString("tag_name", "");
                Version version;
                if (!TryParseVersion(tag, out version)) throw new InvalidDataException("The latest release has an invalid version number.");

                var assets = new List<JsonObject>();
                foreach (var value in root.GetNamedArray("assets", new JsonArray()))
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    var candidate = value.GetObject();
                    if (candidate.GetNamedString("name", "").EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase)) assets.Add(candidate);
                }
                var expected = version.ToString(4).Replace('.', '_');
                var asset = assets.FirstOrDefault(item => item.GetNamedString("name", "").Contains(expected));
                if (asset == null && assets.Count == 1) asset = assets[0];
                if (asset == null) throw new InvalidDataException("The latest release does not contain a unique app bundle.");

                return new GitHubReleaseInfo
                {
                    Version = version,
                    TagName = tag,
                    ReleaseUrl = root.GetNamedString("html_url", ""),
                    BundleUrl = asset.GetNamedString("browser_download_url", ""),
                    BundleName = asset.GetNamedString("name", ""),
                    BundleSize = (long)asset.GetNamedNumber("size", 0)
                };
            }
        }

        public async Task<StorageFile> DownloadAsync(GitHubReleaseInfo release, IProgress<double> progress)
        {
            var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("Updates", CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync(release.BundleName, CreationCollisionOption.ReplaceExisting);
            using (var client = CreateClient())
            using (var response = await client.GetAsync(release.BundleUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.BundleSize;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = await file.OpenStreamForWriteAsync())
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        copied += read;
                        if (total > 0 && progress != null) progress.Report((double)copied / total);
                    }
                }
            }
            return file;
        }

        public async Task CleanupAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.TemporaryFolder.GetFolderAsync("Updates");
                foreach (var file in await folder.GetFilesAsync()) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (FileNotFoundException) { }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveBubbles/0.3.2.5");
            return client;
        }

        private static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var value = tag.Trim().TrimStart('v', 'V');
            Version parsed;
            if (!Version.TryParse(value, out parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }
    }
}
