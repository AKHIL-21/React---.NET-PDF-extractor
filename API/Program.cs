using iText.Forms;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseCors(frontendPolicy);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/pdf/extract-fields", async (
    IFormFile file,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
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
        var textPages = ExtractTextPages(document);

        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (pdfFields is not null)
        {
            foreach (var (name, field) in pdfFields)
            {
                fields[name] = field.GetValueAsString();
            }
        }

        var acroFieldCount = fields.Count;
        var modelFields = await ExtractFieldsWithModelAsync(textPages, httpClientFactory, configuration, cancellationToken);
        var textFields = ExtractFieldsFromTextPages(textPages);

        foreach (var (name, value) in modelFields)
        {
            AddField(fields, name, value);
        }

        foreach (var (name, value) in textFields)
        {
            AddField(fields, name, value);
        }

        var extractionMode = (acroFieldCount, textFields.Count) switch
        {
            _ when modelFields.Count > 0 && acroFieldCount > 0 => "model-form-fields-and-visible-text",
            _ when modelFields.Count > 0 => "model-visible-text",
            (0, 0) => "no-fields-found",
            (> 0, > 0) => "form-fields-and-visible-text",
            (> 0, 0) => "form-fields",
            _ => "visible-text"
        };

        return Results.Ok(new PdfExtractionResponse(
            file.FileName,
            fields.Count,
            extractionMode,
            fields));
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = $"Could not read PDF form fields: {exception.Message}" });
    }
})
.DisableAntiforgery();

app.Run();

static IReadOnlyList<PdfTextPage> ExtractTextPages(PdfDocument document)
{
    var pages = new List<PdfTextPage>();

    for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
    {
        var text = PdfTextExtractor.GetTextFromPage(
            document.GetPage(pageNumber),
            new LocationTextExtractionStrategy());

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(line => line.Length > 0)
            .ToList();

        pages.Add(new PdfTextPage(pageNumber, lines));
    }

    return pages;
}

static IReadOnlyDictionary<string, string> ExtractFieldsFromTextPages(IReadOnlyList<PdfTextPage> pages)
{
    var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var allLines = new List<string>();

    foreach (var page in pages)
    {
        allLines.AddRange(page.Lines);
        AnalyzeTextLines(page.Lines, fields, page.PageNumber);
    }

    if (fields.Count == 0 && allLines.Count > 0)
    {
        fields["Extracted Text"] = string.Join(Environment.NewLine, allLines);
    }

    return fields;
}

