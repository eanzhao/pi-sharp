using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;

namespace PiSharp.WebUi;

public sealed class DocumentExtractor
{
    public async Task<string> ExtractTextAsync(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        try
        {
            await using var bufferedStream = new MemoryStream();
            await stream.CopyToAsync(bufferedStream).ConfigureAwait(false);
            bufferedStream.Position = 0;

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => ExtractPdfText(bufferedStream),
                ".docx" => ExtractDocxText(bufferedStream),
                ".xlsx" => ExtractXlsxText(bufferedStream),
                ".pptx" => ExtractPptxText(bufferedStream),
                _ => $"Unsupported document format '{extension}'. Supported formats: .pdf, .docx, .xlsx, .pptx.",
            };
        }
        catch (Exception exception)
        {
            return $"Failed to extract text from '{fileName}': {exception.Message}";
        }
    }

    private static string ExtractPdfText(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        var pages = document.GetPages()
            .Select(page => page.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return pages.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine + Environment.NewLine, pages);
    }

    private static string ExtractDocxText(Stream stream)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var paragraphs = document.MainDocumentPart?.Document?.Body?
            .Descendants<Paragraph>()
            .Select(static paragraph => string.Concat(paragraph.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(static text => text.Text)))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return paragraphs is { Length: > 0 }
            ? string.Join(Environment.NewLine, paragraphs)
            : string.Empty;
    }

    private static string ExtractXlsxText(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        var firstSheet = workbookPart?.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault();
        if (workbookPart is null || firstSheet is null)
        {
            return string.Empty;
        }

        var worksheetPart = workbookPart.GetPartById(firstSheet.Id!) as WorksheetPart;
        if (worksheetPart?.Worksheet is null)
        {
            return string.Empty;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var rows = new List<string>();

        foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
        {
            var cells = row.Elements<Cell>()
                .Select(cell => ReadCellValue(cell, sharedStrings))
                .ToArray();

            if (cells.Any(static value => !string.IsNullOrWhiteSpace(value)))
            {
                rows.Add(string.Join('\t', cells));
            }
        }

        return rows.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, rows);
    }

    private static string ExtractPptxText(Stream stream)
    {
        using var document = PresentationDocument.Open(stream, false);
        var presentationPart = document.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList is null)
        {
            return string.Empty;
        }

        var slides = new List<string>();

        foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
        {
            var slidePart = presentationPart.GetPartById(slideId.RelationshipId!) as SlidePart;
            if (slidePart?.Slide is null)
            {
                continue;
            }

            var text = slidePart.Slide.Descendants<A.Text>()
                .Select(static item => item.Text)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (text.Length > 0)
            {
                slides.Add(string.Join(Environment.NewLine, text));
            }
        }

        return slides.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine + Environment.NewLine, slides);
    }

    private static string ReadCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var rawValue = cell.CellValue?.InnerText;

        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(rawValue, out var sharedStringIndex) &&
            sharedStrings?.ElementAtOrDefault(sharedStringIndex) is SharedStringItem sharedStringItem)
        {
            return sharedStringItem.InnerText;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return rawValue == "1" ? "TRUE" : "FALSE";
        }

        return rawValue ?? string.Empty;
    }
}
