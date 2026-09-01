using System.Globalization;

using FieldOps.Features.Quotes;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FieldOps.Web.Documents;

/// <summary>
/// Renders a Japanese 見積書 (quote) for a single <see cref="QuoteDetailsViewModel"/> using QuestPDF.
/// Every company identity shown here is fictional demo data; see the footer disclaimer.
/// </summary>
public sealed class QuoteDocument(QuoteDetailsViewModel quote) : IDocument
{
    public const string FontFamilyName = "IBM Plex Sans JP";

    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"見積書 {quote.QuoteNumber}",
        Author = "FieldOps 業務ポータル（架空デモ）",
        Subject = $"{quote.PartyName} 御中 見積書",
        Language = "ja-JP"
    };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(style => style.FontFamily(FontFamilyName).FontSize(10).FontColor(Colors.Black));

            page.Content().Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(16);

            column.Item().Element(ComposeTitle);
            column.Item().Element(ComposeParties);
            column.Item().Element(ComposeTotalBanner);
            column.Item().Element(ComposeSenderBlock);
            column.Item().Element(ComposeLineItemsTable);

            string? notes = quote.Notes;
            if (!string.IsNullOrWhiteSpace(notes))
            {
                column.Item().Element(notesContainer => ComposeNotes(notesContainer, notes));
            }
        });
    }

    private void ComposeTitle(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().AlignCenter().Text("見積書").FontSize(24).Bold();
            column.Item().AlignCenter().PaddingTop(2)
                .Text($"{quote.QuoteNumber}（第{quote.RevisionNumber}版）")
                .FontSize(10).FontColor(Colors.Grey.Darken2);
        });
    }

    private void ComposeParties(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem(3).Column(column =>
            {
                column.Spacing(3);
                column.Item().Text("御見積先").FontSize(9).FontColor(Colors.Grey.Darken1);
                column.Item().BorderBottom(1).BorderColor(Colors.Grey.Darken3).PaddingBottom(4)
                    .Text($"{quote.PartyName} 御中").FontSize(14).Bold();
                column.Item().PaddingTop(2).Text($"現場名: {quote.SiteName}").FontSize(10);
            });

            row.ConstantItem(24);

            row.RelativeItem(2).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(column =>
            {
                column.Spacing(4);
                ComposeMetaLine(column, "見積番号", quote.QuoteNumber);
                ComposeMetaLine(column, "版数", $"第{quote.RevisionNumber}版");
                ComposeMetaLine(column, "発行日", FormatDate(quote.IssuedOn));
                ComposeMetaLine(column, "有効期限", FormatDate(quote.ValidUntil));
            });
        });
    }

    private static void ComposeMetaLine(ColumnDescriptor column, string label, string value)
    {
        column.Item().Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(9).FontColor(Colors.Grey.Darken1);
            row.RelativeItem().AlignRight().Text(value).FontSize(10).SemiBold();
        });
    }

    private void ComposeTotalBanner(IContainer container)
    {
        container.Background(Colors.Grey.Lighten4).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(12)
            .Row(row =>
            {
                row.RelativeItem().AlignMiddle().Text("御見積金額（税込）").FontSize(12).SemiBold();
                row.RelativeItem().AlignRight().Text(FormatMoney(quote.TotalAmount)).FontSize(22).Bold();
            });
    }

    private void ComposeSenderBlock(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(2);
            column.Item().Text("差出人（見積発行元）").FontSize(9).FontColor(Colors.Grey.Darken1);
            column.Item().Text($"FieldOps 業務ポータル　{quote.BranchName}").FontSize(11).SemiBold();
            column.Item().Text($"担当: {quote.OwnerName}").FontSize(10);
            column.Item().PaddingTop(2)
                .Text("※本見積書は架空のデモデータです。実在する企業・登録番号・連絡先ではありません。")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private void ComposeLineItemsTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(36);
                columns.RelativeColumn(4f);
                columns.RelativeColumn(1f);
                columns.RelativeColumn(0.9f);
                columns.RelativeColumn(1.6f);
                columns.RelativeColumn(1.8f);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("番号");
                header.Cell().Element(HeaderCell).Text("品名");
                header.Cell().Element(HeaderCell).AlignRight().Text("数量");
                header.Cell().Element(HeaderCell).Text("単位");
                header.Cell().Element(HeaderCell).AlignRight().Text("単価");
                header.Cell().Element(HeaderCell).AlignRight().Text("金額");
            });

            int rowNumber = 0;
            foreach (QuoteLineItemView lineItem in quote.LineItems)
            {
                rowNumber++;
                bool isAlternateRow = rowNumber % 2 == 0;

                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).Text($"{rowNumber}");
                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).Text(lineItem.Description);
                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).AlignRight().Text(FormatQuantity(lineItem.Quantity));
                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).Text(lineItem.UnitName);
                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).AlignRight().Text(FormatMoney(lineItem.UnitPrice));
                table.Cell().Element(cell => BodyCell(cell, isAlternateRow)).AlignRight().Text(FormatMoney(lineItem.Amount));
            }

            table.Cell().ColumnSpan(5).Element(SummaryLabelCell).AlignRight().Text("小計");
            table.Cell().Element(SummaryValueCell).AlignRight().Text(FormatMoney(quote.Subtotal));

            table.Cell().ColumnSpan(5).Element(SummaryLabelCell).AlignRight().Text($"消費税（{FormatPercent(quote.TaxRatePercent)}）");
            table.Cell().Element(SummaryValueCell).AlignRight().Text(FormatMoney(quote.TaxAmount));

            table.Cell().ColumnSpan(5).Element(TotalLabelCell).AlignRight().Text("合計").Bold();
            table.Cell().Element(TotalValueCell).AlignRight().Text(FormatMoney(quote.TotalAmount)).Bold();
        });
    }

    private static void ComposeNotes(IContainer container, string notes)
    {
        container.Column(column =>
        {
            column.Spacing(2);
            column.Item().Text("備考").FontSize(9).FontColor(Colors.Grey.Darken1);
            column.Item().Background(Colors.Grey.Lighten5).Padding(8).Text(notes).FontSize(10);
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(2);
            column.Item().AlignCenter()
                .Text("これは架空データによるデモ出力です。実在する企業・取引を示すものではありません。")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
            column.Item().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Darken1));
                text.Span("ページ ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.Background(Colors.Grey.Darken3).Padding(6)
            .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(9).SemiBold());

    private static IContainer BodyCell(IContainer container, bool isAlternateRow) =>
        container.Background(isAlternateRow ? Colors.Grey.Lighten4 : Colors.White)
            .BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
            .Padding(6);

    private static IContainer SummaryLabelCell(IContainer container) =>
        container.PaddingVertical(4).PaddingHorizontal(6);

    private static IContainer SummaryValueCell(IContainer container) =>
        container.PaddingVertical(4).PaddingHorizontal(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten2);

    private static IContainer TotalLabelCell(IContainer container) =>
        container.PaddingVertical(6).PaddingHorizontal(6).BorderTop(1).BorderColor(Colors.Grey.Darken3);

    private static IContainer TotalValueCell(IContainer container) =>
        container.PaddingVertical(6).PaddingHorizontal(6).BorderTop(1).BorderColor(Colors.Grey.Darken3);

    private static string FormatMoney(decimal value) => value.ToString("C0", JapaneseCulture);

    private static string FormatQuantity(decimal value) => value.ToString("0.##", JapaneseCulture);

    private static string FormatPercent(decimal value) => value.ToString("0.##", JapaneseCulture) + "%";

    private static string FormatDate(DateTime? value) => value?.ToString("yyyy年M月d日", JapaneseCulture) ?? "未入力";
}