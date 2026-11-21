using System.IO.Compression;
using System.Xml.Linq;
using Nogrammerly.Core;
using Xunit;

public class StarterDocumentTests
{
    private const string Manifest = "<OfficeApp><Id>6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41</Id>\n  <Version>0.1.0.0</Version></OfficeApp>";

    private static Dictionary<string, XDocument> Parts(byte[] document)
    {
        using var zip = new ZipArchive(new MemoryStream(document));
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); return XDocument.Load(s); });
    }

    [Fact]
    public void RefersToTheRegisteredAddInByIdAndVersion()
    {
        var parts = Parts(StarterDocument.Build(Manifest, "Ready."));
        var reference = parts["word/webextensions/webextension.xml"].Descendants().Single(e => e.Name.LocalName == "reference");
        Assert.Equal("6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41", reference.Attribute("id")!.Value);
        Assert.Equal("0.1.0.0", reference.Attribute("version")!.Value);
        Assert.Equal(("developer", "Registry"), (reference.Attribute("store")!.Value, reference.Attribute("storeType")!.Value));
        Assert.Equal("{6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41}", parts["word/webextensions/webextension.xml"].Root!.Attribute("id")!.Value);
    }

    [Fact]
    public void EveryPartIsDeclaredAndEveryRelationshipLeadsToAPart()
    {
        var parts = Parts(StarterDocument.Build(Manifest, "Ready."));
        var overrides = parts["[Content_Types].xml"].Root!.Elements().Where(e => e.Name.LocalName == "Override").Select(e => e.Attribute("PartName")!.Value.TrimStart('/'));
        Assert.All(overrides, name => Assert.Contains(name, parts.Keys));
        var targets = parts.Where(p => p.Key.EndsWith(".rels")).SelectMany(p => p.Value.Root!.Elements()).Select(e => e.Attribute("Target")!.Value.TrimStart('/'));
        Assert.Equal(["word/document.xml", "word/webextensions/taskpanes.xml", "word/webextensions/webextension.xml"], targets.Order());
        // The pane points at the add-in through the relationship of the same name.
        var paneRef = parts["word/webextensions/taskpanes.xml"].Descendants().Single(e => e.Name.LocalName == "webextensionref").Attributes().Single(a => a.Name.LocalName == "id").Value;
        Assert.Equal(paneRef, parts["word/webextensions/_rels/taskpanes.xml.rels"].Root!.Elements().Single().Attribute("Id")!.Value);
    }

    [Fact]
    public void TheMessageIsShownAsTextNotMarkup()
    {
        var body = Parts(StarterDocument.Build(Manifest, "Tom & <Jerry>")).GetValueOrDefault("word/document.xml")!;
        Assert.Equal("Tom & <Jerry>", body.Descendants().Single(e => e.Name.LocalName == "t").Value);
    }

    [Fact]
    public void RefusesAManifestWithoutAnIdOrVersion() =>
        Assert.Throws<InvalidDataException>(() => StarterDocument.Build("<OfficeApp><Id>nope</Id></OfficeApp>", "x"));
}
