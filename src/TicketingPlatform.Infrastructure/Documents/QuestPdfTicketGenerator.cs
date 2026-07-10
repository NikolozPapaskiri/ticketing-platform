using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TicketingPlatform.Application.Abstractions;

namespace TicketingPlatform.Infrastructure.Documents;

/// <summary>Renders the ticket PDF with QuestPDF (Community license - free below $1M revenue).</summary>
public sealed class QuestPdfTicketGenerator : ITicketDocumentGenerator
{
    static QuestPdfTicketGenerator() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Generate(TicketDocumentData data) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A5.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(t => t.FontSize(12));

                page.Header().Text("TICKET").Bold().FontSize(28);

                page.Content().PaddingVertical(12).Column(column =>
                {
                    column.Spacing(6);
                    column.Item().Text(data.EventName).Bold().FontSize(18);
                    if (data.VenueName is not null)
                        column.Item().Text(data.VenueName);
                    column.Item().Text($"Starts: {data.StartsAt:yyyy-MM-dd HH:mm} UTC");
                    column.Item().Text($"{data.TicketTypeName}  x{data.Quantity}");
                    column.Item().Text($"Paid: {data.Amount} {data.Currency}");
                    column.Item().Text($"Holder: {data.CustomerEmail}");
                    column.Item().Text($"Validation code: {data.ValidationCode}").Bold();
                });

                // The order id doubles as the entry reference a scanner would validate.
                page.Footer().Text($"Order {data.OrderId}").FontSize(9).Light();
            });
        }).GeneratePdf();
}
