// Stendly Telegram Bot — .NET Example
//
// A real Telegram bot that demonstrates all Stendly SDK features:
// - Create payment intents and share payment links
// - View merchant profile and stats
// - Manage POS terminals
// - Webhook-based payment notifications (no polling)
//
// Usage:
//   dotnet run
//
// Environment variables:
//   STENDLY_API_KEY       — Your st_live_... API key
//   STENDLY_ENVIRONMENT   — "mainnet" or "devnet" (default: devnet)
//   TELEGRAM_BOT_TOKEN    — Your Telegram bot token from @BotFather
//   PORT                  — Webhook server port (default: 9900)

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Stendly;
using Stendly.Exceptions;
using Stendly.Models;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var stendlyApiKey = config["STENDLY_API_KEY"]
    ?? throw new InvalidOperationException("STENDLY_API_KEY is not set");
var stendlyEnv = config["STENDLY_ENVIRONMENT"] ?? "devnet";
var telegramToken = config["TELEGRAM_BOT_TOKEN"]
    ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set");
var stendlyWebhookSecret = config["STENDLY_WEBHOOK_SECRET"] ?? "";
var port = int.Parse(config["PORT"] ?? "9900");

// Product catalog
var products = new (string Name, int PriceCents, string Emoji)[]
{
    ("Coffee", 35, "\u2615"),
    ("Sandwich", 65, "\U0001f96a"),
    ("Premium Meal", 120, "\U0001f37d\ufe0f"),
};

// Initialize clients
var stendly = new StendlyClient(new HttpClient(), stendlyApiKey, environment: stendlyEnv);
var bot = new TelegramBotClient(telegramToken);

// In-memory store: invoiceId -> chatId
var pendingInvoices = new ConcurrentDictionary<Guid, long>();

// ---------------------------------------------------------------------------
// Webhook server (built into the bot via ASP.NET Minimal API)
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapPost("/webhook", async (HttpContext context) =>
{
    using var ms = new MemoryStream();
    await context.Request.Body.CopyToAsync(ms);
    var rawBodyBytes = ms.ToArray();

    var signature = context.Request.Headers["X-Stendly-Signature"].FirstOrDefault() ?? "";

    WebhookEvent evt;
    try
    {
        evt = await stendly.Webhooks.ConstructEventAsync(rawBodyBytes, signature, stendlyWebhookSecret);
    }
    catch (StendlySignatureVerificationException ex)
    {
        app.Logger.LogWarning("Invalid webhook signature: {Reason}", ex.Reason);
        return Results.Json(new { error = "Invalid signature" }, statusCode: 401);
    }

    app.Logger.LogInformation("Received event: {EventType}", evt.EventType);

    var chatId = pendingInvoices.GetValueOrDefault(evt.Data.PaymentIntentId);

    if (evt.EventType == "payment_intent.succeeded")
    {
        app.Logger.LogInformation(
            "Payment succeeded: order={OrderId}, amount=${Amount}",
            evt.Data.OrderId, evt.Data.AmountCents / 100m);

        if (chatId != 0)
        {
            await bot.SendMessage(chatId,
                $"✅ Payment Received!\n\n"
                + $"Order: {evt.Data.OrderId}\n"
                + $"Amount: ${evt.Data.AmountCents / 100m:F2}\n"
                + $"Transaction: {evt.Data.TxSignature ?? "N/A"}");
        }
    }
    else if (evt.EventType == "payment_intent.failed")
    {
        app.Logger.LogWarning("Payment failed: order={OrderId}", evt.Data.OrderId);

        if (chatId != 0)
        {
            await bot.SendMessage(chatId,
                $"❌ Payment Failed\n\n"
                + $"Order: {evt.Data.OrderId}\n"
                + $"Please try again or contact support.");
        }
    }
    else if (evt.EventType == "payment_intent.expired")
    {
        app.Logger.LogInformation("Payment expired: order={OrderId}", evt.Data.OrderId);

        if (chatId != 0)
        {
            await bot.SendMessage(chatId,
                $"⏰ Payment Expired\n\n"
                + $"Order: {evt.Data.OrderId}\n"
                + $"The invoice has expired. Please create a new one.");
        }
    }
    else if (evt.EventType == "payment_intent.underpaid")
    {
        app.Logger.LogWarning(
            "Payment underpaid: order={OrderId}, expected=${Expected}, got=${Got}",
            evt.Data.OrderId, evt.Data.ExpectedAmountCents / 100m, evt.Data.AmountCents / 100m);

        if (chatId != 0)
        {
            await bot.SendMessage(chatId,
                $"⚠️ Payment Underpaid\n\n"
                + $"Order: {evt.Data.OrderId}\n"
                + $"Expected: ${evt.Data.ExpectedAmountCents / 100m:F2}\n"
                + $"Received: ${evt.Data.AmountCents / 100m:F2}\n"
                + $"Please pay the remaining amount.");
        }
    }
    else if (evt.EventType == "payment_intent.cancelled")
    {
        app.Logger.LogInformation("Payment cancelled: order={OrderId}", evt.Data.OrderId);

        if (chatId != 0)
        {
            await bot.SendMessage(chatId,
                $"🚫 Payment Cancelled\n\n"
                + $"Order: {evt.Data.OrderId}");
        }
    }

    return Results.Json(new { received = true });
});

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

