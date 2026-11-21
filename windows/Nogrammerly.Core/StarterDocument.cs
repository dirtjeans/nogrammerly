using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Nogrammerly.Core;

/// <summary>
/// A small Word document that carries a reference to the Word add-in. Word lists an add-in that isn't from its
/// store under "Developer Add-ins" and shows its button only once someone adds it from there. Opening this
/// document does that step: Word loads the add-in it refers to and opens its pane.
/// </summary>
public static class StarterDocument
{
    /// <param name="manifest">The add-in's manifest; the document must name the same id and version.</param>
    /// <param name="message">One line of text shown in the document.</param>
    public static byte[] Build(string manifest, string message)
    {
        var id = Regex.Match(manifest, @"<Id>\s*([0-9a-fA-F-]{36})\s*</Id>").Groups[1].Value;
        var version = Regex.Match(manifest, @"<Version>\s*([0-9.]+)\s*</Version>").Groups[1].Value;
        if (id.Length == 0 || version.Length == 0) throw new InvalidDataException("The add-in's manifest has no id or version.");

        const string package = "http://schemas.openxmlformats.org/package/2006";
        const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string extensions = "http://schemas.microsoft.com/office/webextensions";
        var parts = new (string Name, string Xml)[]
        {
            ("[Content_Types].xml",
                $"<Types xmlns=\"{package}/content-types\">" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/webextensions/taskpanes.xml\" ContentType=\"application/vnd.ms-office.webextensiontaskpanes+xml\"/>" +
                "<Override PartName=\"/word/webextensions/webextension.xml\" ContentType=\"application/vnd.ms-office.webextension+xml\"/>" +
                "</Types>"),
            ("_rels/.rels",
                $"<Relationships xmlns=\"{package}/relationships\">" +
                $"<Relationship Id=\"rDoc\" Type=\"{office}/officeDocument\" Target=\"/word/document.xml\"/>" +
                "<Relationship Id=\"rPanes\" Type=\"http://schemas.microsoft.com/office/2011/relationships/webextensiontaskpanes\" Target=\"/word/webextensions/taskpanes.xml\"/>" +
                "</Relationships>"),
            ("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                $"<w:p><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(message)}</w:t></w:r></w:p>" +
                "</w:body></w:document>"),
            // One task pane, shown, holding the add-in named in the next part.
            ("word/webextensions/taskpanes.xml",
                $"<wetp:taskpanes xmlns:wetp=\"{extensions}/taskpanes/2010/11\" xmlns:r=\"{office}\">" +
                "<wetp:taskpane dockstate=\"right\" visibility=\"1\" width=\"350\" row=\"1\"><wetp:webextensionref r:id=\"rAddin\"/></wetp:taskpane>" +
                "</wetp:taskpanes>"),
            ("word/webextensions/_rels/taskpanes.xml.rels",
                $"<Relationships xmlns=\"{package}/relationships\">" +
                "<Relationship Id=\"rAddin\" Type=\"http://schemas.microsoft.com/office/2011/relationships/webextension\" Target=\"/word/webextensions/webextension.xml\"/>" +
                "</Relationships>"),
            // "developer" and "Registry" tell Word to find the add-in among those registered on this PC.
            ("word/webextensions/webextension.xml",
                $"<we:webextension xmlns:we=\"{extensions}/webextension/2010/11\" id=\"{{{id}}}\">" +
                $"<we:reference id=\"{id}\" version=\"{version}\" store=\"developer\" storeType=\"Registry\"/>" +
                "<we:alternateReferences/><we:properties/><we:bindings/></we:webextension>"),
        };

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml));
            }
        }
        return stream.ToArray();
    }
}