static async Task<IReadOnlyDictionary<string, string>> ExtractFieldsWithModelAsync(
    IReadOnlyList<PdfTextPage> pages,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["OPENAI_API_KEY"];
    var model = configuration["OPENAI_MODEL"];

    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model) || pages.Count == 0)
    {
        return new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    var endpoint = configuration["OPENAI_CHAT_COMPLETIONS_URL"];

    if (string.IsNullOrWhiteSpace(endpoint))
    {
        endpoint = "https://api.openai.com/v1/chat/completions";
    }

    var documentText = BuildModelInput(pages);
    var requestBody = new
    {
        model,
        temperature = 0,
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new
            {
                role = "system",
                content = """
You extract structured information from PDF text.
Infer labels from layout, nearby headings, wording, questions, tables, and repeated patterns.
Do not use a predefined field list. Do not invent values.
If a value has no nearby label, create a short descriptive label from the closest section or surrounding text.
Return only JSON in this shape: {"fields":[{"label":"...","value":"..."}]}.
"""
            },
            new
            {
                role = "user",
                content = documentText
            }
        }
    };

    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseModelFields(responseJson);
    }
    catch
    {
        return new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

static string BuildModelInput(IReadOnlyList<PdfTextPage> pages)
{
    const int maxCharacters = 60000;
    var builder = new StringBuilder();

    foreach (var page in pages)
    {
        builder.AppendLine($"[Page {page.PageNumber}]");

        foreach (var line in page.Lines)
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();

        if (builder.Length >= maxCharacters)
        {
            break;
        }
    }

    return builder.Length <= maxCharacters
        ? builder.ToString()
        : builder.ToString(0, maxCharacters);
}

static IReadOnlyDictionary<string, string> ParseModelFields(string responseJson)
{
    var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    using var responseDocument = JsonDocument.Parse(responseJson);
    var root = responseDocument.RootElement;

    if (!root.TryGetProperty("choices", out var choices) ||
        choices.ValueKind != JsonValueKind.Array ||
        choices.GetArrayLength() == 0)
    {
        return fields;
    }

    var content = choices[0]
        .GetProperty("message")
        .GetProperty("content")
        .GetString();

    if (string.IsNullOrWhiteSpace(content))
    {
        return fields;
    }

    using var contentDocument = JsonDocument.Parse(content);

    if (!contentDocument.RootElement.TryGetProperty("fields", out var extractedFields) ||
        extractedFields.ValueKind != JsonValueKind.Array)
    {
        return fields;
    }

    foreach (var field in extractedFields.EnumerateArray())
    {
        if (!field.TryGetProperty("label", out var labelElement) ||
            !field.TryGetProperty("value", out var valueElement))
        {
            continue;
        }

        AddField(fields, labelElement.GetString() ?? string.Empty, valueElement.GetString() ?? string.Empty);
    }

    return fields;
}

static void AnalyzeTextLines(IReadOnlyList<string> lines, IDictionary<string, string> fields, int pageNumber)
{
    for (var index = 0; index < lines.Count; index++)
    {
        ExtractSeparatedPairs(lines[index], fields);
        ExtractColumnPairs(lines, index, fields);
        ExtractInlineInferredPair(lines, index, fields);
        ExtractAdjacentPair(lines, index, fields);
    }

    ExtractStandaloneValues(lines, fields, pageNumber);
}

static void ExtractSeparatedPairs(string line, IDictionary<string, string> fields)
{
    foreach (var segment in SplitColumns(line))
    {
        if (TrySplitSeparatedPair(segment, out var label, out var value))
        {
            AddField(fields, label, value);
        }
    }
}

static void ExtractColumnPairs(IReadOnlyList<string> lines, int index, IDictionary<string, string> fields)
{
    var columns = SplitColumns(lines[index]).ToList();

    if (columns.Count >= 4 && columns.Count % 2 == 0)
    {
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex += 2)
        {
            var label = columns[columnIndex];
            var value = columns[columnIndex + 1];

            if (IsLikelyLabel(label) && IsLikelyValueForLabel(value, label))
            {
                AddField(fields, label, value);
            }
        }
    }

    if (index >= lines.Count - 1)
    {
        return;
    }

    var nextColumns = SplitColumns(lines[index + 1]).ToList();

    if (columns.Count < 2 || columns.Count != nextColumns.Count)
    {
        return;
    }

    var labelCount = columns.Count(IsLikelyLabel);
    var valueCount = nextColumns
        .Select((value, columnIndex) => IsLikelyValueForLabel(value, columns[columnIndex]))
        .Count(isValue => isValue);

    if (labelCount < columns.Count || valueCount < Math.Max(1, columns.Count - 1))
    {
        return;
    }

    for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
    {
        if (IsLikelyValueForLabel(nextColumns[columnIndex], columns[columnIndex]))
        {
            AddField(fields, columns[columnIndex], nextColumns[columnIndex]);
        }
    }
}

static void ExtractInlineInferredPair(IReadOnlyList<string> lines, int index, IDictionary<string, string> fields)
{
    var line = lines[index];

    if (TrySplitInlineInferredPair(line, out var label, out var value))
    {
        if (index + 1 < lines.Count &&
            LooksLikeAddressValue(value) &&
            LooksLikeAddressContinuation(lines[index + 1]))
        {
            value = $"{value} {lines[index + 1]}";
        }

        AddField(fields, label, value);
    }
}