// ---------------------------------------------------------------------------
// Main menu keyboard
// ---------------------------------------------------------------------------

var mainKeyboard = new InlineKeyboardMarkup(new[]
{
    new[] { InlineKeyboardButton.WithCallbackData("\U0001f6d2 Buy a product", "menu_buy") },
    new[] { InlineKeyboardButton.WithCallbackData("\U0001f4ca My Stats", "menu_stats") },
    new[] { InlineKeyboardButton.WithCallbackData("\U0001f464 My Profile", "menu_profile") },
    new[] { InlineKeyboardButton.WithCallbackData("\U0001f4bb Terminals", "menu_terminals") },
});

// ---------------------------------------------------------------------------
// Message handler
// ---------------------------------------------------------------------------

async Task OnMessage(ITelegramBotClient botClient, Message message, CancellationToken ct)
{
    if (message.Text == null) return;

    switch (message.Text)
    {
        case "/start":
            await botClient.SendMessage(message.Chat.Id,
                "Welcome to Stendly Demo Bot! \U0001f4b3\n\n"
                + "I can create payment intents, check stats, manage terminals, and more.\n"
                + "Choose an option below:",
                replyMarkup: mainKeyboard, cancellationToken: ct);
            break;

        case "/buy":
            await ShowProducts(botClient, message.Chat.Id, ct);
            break;

        case "/stats":
            await ShowStats(botClient, message.Chat.Id, ct);
            break;

        case "/profile":
            await ShowProfile(botClient, message.Chat.Id, ct);
            break;

        case "/terminals":
            await ShowTerminals(botClient, message.Chat.Id, ct);
            break;
    }
}

