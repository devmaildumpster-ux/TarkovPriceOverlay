using System.Text.Json;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Maps.Data;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class DataServiceTests
{
    private sealed class FakeFeed : IFeedSource
    {
        public int Calls;
        public bool Fail;
        public Task<JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("offline");
            var name = path.Split('/').Last() switch
            {
                "maps" => "maps_sample.json", "maps_en" => "maps_en_sample.json", "tasks" => "tasks_sample.json",
                "tasks_en" => "tasks_en_sample.json", "traders_en" => "traders_en_sample.json", var x => throw new InvalidOperationException(x),
            };
            return Task.FromResult(Fx.Doc(name));
        }
    }

    [Fact]
    public async Task Downloads_caches_and_falls_back()
    {
        var root = Directory.CreateTempSubdirectory("mapdata");
        try
        {
            var paths = new MapPaths(root.FullName);
            var feed = new FakeFeed();
            int getText = 0;
            var svc = new MapDataService(paths, feed, (u, ct) => { getText++; return Task.FromResult(DefinitionsJson); }, _ => { });
            var defs = await svc.GetDefinitionsAsync(CancellationToken.None);
            Assert.Contains(defs, d => d.Key == "customs");
            Assert.True(File.Exists(paths.DefinitionsFile));
            var m = await svc.GetMapFeedAsync("pve", CancellationToken.None);
            var t = await svc.GetTaskFeedAsync("pve", CancellationToken.None);
            Assert.NotNull(m); Assert.NotNull(t);
            Assert.True(File.Exists(paths.MapFeedFile("pve")) && File.Exists(paths.TaskFeedFile("pve")));
            int calls = feed.Calls;

            // a fresh service reads the caches without network
            var svc2 = new MapDataService(paths, new FakeFeed { Fail = true }, (u, ct) => throw new HttpRequestException("offline"), _ => { });
            Assert.Equal(m!.Maps.Count, (await svc2.GetMapFeedAsync("pve", CancellationToken.None))!.Maps.Count);
            Assert.Equal(t!.Tasks.Count, (await svc2.GetTaskFeedAsync("pve", CancellationToken.None))!.Tasks.Count);
            Assert.Contains(await svc2.GetDefinitionsAsync(CancellationToken.None), d => d.Key == "customs");
            // forced refresh while offline keeps the last good copy and reports the problem
            Assert.NotNull(await svc2.GetMapFeedAsync("pve", CancellationToken.None, force: true));
            Assert.NotNull(svc2.LastProblem);
            Assert.Equal(1, getText);
            Assert.True(calls >= 5);

            // nothing cached and offline: the embedded drawings still load
            var empty = new MapDataService(new MapPaths(Path.Combine(root.FullName, "x")), new FakeFeed { Fail = true }, (u, ct) => throw new HttpRequestException("offline"), _ => { });
            Assert.Contains(await empty.GetDefinitionsAsync(CancellationToken.None), d => d.Key == "woods");
            Assert.Null(await empty.GetMapFeedAsync("pve", CancellationToken.None));
        }
        finally { root.Delete(true); }
    }

    private static string DefinitionsJson => new StreamReader(typeof(MapDataService).Assembly.GetManifestResourceStream("TarkovOverlay.Maps.maps.fallback.json")!).ReadToEnd();
}