static void ExtractAdjacentPair(IReadOnlyList<string> lines, int index, IDictionary<string, string> fields)
{
    var label = lines[index];
    var nextLine = index + 1 < lines.Count ? lines[index + 1] : string.Empty;

    if (!LooksLikePrompt(label) ||
        IsLikelySectionHeading(label, nextLine) ||
        TrySplitInlineInferredPair(label, out _, out _))
    {
        return;
    }

    var valueLines = new List<string>();

    for (var lookAhead = 1; lookAhead <= 4 && index + lookAhead < lines.Count; lookAhead++)
    {
        var value = lines[index + lookAhead];
        var followingLine = index + lookAhead + 1 < lines.Count ? lines[index + lookAhead + 1] : string.Empty;

        if (IsPageMarker(value))
        {
            continue;
        }

        if (valueLines.Count == 0 && (LooksLikePrompt(value) || IsLikelySectionHeading(value, followingLine)))
        {
            return;
        }

        if (valueLines.Count > 0 && (LooksLikePrompt(value) || IsLikelySectionHeading(value, followingLine)))
        {
            break;
        }

        if (value.Length > 260)
        {
            break;
        }

        valueLines.Add(value);

        if (IsStrongStandaloneValue(value) || IsYesNoValue(value) || string.Join(' ', valueLines).Length > 220)
        {
            break;
        }
    }

    if (valueLines.Count > 0)
    {
        AddField(fields, label, string.Join(' ', valueLines));
    }
}

static void ExtractStandaloneValues(IReadOnlyList<string> lines, IDictionary<string, string> fields, int pageNumber)
{
    for (var index = 0; index < lines.Count; index++)
    {
        var line = lines[index];

        if (LooksLikePrompt(line) ||
            IsPageMarker(line) ||
            TrySplitSeparatedPair(line, out _, out _) ||
            TrySplitInlineInferredPair(line, out _, out _))
        {
            continue;
        }

        var valueKind = GetStandaloneValueKind(line);

        if (valueKind.Length > 0)
        {
            var context = FindNearestContext(lines, index);
            var label = context.Length > 0
                ? $"{context} {valueKind}"
                : $"Page {pageNumber} {valueKind}";

            AddField(fields, label, line);
        }
    }
}

static bool TrySplitSeparatedPair(string line, out string label, out string value)
{
    var match = Regex.Match(
        line,
        @"^(?<label>[A-Za-z][A-Za-z0-9\s/#&().,'-]{1,110}?)(?:\s*[:=]\s*|\s+-\s+|\.{3,}|_{2,}|-{2,})(?<value>.+)$");

    if (!match.Success)
    {
        label = string.Empty;
        value = string.Empty;
        return false;
    }

    label = match.Groups["label"].Value;
    value = match.Groups["value"].Value;

    return IsLikelyLabel(label) && IsLikelyValueForLabel(value, label);
}

static bool TrySplitInlineInferredPair(string line, out string label, out string value)
{
    label = string.Empty;
    value = string.Empty;

    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (parts.Length < 2 ||
        TrySplitSeparatedPair(line, out _, out _) ||
        IsPageMarker(line) ||
        LooksLikeAddressContinuation(line))
    {
        return false;
    }

    if (line.EndsWith('.') && !ContainsValuePattern(line))
    {
        return false;
    }

    var bestScore = int.MinValue;

    for (var splitIndex = 1; splitIndex < parts.Length; splitIndex++)
    {
        var candidateLabel = string.Join(' ', parts[..splitIndex]);
        var candidateValue = string.Join(' ', parts[splitIndex..]);

        if (!ContainsValuePattern(line) &&
            !IsYesNoValue(candidateValue) &&
            !LooksLikeEmphasizedTextValue(candidateValue))
        {
            continue;
        }

        if (!IsLikelyInlinePair(candidateLabel, candidateValue))
        {
            continue;
        }

        var score = ScoreInlinePair(candidateLabel, candidateValue);

        if (score <= bestScore)
        {
            continue;
        }

        bestScore = score;
        label = candidateLabel;
        value = candidateValue;
    }

    return bestScore >= 20;
}

