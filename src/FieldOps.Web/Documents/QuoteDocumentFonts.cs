using QuestPDF.Drawing;

namespace FieldOps.Web.Documents;

/// <summary>
/// Registers the Japanese font embedded in this assembly with QuestPDF at application startup.
/// </summary>
/// <remarks>
/// The production container image (see the repository Dockerfile) installs no font packages at all,
/// so relying on environment/system fonts would silently produce PDFs full of tofu boxes for Japanese
/// text. Registering an embedded font file here makes rendering deterministic on every machine,
/// including a developer's local <c>dotnet run</c>.
/// </remarks>
public static class QuoteDocumentFonts
{
    private const string RegularResourceName = "FieldOps.Web.Resources.Fonts.IBMPlexSansJP-Regular.ttf";
    private const string BoldResourceName = "FieldOps.Web.Resources.Fonts.IBMPlexSansJP-Bold.ttf";

    public static void Register()
    {
        FontManager.RegisterFontFromEmbeddedResource(RegularResourceName);
        FontManager.RegisterFontFromEmbeddedResource(BoldResourceName);
    }
}