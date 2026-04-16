using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PiSharp.WebUi.Tests;

public sealed class DocumentExtractorTests
{
    private readonly DocumentExtractor _extractor = new();

    [Fact]
    public async Task ExtractTextAsync_ExtractsPdfText()
    {
        await using var stream = CreatePdfStream("Hello PDF");

        var text = await _extractor.ExtractTextAsync(stream, "sample.pdf");

        Assert.Contains("Hello PDF", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractTextAsync_ExtractsDocxText()
    {
        await using var stream = CreateDocxStream("Hello DOCX");

        var text = await _extractor.ExtractTextAsync(stream, "sample.docx");

        Assert.Contains("Hello DOCX", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractTextAsync_ExtractsXlsxTextFromFirstSheet()
    {
        await using var stream = CreateXlsxStream("Name", "PiSharp");

        var text = await _extractor.ExtractTextAsync(stream, "sample.xlsx");

        Assert.Contains("Name\tPiSharp", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractTextAsync_ExtractsPptxText()
    {
        await using var stream = CreatePptxStream("Hello PPTX");

        var text = await _extractor.ExtractTextAsync(stream, "sample.pptx");

        Assert.Contains("Hello PPTX", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsErrorForUnsupportedFormats()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("plain text"));

        var text = await _extractor.ExtractTextAsync(stream, "sample.txt");

        Assert.Contains("Unsupported document format", text, StringComparison.Ordinal);
    }

    private static MemoryStream CreatePdfStream(string text)
    {
        var escapedText = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

        var streamContent = $"BT\n/F1 18 Tf\n72 100 Td\n({escapedText}) Tj\nET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamContent)} >>\nstream\n{streamContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var builder = new StringBuilder();
        var offsets = new List<int>();
        builder.Append("%PDF-1.4\n");

        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1);
            builder.Append(" 0 obj\n");
            builder.Append(objects[index]);
            builder.Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n");
        builder.Append("0 ");
        builder.Append(objects.Length + 1);
        builder.Append('\n');
        builder.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10"));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n");
        builder.Append("<< /Root 1 0 R /Size ");
        builder.Append(objects.Length + 1);
        builder.Append(" >>\n");
        builder.Append("startxref\n");
        builder.Append(xrefOffset);
        builder.Append("\n%%EOF");

        return new MemoryStream(Encoding.ASCII.GetBytes(builder.ToString()));
    }

    private static MemoryStream CreateDocxStream(string text)
    {
        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new W.Document(
                new W.Body(
                    new W.Paragraph(
                        new W.Run(
                            new W.Text(text)))));
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateXlsxStream(string firstCell, string secondCell)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new S.Worksheet(
                new S.SheetData(
                    new S.Row(
                        CreateInlineStringCell(firstCell),
                        CreateInlineStringCell(secondCell))));

            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1",
            });

            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreatePptxStream(string text)
    {
        var stream = new MemoryStream();
        using (var presentation = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = presentation.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.GroupShapeProperties(),
                        new P.Shape(
                            new P.NonVisualShapeProperties(
                                new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" },
                                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                                new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape())),
                            new P.ShapeProperties(),
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.ListStyle(),
                                new A.Paragraph(new A.Run(new A.Text(text))))))));

            slidePart.Slide.Save();

            presentationPart.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId
                {
                    Id = 256U,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });

            presentationPart.Presentation.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static S.Cell CreateInlineStringCell(string value) =>
        new()
        {
            DataType = S.CellValues.InlineString,
            InlineString = new S.InlineString(new S.Text(value)),
        };
}