static int ScoreInlinePair(string label, string value)
{
    var score = 0;
    var normalizedLabel = NormalizeLabel(label);
    var normalizedValue = CleanValue(value);

    if (HasPromptShape(normalizedLabel))
    {
        score += 20;
    }

    if (IsStrongStandaloneValue(normalizedValue))
    {
        score += 40;
    }

    if (IsYesNoValue(normalizedValue))
    {
        score += 35;
    }

    if (LooksLikePersonOrOrganization(normalizedValue))
    {
        score += 15;
    }

    if (LooksLikeAddressValue(normalizedValue))
    {
        score += 25;
    }

    if (Regex.IsMatch(normalizedLabel, @"\b(to|for|from|by)\b", RegexOptions.IgnoreCase) &&
        LooksLikePersonOrOrganization(normalizedValue))
    {
        score += 10;
    }

    if (normalizedLabel.EndsWith(" To", StringComparison.OrdinalIgnoreCase))
    {
        score += 20;
    }

    if (normalizedValue.StartsWith("To ", StringComparison.OrdinalIgnoreCase))
    {
        score -= 20;
    }

    if (Regex.IsMatch(normalizedValue, @"\d") && WordCount(normalizedLabel) is >= 2 and <= 4)
    {
        score += 10;
    }

    if (WordCount(normalizedValue) == 1 &&
        Regex.IsMatch(normalizedValue, @"^[A-Za-z]+$") &&
        !IsSingleWordTextValueAllowed(normalizedLabel, normalizedValue))
    {
        score -= 35;
    }

    if (ContainsValuePattern(normalizedLabel))
    {
        score -= 60;
    }

    if (WordCount(normalizedValue) == 1 &&
        Regex.IsMatch(normalizedValue, @"^[A-Za-z]+$") &&
        WordCount(normalizedLabel) > 3)
    {
        score -= 25;
    }

    score -= Math.Max(0, WordCount(normalizedLabel) - 4) * 3;

    return score;
}

