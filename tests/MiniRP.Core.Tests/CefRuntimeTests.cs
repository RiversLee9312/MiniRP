using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using MiniRP.Cli;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class CefRuntimeTests
{
    private DirectoryInfo fixture = null!;
    private string Root => fixture.FullName;

    [TestInitialize]
    public void CreateFixture() => fixture = Directory.CreateTempSubdirectory("minirp-cef-fixture-");

    [TestCleanup]
    public void CleanFixture() => fixture.Delete(recursive: true);

    private sealed class FixtureTransfer(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    [TestMethod]
    public async Task InterruptedTransferResumesAtTheValidatedOffset()
    {
        const string segment = "runtime-";
        var payload = "runtime-archive"u8.ToArray();
        var release = new CefRuntimeRelease("fixture", "linux64", payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)), "");
        int calls = 0;
        using var client = new HttpClient(new FixtureTransfer(request =>
        {
            if (calls++ == 0)
            {
                Assert.AreEqual(0L, request.Headers.Range!.Ranges.Single().From);
                var body = new ByteArrayContent(payload[..segment.Length]);
                body.Headers.ContentLength = payload.Length;
                return new(HttpStatusCode.OK) { Content = body };
            }
            var range = request.Headers.Range!.Ranges.Single();
            Assert.AreEqual((long)segment.Length, range.From);
            Assert.AreEqual((long)payload.Length - 1, range.To);
            var trailing = new ByteArrayContent(payload[segment.Length..]);
            trailing.Headers.ContentRange = new(segment.Length, payload.Length - 1, payload.Length);
            return new(HttpStatusCode.PartialContent) { Content = trailing };
        }));
        var phases = new List<CefInstallProgress>();
        var path = Path.Combine(Root, "download");
        await CefRuntimeInstaller.DownloadAsync(release, client, path, phases.Add, CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(2, calls);
        Assert.AreEqual("verifying", phases[^1].Phase);
        Assert.AreEqual((long)payload.Length, phases[^1].BytesReceived);
    }

    [TestMethod]
    public async Task DownloadRejectsPayloadWithTheRightLengthButWrongDigest()
    {
        var release = new CefRuntimeRelease("fixture", "linux64", 8, new string('0', 64), "");
        using var client = new HttpClient(new FixtureTransfer(_ =>
        {
            var body = new ByteArrayContent("corrupt!"u8.ToArray());
            body.Headers.ContentRange = new(0, release.DownloadBytes - 1, release.DownloadBytes);
            return new(HttpStatusCode.PartialContent) { Content = body };
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CefRuntimeInstaller.DownloadAsync(release, client, Path.Combine(Root, "download"), _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExtractionRejectsTraversalLinksAndFlatteningCollisions()
    {
        using var traversal = Archive(FileEntry("fixture/Release/../outside.dll"));
        using var link = Archive(new PaxTarEntry(TarEntryType.SymbolicLink, "fixture/Release/library.so")
            { LinkName = "payload.so" });
        using var duplicate = Archive(FileEntry("fixture/Release/shared.pak"), FileEntry("fixture/Resources/Shared.pak"));
        foreach (var (source, name) in new[] { (traversal, "traversal"), (link, "link"), (duplicate, "duplicate") })
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, name));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                CefRuntimeInstaller.ExtractAsync(source, "fixture", folder.FullName, CancellationToken.None));
        }
        Assert.IsFalse(File.Exists(Path.Combine(Root, "outside.dll")));
    }

    [TestMethod]
    public async Task ExtractionKeepsPayloadAndVerifiesItsDigestOnReuse()
    {
        using var source = Archive(FileEntry("fixture/Release/libcef.so"),
            FileEntry("fixture/Release/libcef.a"), FileEntry("fixture/include/cef.h"),
            FileEntry("fixture/Resources/locales/en-US.pak"), FileEntry("fixture/LICENSE.txt"));
        await CefRuntimeInstaller.ExtractAsync(source, "fixture", Root, CancellationToken.None);
        Assert.IsTrue(File.Exists(Path.Combine(Root, "locales", "en-US.pak")));
        Assert.IsFalse(File.Exists(Path.Combine(Root, "libcef.a")));
        Assert.IsFalse(Directory.Exists(Path.Combine(Root, "include")));
        var digest = await CefRuntimeInstaller.RuntimeDigestAsync(Root, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(Root, "libcef.so"), "tampered");
        var modified = await CefRuntimeInstaller.RuntimeDigestAsync(Root, CancellationToken.None);
        Assert.AreNotEqual(digest, modified);
    }

    private static PaxTarEntry FileEntry(string name) => new(TarEntryType.RegularFile, name)
        { DataStream = new MemoryStream("fixture"u8.ToArray()) };

    private static MemoryStream Archive(params TarEntry[] entries)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var data = entry.DataStream;
                writer.WriteEntry(entry);
            }
        stream.Position = 0;
        return stream;
    }
}