async Task ShowProducts(ITelegramBotClient botClient, long chatId, CancellationToken ct)
{
    var buttons = products.Select((p, i) =>
        InlineKeyboardButton.WithCallbackData($"{p.Emoji} {p.Name} — ${p.PriceCents / 100m:F2}", $"buy_{i}")
    ).Select(b => new[] { b }).ToArray();

    await botClient.SendMessage(chatId, "Select a product to purchase:",
        replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
}

async Task ShowStats(ITelegramBotClient botClient, long chatId, CancellationToken ct)
{
    try
    {
        var stats = await stendly.Merchant.GetStatsAsync(cancellationToken: ct);
        var chart = string.Join("\n", stats.ChartData.TakeLast(5).Select(d =>
            $"  {d.Date:MM/dd}: ${d.VolumeCents / 100m:F2} ({d.Transactions} tx)"));

        await botClient.SendMessage(chatId,
            $"\U0001f4ca Merchant Stats (30 days)\n\n"
            + $"Total volume: ${stats.TotalVolumeCents / 100m:F2}\n"
            + $"Transactions: {stats.TotalTransactions}\n"
            + $"Success rate: {stats.SuccessRate:F1}%\n"
            + $"Avg transaction: ${(decimal)stats.AverageTransactionCents / 100m:F2}\n\n"
            + $"Last 5 days:\n{chart}",
            cancellationToken: ct);
    }
    catch (StendlyException ex)
    {
        await botClient.SendMessage(chatId, $"\u274c Error: {ex.Message}", cancellationToken: ct);
    }
}

async Task ShowProfile(ITelegramBotClient botClient, long chatId, CancellationToken ct)
{
    try
    {
        var profile = await stendly.Merchant.GetProfileAsync(cancellationToken: ct);
        var payout = profile.PayoutAddress;
        var shortAddr = $"{payout[..12]}...{payout[^8..]}";

        await botClient.SendMessage(chatId,
            $"\U0001f464 Merchant Profile\n\n"
            + $"Name: {profile.Name}\n"
            + $"Payout: {shortAddr}\n"
            + $"Webhook: {profile.WebhookUrl ?? "Not set"}\n"
            + $"Status: {profile.VerificationStatusLabel}",
            cancellationToken: ct);
    }
    catch (StendlyException ex)
    {
        await botClient.SendMessage(chatId, $"\u274c Error: {ex.Message}", cancellationToken: ct);
    }
}

async Task ShowTerminals(ITelegramBotClient botClient, long chatId, CancellationToken ct)
{
    try
    {
        var terminals = await stendly.Terminals.ListTerminalsAsync(cancellationToken: ct);
        if (terminals.Count == 0)
        {
            await botClient.SendMessage(chatId, "\U0001f4bb No terminals found.", cancellationToken: ct);
            return;
        }

        var lines = terminals.Select(t =>
            $"{(t.IsActive ? "\U0001f7e2 Active" : "\U0001f534 Inactive")} {t.Name}"
        );

        await botClient.SendMessage(chatId,
            $"\U0001f4bb Terminals ({terminals.Count}):\n\n{string.Join("\n", lines)}",
            cancellationToken: ct);
    }
    catch (StendlyException ex)
    {
        await botClient.SendMessage(chatId, $"\u274c Error: {ex.Message}", cancellationToken: ct);
    }
}

// ---------------------------------------------------------------------------
// Callback query handler
// ---------------------------------------------------------------------------

async Task OnCallbackQuery(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
{
    var data = query.Data;
    if (data == null) return;

    var chatId = query.Message!.Chat.Id;

    // --- Buy product ---
    if (data.StartsWith("buy_"))
    {
        var index = int.Parse(data["buy_".Length..]);
        var product = products[index];

        await botClient.SendMessage(chatId,
            $"Creating payment for {product.Emoji} {product.Name} (${product.PriceCents / 100m:F2})...",
            cancellationToken: ct);

        try
        {
            var orderId = $"tg_{Guid.NewGuid():N}"[..16];
            var intent = await stendly.Intents.CreateIntentAsync(product.PriceCents, orderId, cancellationToken: ct);

            pendingInvoices[intent.Id] = chatId;

            var url = stendly.InvoiceUrl(intent.Id.ToString());

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl("\U0001f517 Open Payment Page", url) },
            });

            await botClient.SendMessage(chatId,
                $"\U0001f4e6 Payment created!\n\n"
                + $"Order: {orderId}\n"
                + $"Amount: ${intent.ExpectedAmountCents / 100m:F2}\n"
                + $"Status: {intent.Status}\n"
                + $"Expires: {intent.ExpiresAt:yyyy-MM-dd HH:mm UTC}\n\n"
                + "Tap the button below to open the payment page.\n"
                + "You will receive a notification when payment is received.",
                replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (StendlyException ex)
        {
            await botClient.SendMessage(chatId, $"\u274c Error: {ex.Message}", cancellationToken: ct);
        }

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        return;
    }

    // --- Menu actions ---
    if (data.StartsWith("menu_"))
    {
        var action = data["menu_".Length..];

        switch (action)
        {
            case "buy":
                await botClient.SendMessage(chatId, "Select a product:", cancellationToken: ct);
                await ShowProducts(botClient, chatId, ct);
                break;
            case "stats":
                await ShowStats(botClient, chatId, ct);
                break;
            case "profile":
                await ShowProfile(botClient, chatId, ct);
                break;
            case "terminals":
                await ShowTerminals(botClient, chatId, ct);
                break;
        }

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        return;
    }
}

// ---------------------------------------------------------------------------
// Start
// ---------------------------------------------------------------------------

// Start webhook server
app.Logger.LogInformation("Starting Stendly Webhook Server on port {Port}", port);
_ = app.RunAsync($"http://0.0.0.0:{port}");

Console.WriteLine("Starting Stendly Telegram Bot (.NET)...");
Console.WriteLine($"Environment: {stendlyEnv}");
Console.WriteLine($"Payment notifications are delivered via webhooks on port {port}");

using var cts = new CancellationTokenSource();
var receiverOptions = new ReceiverOptions();

bot.StartReceiving(
    async (client, update, ct) =>
    {
        if (update.Type == UpdateType.Message)
            await OnMessage(client, update.Message!, ct);
        else if (update.Type == UpdateType.CallbackQuery)
            await OnCallbackQuery(client, update.CallbackQuery!, ct);
    },
    async (client, exception, source, ct) =>
    {
        Console.WriteLine($"Error: {exception.Message}");
    },
    receiverOptions,
    cts.Token);

Console.WriteLine("Bot is running. Press Ctrl+C to stop.");
Console.CancelKeyPress += (_, _) => { cts.Cancel(); Console.WriteLine("Bot stopped."); };

await Task.Delay(Timeout.Infinite, cts.Token);
