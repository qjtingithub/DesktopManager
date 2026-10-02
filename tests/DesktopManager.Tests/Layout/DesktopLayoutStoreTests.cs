using System.Text.Json;
using DesktopManager.Layout;

namespace DesktopManager.Tests.Layout;

public sealed class DesktopLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DesktopManager.LayoutTests",
        Guid.NewGuid().ToString("N"));

    public DesktopLayoutStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void DefaultFilePathUsesLocalApplicationDataDesktopManagerDirectory()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopManager",
            "desktop-layout.json");

        Assert.Equal(expected, DesktopLayoutStore.DefaultFilePath);
    }

    [Fact]
    public void ConstructorNormalizesPathAndRejectsBlankPath()
    {
        Assert.Throws<ArgumentException>(() => new DesktopLayoutStore("  "));

        string relativePath = Path.Combine(_directory, ".", "layout.json");
        DesktopLayoutStore store = new(relativePath);
        DesktopLayoutDocument document = new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            []);

        store.Save(document);
        Assert.True(File.Exists(Path.GetFullPath(relativePath)));
    }

    [Fact]
    public void LoadReturnsEmptyDocumentWhenFileDoesNotExist()
    {
        DesktopLayoutStore store = CreateStore();

        DesktopLayoutDocument document = store.Load();

        Assert.Equal(DesktopLayoutDocument.Empty, document);
    }

    [Fact]
    public void SaveAndLoadRoundTripMultipleRegionsAndMembers()
    {
        DesktopLayoutDocument expected = new(
            DesktopLayoutDocument.CurrentVersion,
            [
                new DesktopRegionLayout(
                    Guid.NewGuid(),
                    "学习资料",
                    "#2F80ED",
                    0.26,
                    -20,
                    35,
                    430,
                    230,
                    true,
                    ["C:\\Users\\Qiu\\Desktop\\one.lnk", "shell:two"]),
                new DesktopRegionLayout(
                    Guid.NewGuid(),
                    "待处理",
                    "#F2994A",
                    0.6,
                    500,
                    100,
                    300,
                    180,
                    false,
                    ["shell:three"])
            ]);

        DesktopLayoutStore store = CreateStore();
        store.Save(expected);

        DesktopLayoutDocument actual = store.Load();

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Regions.Length, actual.Regions.Length);
        for (int index = 0; index < expected.Regions.Length; index++)
        {
            Assert.Equal(expected.Regions[index].Id, actual.Regions[index].Id);
            Assert.Equal(expected.Regions[index].Name, actual.Regions[index].Name);
            Assert.Equal(expected.Regions[index].Color, actual.Regions[index].Color);
            Assert.Equal(expected.Regions[index].BackgroundOpacity, actual.Regions[index].BackgroundOpacity);
            Assert.Equal(expected.Regions[index].Bounds, actual.Regions[index].Bounds);
            Assert.Equal(expected.Regions[index].IsLocked, actual.Regions[index].IsLocked);
            Assert.Equal(
                expected.Regions[index].MemberIdentities,
                actual.Regions[index].MemberIdentities);
        }
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"backgroundOpacity\"", json);
        Assert.Contains("\"memberIdentities\"", json);
        Assert.DoesNotContain("\"BackgroundOpacity\"", json);
    }

    [Fact]
    public void InvalidJsonThrowsWithoutChangingOriginalFile()
    {
        File.WriteAllText(FilePath, "{\"version\":1,\"regions\":[");
        string original = File.ReadAllText(FilePath);

        Assert.Throws<InvalidDataException>(() => CreateStore().Load());

        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void UnsupportedVersionThrows()
    {
        File.WriteAllText(FilePath, "{\"version\":99,\"regions\":[]}");

        Assert.Throws<InvalidDataException>(() => CreateStore().Load());
    }

    [Fact]
    public void FileLargerThanOneMiBThrows()
    {
        File.WriteAllBytes(FilePath, new byte[1024 * 1024 + 1]);

        Assert.Throws<InvalidDataException>(() => CreateStore().Load());
    }

    [Fact]
    public void SaveRejectsInvalidRegionValues()
    {
        Guid id = Guid.NewGuid();
        DesktopLayoutStore store = CreateStore();

        AssertInvalid(store, new DesktopRegionLayout(
            Guid.Empty, "Valid", "#123456", 0.2, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, " ", "#123456", 0.2, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "red", 0.2, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#12345G", 0.2, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#123456", 0.1, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#123456", 0.86, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#123456", double.NaN, 0, 0, 1, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#123456", 0.2, 0, 0, 0, 1, false, []));
        AssertInvalid(store, new DesktopRegionLayout(
            id, "Valid", "#123456", 0.2, 0, 0, 1, -1, false, []));
    }

    [Fact]
    public void SaveRejectsEmptyAndDuplicateRegionIds()
    {
        Guid id = Guid.NewGuid();
        DesktopLayoutStore store = CreateStore();

        DesktopRegionLayout region = ValidRegion(id, "one");
        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [region, ValidRegion(id, "two")]));
        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [region with { MemberIdentities = null! }]));
    }

    [Fact]
    public void SaveRejectsDuplicateMemberIdentitiesWithinAndAcrossRegions()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        DesktopLayoutStore store = CreateStore();

        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [ValidRegion(firstId, "one") with { MemberIdentities = ["same", "same"] }]));
        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [
                ValidRegion(firstId, "one") with { MemberIdentities = ["same"] },
                ValidRegion(secondId, "two") with { MemberIdentities = ["SAME"] }
            ]));
    }

    [Fact]
    public void SaveRejectsInvalidRootValues()
    {
        DesktopLayoutStore store = CreateStore();

        AssertInvalid(store, new DesktopLayoutDocument(0, []));
        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            null!));
    }

    [Fact]
    public void SaveRejectsMoreThanMaximumRegions()
    {
        DesktopRegionLayout[] regions = Enumerable
            .Range(0, DesktopLayoutDocument.MaxRegionCount + 1)
            .Select(index => ValidRegion(Guid.NewGuid(), $"region-{index}"))
            .ToArray();

        AssertInvalid(CreateStore(), new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            regions));
    }

    [Fact]
    public void LoadRejectsMoreThanMaximumRegions()
    {
        DesktopRegionLayout[] regions = Enumerable
            .Range(0, DesktopLayoutDocument.MaxRegionCount + 1)
            .Select(index => ValidRegion(Guid.NewGuid(), $"region-{index}"))
            .ToArray();
        string json = JsonSerializer.Serialize(
            new DesktopLayoutDocument(DesktopLayoutDocument.CurrentVersion, regions),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(FilePath, json);

        Assert.Throws<InvalidDataException>(() => CreateStore().Load());
    }

    [Fact]
    public void SaveOverwritesAtomicallyAndLeavesNoTemporaryFile()
    {
        DesktopLayoutStore store = CreateStore();
        DesktopLayoutDocument first = new(
            DesktopLayoutDocument.CurrentVersion,
            [ValidRegion(Guid.NewGuid(), "first")]);
        DesktopLayoutDocument second = new(
            DesktopLayoutDocument.CurrentVersion,
            [ValidRegion(Guid.NewGuid(), "second")]);

        store.Save(first);
        string firstJson = File.ReadAllText(FilePath);
        store.Save(second);

        Assert.NotEqual(firstJson, File.ReadAllText(FilePath));
        DesktopLayoutDocument loaded = store.Load();
        Assert.Equal(second.Version, loaded.Version);
        Assert.Equal(second.Regions[0].Name, loaded.Regions[0].Name);
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(FilePath, Directory.GetFiles(_directory)[0]);
    }

    [Fact]
    public void SaveDoesNotTouchExistingFileWhenDocumentIsInvalid()
    {
        DesktopLayoutStore store = CreateStore();
        store.Save(new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [ValidRegion(Guid.NewGuid(), "original")]));
        string original = File.ReadAllText(FilePath);

        AssertInvalid(store, new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [ValidRegion(Guid.NewGuid(), " ")]));

        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_directory, "desktop-layout.json");

    private DesktopLayoutStore CreateStore() => new(FilePath);

    private static DesktopRegionLayout ValidRegion(Guid id, string name) =>
        new(id, name, "#123456", 0.2, 0, 0, 100, 100, false, []);

    private static void AssertInvalid(
        DesktopLayoutStore store,
        DesktopRegionLayout region) =>
        Assert.Throws<InvalidDataException>(() => store.Save(new DesktopLayoutDocument(
            DesktopLayoutDocument.CurrentVersion,
            [region])));

    private static void AssertInvalid(
        DesktopLayoutStore store,
        DesktopLayoutDocument document) =>
        Assert.Throws<InvalidDataException>(() => store.Save(document));
}