static void AddField(IDictionary<string, string> fields, string rawLabel, string rawValue)
{
    var adjustedField = PromoteValuePrefixToLabel(rawLabel, rawValue);
    var label = NormalizeLabel(adjustedField.Label);
    var value = CleanValue(adjustedField.Value);

    if (label.Length == 0 || value.Length == 0 || IsPageMarker(label) || IsPageMarker(value))
    {
        return;
    }

    if (fields.Values.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) &&
        label.StartsWith("Detected ", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var uniqueLabel = label;
    var duplicateCount = 2;

    while (fields.TryGetValue(uniqueLabel, out var existingValue))
    {
        if (string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        uniqueLabel = $"{label} ({duplicateCount})";
        duplicateCount++;
    }

    fields[uniqueLabel] = value;
}

static bool IsLikelyInlinePair(string label, string value)
{
    var isShortUnknownLabel = IsShortUnknownLabel(label, value);

    return (IsLikelyLabel(label) || isShortUnknownLabel) &&
        IsLikelyValueForLabel(value, label);
}

static (string Label, string Value) PromoteValuePrefixToLabel(string rawLabel, string rawValue)
{
    var label = NormalizeLabel(rawLabel);
    var value = CleanValue(rawValue);
    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (parts.Length < 2 || WordCount(label) > 4)
    {
        return (rawLabel, rawValue);
    }

    var prefix = parts[0];
    var remainder = string.Join(' ', parts[1..]);

    if (!Regex.IsMatch(prefix, @"^[A-Za-z][A-Za-z/-]*$") ||
        ContainsValuePattern(prefix) ||
        IsYesNoValue(prefix))
    {
        return (rawLabel, rawValue);
    }

    if (IsStrongStandaloneValue(remainder) ||
        LooksLikePersonOrOrganization(remainder) ||
        LooksLikeAddressValue(remainder))
    {
        return ($"{label} {prefix}", remainder);
    }

    return (rawLabel, rawValue);
}

static bool IsLikelyLabel(string line)
{
    var cleaned = NormalizeLabel(line);

    if (cleaned.Length is < 2 or > 120)
    {
        return false;
    }

    if (Regex.IsMatch(cleaned, @"@|\$|https?://", RegexOptions.IgnoreCase))
    {
        return false;
    }

    if (IsPageMarker(cleaned) || IsStrongStandaloneValue(cleaned) || ContainsValuePattern(cleaned))
    {
        return false;
    }

    return HasPromptShape(cleaned);
}

static bool LooksLikePrompt(string line)
{
    var cleaned = NormalizeLabel(line);

    if (!IsLikelyLabel(cleaned))
    {
        return false;
    }

    return cleaned.EndsWith('?') ||
        cleaned.EndsWith(':') ||
        HasPromptShape(cleaned) ||
        WordCount(cleaned) <= 8;
}

static bool IsLikelyValueForLabel(string value, string label)
{
    var cleanedValue = CleanValue(value);

    if (cleanedValue.Length == 0 ||
        cleanedValue.Length > 260 ||
        IsPageMarker(cleanedValue))
    {
        return false;
    }

    if (IsStrongStandaloneValue(cleanedValue) || IsYesNoValue(cleanedValue))
    {
        return true;
    }

    if (LooksLikePersonOrOrganization(cleanedValue) && HasPromptShape(label))
    {
        return true;
    }

    if (HasPromptShape(label) &&
        WordCount(cleanedValue) <= 14 &&
        !cleanedValue.EndsWith('?') &&
        !Regex.IsMatch(cleanedValue, @"^[A-Za-z][A-Za-z0-9\s/#&().,'-]{1,110}\s*[:=]"))
    {
        return true;
    }

    if (NormalizeLabel(label).EndsWith('?') && WordCount(cleanedValue) <= 30)
    {
        return true;
    }

    return false;
}

static bool IsLikelySectionHeading(string line, string nextLine)
{
    var cleaned = NormalizeLabel(line);

    if (cleaned.Length < 3 ||
        cleaned.EndsWith('?') ||
        Regex.IsMatch(cleaned, @"[:=]") ||
        IsStrongStandaloneValue(cleaned) ||
        WordCount(cleaned) > 8)
    {
        return false;
    }

    if (nextLine.Length > 0 &&
        (TrySplitSeparatedPair(nextLine, out _, out _) ||
            TrySplitInlineInferredPair(nextLine, out _, out _) ||
            LooksLikePrompt(nextLine)))
    {
        return true;
    }

    return !cleaned.EndsWith(':') && !cleaned.EndsWith('?') && IsTitleLike(cleaned);
}

static bool HasPromptShape(string line)
{
    var cleaned = NormalizeLabel(line);

    if (cleaned.Length is < 2 or > 120 ||
        ContainsValuePattern(cleaned) ||
        IsPageMarker(cleaned))
    {
        return false;
    }

    return cleaned.EndsWith(':') ||
        cleaned.EndsWith('?') ||
        Regex.IsMatch(cleaned, @"^[A-Za-z][A-Za-z0-9\s/#&().,'-]*$") && WordCount(cleaned) <= 10;
}

static bool IsShortUnknownLabel(string label, string value)
{
    var cleaned = NormalizeLabel(label);

    return WordCount(cleaned) is >= 1 and <= 3 &&
        Regex.IsMatch(cleaned, @"^[A-Za-z][A-Za-z0-9/#-]*$") &&
        !ContainsValuePattern(cleaned) &&
        IsStrongStandaloneValue(value);
}

static bool IsSingleWordTextValueAllowed(string label, string value)
{
    if (IsYesNoValue(value))
    {
        return true;
    }

    return NormalizeLabel(label).EndsWith(':') ||
        NormalizeLabel(label).EndsWith('?') ||
        WordCount(label) <= 4;
}

static bool LooksLikeAddressValue(string line)
{
    return Regex.IsMatch(
        CleanValue(line),
        @"^\d+\s+[A-Za-z0-9 .'-]+$",
        RegexOptions.IgnoreCase);
}

static bool LooksLikeEmphasizedTextValue(string value)
{
    var words = CleanValue(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(word => Regex.IsMatch(word, @"[A-Za-z]"))
        .ToList();

    return words.Count >= 2 &&
        words.All(word => word.Any(char.IsLetter) && word == word.ToUpperInvariant());
}

static bool LooksLikeAddressContinuation(string line)
{
    return Regex.IsMatch(
        CleanValue(line),
        @"^[A-Za-z .'-]+,\s*[A-Z]{2}\s+\d{5}(?:-\d{4})?$",
        RegexOptions.IgnoreCase);
}

static bool IsPageMarker(string line)
{
    return Regex.IsMatch(CleanLine(line), @"^\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
}

static bool IsStrongStandaloneValue(string value)
{
    return GetStandaloneValueKind(value).Length > 0;
}

static string GetStandaloneValueKind(string value)
{
    var cleaned = CleanValue(value);

    if (Regex.IsMatch(cleaned, @"^[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}$"))
    {
        return "Email";
    }

    if (Regex.IsMatch(cleaned, @"^(?:\+?1[-.\s]?)?(?:\(?\d{3}\)?[-.\s]?)\d{3}[-.\s]?\d{4}$"))
    {
        return "Phone";
    }

    if (Regex.IsMatch(cleaned, @"^(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|[A-Za-z]{3,9}\s+\d{1,2},\s+\d{4})(?:\s+-\s+(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|[A-Za-z]{3,9}\s+\d{1,2},\s+\d{4}))?$"))
    {
        return "Date";
    }

    if (Regex.IsMatch(cleaned, @"^\$?\d{1,3}(?:,\d{3})*(?:\.\d{2})?$"))
    {
        return "Amount";
    }

    if (Regex.IsMatch(cleaned, @"^\d{3}-\d{2}-\d{4}$"))
    {
        return "SSN";
    }

    if (LooksLikeAddressValue(cleaned))
    {
        return "Address";
    }

    if (Regex.IsMatch(cleaned, @"^[A-Z]{1,6}[- ]?\d{3,}[A-Z0-9-]*$", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(cleaned, @"^\d{5,}$"))
    {
        return "Identifier";
    }

    return string.Empty;
}

static bool IsYesNoValue(string value)
{
    return Regex.IsMatch(CleanValue(value), @"^(yes|no|true|false|n/a|na|none|unknown)$", RegexOptions.IgnoreCase);
}

static bool ContainsValuePattern(string value)
{
    return IsStrongStandaloneValue(value) ||
        IsYesNoValue(value) ||
        Regex.IsMatch(value, @"\d{2,}|@|\$|%|#");
}

static bool LooksLikePersonOrOrganization(string value)
{
    var cleaned = CleanValue(value);

    if (WordCount(cleaned) is < 2 or > 8 || ContainsValuePattern(cleaned))
    {
        return false;
    }

    return Regex.IsMatch(cleaned, @"^[A-Za-z][A-Za-z .,'&-]+$");
}

static string FindNearestContext(IReadOnlyList<string> lines, int index)
{
    for (var lookBehind = index - 1; lookBehind >= Math.Max(0, index - 6); lookBehind--)
    {
        var candidate = NormalizeLabel(lines[lookBehind]);

        if (candidate.Length == 0 ||
            IsPageMarker(candidate) ||
            ContainsValuePattern(candidate) ||
            TrySplitSeparatedPair(candidate, out _, out _) ||
            TrySplitInlineInferredPair(candidate, out _, out _))
        {
            continue;
        }

        if (IsTitleLike(candidate) || HasPromptShape(candidate))
        {
            return candidate;
        }
    }

    return string.Empty;
}

static bool IsTitleLike(string value)
{
    var words = CleanLine(value).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (words.Length == 0)
    {
        return false;
    }

    return words.All(word =>
        word.Length == 0 ||
        !char.IsLetter(word[0]) ||
        char.IsUpper(word[0]));
}

static int WordCount(string value)
{
    return Regex.Matches(CleanLine(value), @"[A-Za-z0-9]+").Count;
}

static IEnumerable<string> SplitColumns(string line)
{
    return Regex
        .Split(line.Trim(), @"\s{2,}|\t+|\s*\|\s*")
        .Select(CleanLine)
        .Where(column => column.Length > 0);
}

static string NormalizeLabel(string value)
{
    return CleanLine(value)
        .Trim(':', '.', '_', '-', ' ')
        .Replace("  ", " ");
}

static string CleanValue(string value)
{
    return Regex
        .Replace(CleanLine(value), @"^[\s:._=\-*\[\]()]+|[\s:._=\-*]+$", string.Empty)
        .Trim();
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

record PdfTextPage(
    int PageNumber,
    IReadOnlyList<string> Lines);
