using iText.Forms;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

const string frontendPolicy = "Frontend";

builder.Services.AddCors(options =>
{
    options.AddPolicy(frontendPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors(frontendPolicy);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/pdf/extract-fields", async (IFormFile file) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest(new { error = "Upload a PDF file with form fields." });
    }

    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
        file.ContentType != "application/pdf")
    {
        return Results.BadRequest(new { error = "Only PDF files are supported." });
    }

    await using var uploadStream = new MemoryStream();
    await file.CopyToAsync(uploadStream);
    uploadStream.Position = 0;

    try
    {
        using var reader = new PdfReader(uploadStream);
        using var document = new PdfDocument(reader);
        var form = PdfAcroForm.GetAcroForm(document, false);
        var pdfFields = form?.GetAllFormFields();

        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (pdfFields is not null)
        {
            foreach (var (name, field) in pdfFields)
            {
                fields[name] = field.GetValueAsString();
            }
        }

        if (fields.Count == 0)
        {
            foreach (var (name, value) in ExtractFieldsFromFlattenedPdf(document))
            {
                fields[name] = value;
            }
        }

        return Results.Ok(new PdfExtractionResponse(
            file.FileName,
            fields.Count,
            fields.Count > 0 ? "extracted" : "no-fields-found",
            fields));
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = $"Could not read PDF form fields: {exception.Message}" });
    }
})
.DisableAntiforgery();

app.Run();

static IReadOnlyDictionary<string, string> ExtractFieldsFromFlattenedPdf(PdfDocument document)
{
    var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var allLines = new List<string>();

    for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
    {
        var text = PdfTextExtractor.GetTextFromPage(
            document.GetPage(pageNumber),
            new SimpleTextExtractionStrategy());

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(line => line.Length > 0)
            .ToList();

        allLines.AddRange(lines);
        ExtractInlineLabelValues(lines, fields);
        ExtractAdjacentLabelValues(lines, fields);
    }

    if (fields.Count == 0 && allLines.Count > 0)
    {
        fields["Extracted Text"] = string.Join(Environment.NewLine, allLines);
    }

    return fields;
}

static void ExtractInlineLabelValues(IReadOnlyList<string> lines, IDictionary<string, string> fields)
{
    foreach (var line in lines)
    {
        var match = Regex.Match(line, @"^(?<label>[A-Za-z][A-Za-z0-9\s/#&().,'-]{1,70}?)[\s:._-]{2,}(?<value>.+)$");

        if (!match.Success)
        {
            match = Regex.Match(line, @"^(?<label>[A-Za-z][A-Za-z0-9\s/#&().,'-]{1,70}?):\s*(?<value>.+)$");
        }

        if (!match.Success)
        {
            continue;
        }

        AddField(fields, match.Groups["label"].Value, match.Groups["value"].Value);
    }
}

static void ExtractAdjacentLabelValues(IReadOnlyList<string> lines, IDictionary<string, string> fields)
{
    for (var index = 0; index < lines.Count - 1; index++)
    {
        var label = lines[index];
        var value = lines[index + 1];

        if (!LooksLikeLabel(label) || LooksLikeLabel(value) || value.Length > 140)
        {
            continue;
        }

        AddField(fields, label, value);
    }
}

static void AddField(IDictionary<string, string> fields, string rawLabel, string rawValue)
{
    var label = NormalizeLabel(rawLabel);
    var value = CleanLine(rawValue);

    if (label.Length == 0 || value.Length == 0 || fields.ContainsKey(label))
    {
        return;
    }

    fields[label] = value;
}

static bool LooksLikeLabel(string line)
{
    var cleaned = NormalizeLabel(line);

    if (cleaned.Length is < 2 or > 80)
    {
        return false;
    }

    if (Regex.IsMatch(cleaned, @"\d{2,}|@|\$"))
    {
        return false;
    }

    var labelWords = new[]
    {
        "name", "address", "city", "state", "zip", "phone", "email", "date", "birth",
        "ssn", "borrower", "applicant", "employer", "income", "loan", "property",
        "signature", "title", "company", "account", "amount", "number", "id"
    };

    return cleaned.EndsWith(':') ||
        labelWords.Any(word => cleaned.Contains(word, StringComparison.OrdinalIgnoreCase));
}

static string NormalizeLabel(string value)
{
    return CleanLine(value)
        .Trim(':', '.', '_', '-', ' ')
        .Replace("  ", " ");
}

static string CleanLine(string value)
{
    return Regex.Replace(value, @"\s+", " ").Trim();
}

record PdfExtractionResponse(
    string FileName,
    int FieldCount,
    string ExtractionMode,
    IReadOnlyDictionary<string, string> Fields);
