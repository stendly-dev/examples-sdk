/**
 * Stendly Telegram Bot — Node.js Example
 *
 * A real Telegram bot that demonstrates all Stendly SDK features:
 * - Create payment intents and share payment links
 * - View merchant profile and stats
 * - Manage POS terminals
 * - Webhook-based payment notifications (no polling)
 *
 * Usage:
 *   npm install
 *   npm start
 *
 * Environment variables:
 *   STENDLY_API_KEY       — Your st_live_... API key
 *   STENDLY_ENVIRONMENT   — "mainnet" or "devnet" (default: devnet)
 *   TELEGRAM_BOT_TOKEN    — Your Telegram bot token from @BotFather
 *   PORT                  — Webhook server port (default: 9900)
 */

import express from "express";
import TelegramBot from "node-telegram-bot-api";
import { StendlyClient } from "@stendly/sdk";

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const STENDLY_API_KEY = process.env.STENDLY_API_KEY;
const STENDLY_ENVIRONMENT = process.env.STENDLY_ENVIRONMENT || "devnet";
const TELEGRAM_BOT_TOKEN = process.env.TELEGRAM_BOT_TOKEN;
const WEBHOOK_SECRET = process.env.STENDLY_WEBHOOK_SECRET || "";
const PORT = parseInt(process.env.PORT || "9900", 10);

if (!STENDLY_API_KEY) throw new Error("STENDLY_API_KEY is not set");
if (!TELEGRAM_BOT_TOKEN) throw new Error("TELEGRAM_BOT_TOKEN is not set");

// Product catalog for the /buy menu
const PRODUCTS = [
  { name: "Coffee", priceCents: 35, emoji: "☕" },
  { name: "Sandwich", priceCents: 65, emoji: "🥪" },
  { name: "Premium Meal", priceCents: 120, emoji: "🍽️" },
];

// ---------------------------------------------------------------------------
// Initialize clients
// ---------------------------------------------------------------------------

const stendly = new StendlyClient({
  apiKey: STENDLY_API_KEY,
  environment: STENDLY_ENVIRONMENT,
});

const bot = new TelegramBot(TELEGRAM_BOT_TOKEN, { polling: true });

// In-memory store: invoiceId -> chatId
const pendingInvoices = new Map();

// ---------------------------------------------------------------------------
// Webhook server (built into the bot)
// ---------------------------------------------------------------------------

const app = express();

app.post("/webhook", express.raw({ type: "application/json" }), (req, res) => {
  const rawBody = req.body;
  const signature = req.headers["x-stendly-signature"] || "";

  let event;
  try {
    event = stendly.webhooks.constructEvent(rawBody, signature, WEBHOOK_SECRET);
  } catch (err) {
    console.warn("Invalid webhook signature:", err.message);
    return res.status(401).json({ error: "Invalid signature" });
  }

  console.log("Received event:", event.event);

  const chatId = pendingInvoices.get(event.data.paymentIntentId);

  if (event.event === "payment_intent.succeeded") {
    console.log(
      `Payment succeeded: order=${event.data.orderId}, amount=$${(event.data.amountCents / 100).toFixed(2)}`
    );
    if (chatId != null) {
      bot.sendMessage(
        chatId,
        `✅ Payment Received!\n\n` +
          `Order: ${event.data.orderId}\n` +
          `Amount: $${(event.data.amountCents / 100).toFixed(2)}\n` +
          `Transaction: ${event.data.txSignature || "N/A"}`
      );
    }
  } else if (event.event === "payment_intent.failed") {
    console.warn(`Payment failed: order=${event.data.orderId}`);
    if (chatId != null) {
      bot.sendMessage(
        chatId,
        `❌ Payment Failed\n\n` +
          `Order: ${event.data.orderId}\n` +
          `Please try again or contact support.`
      );
    }
  } else if (event.event === "payment_intent.expired") {
    console.log(`Payment expired: order=${event.data.orderId}`);
    if (chatId != null) {
      bot.sendMessage(
        chatId,
        `⏰ Payment Expired\n\n` +
          `Order: ${event.data.orderId}\n` +
          `The invoice has expired. Please create a new one.`
      );
    }
  } else if (event.event === "payment_intent.underpaid") {
    console.warn(
      `Payment underpaid: order=${event.data.orderId}, expected=$${(event.data.expectedAmountCents / 100).toFixed(2)}, got=$${(event.data.amountCents / 100).toFixed(2)}`
    );
    if (chatId != null) {
      bot.sendMessage(
        chatId,
        `⚠️ Payment Underpaid\n\n` +
          `Order: ${event.data.orderId}\n` +
          `Expected: $${(event.data.expectedAmountCents / 100).toFixed(2)}\n` +
          `Received: $${(event.data.amountCents / 100).toFixed(2)}\n` +
          `Please pay the remaining amount.`
      );
    }
  } else if (event.event === "payment_intent.cancelled") {
    console.log(`Payment cancelled: order=${event.data.orderId}`);
    if (chatId != null) {
      bot.sendMessage(
        chatId,
        `🚫 Payment Cancelled\n\n` +
          `Order: ${event.data.orderId}`
      );
    }
  }

  res.json({ received: true });
});

app.get("/health", (_req, res) => {
  res.json({ status: "ok" });
});

// ---------------------------------------------------------------------------
// /start — Main menu
// ---------------------------------------------------------------------------

bot.onText(/\/start/, (msg) => {
  const chatId = msg.chat.id;
  bot.sendMessage(chatId, "Welcome to Stendly Demo Bot! 💳\n\n"
    + "I can create payment intents, check stats, manage terminals, and more.\n"
    + "Choose an option below:", {
    reply_markup: {
      inline_keyboard: [
        [{ text: "🛒 Buy a product", callback_data: "menu_buy" }],
        [{ text: "📊 My Stats", callback_data: "menu_stats" }],
        [{ text: "👤 My Profile", callback_data: "menu_profile" }],
        [{ text: "💻 Terminals", callback_data: "menu_terminals" }],
      ],
    },
  });
});

// ---------------------------------------------------------------------------
// /buy — Product selection
// ---------------------------------------------------------------------------

bot.onText(/\/buy/, (msg) => {
  showProducts(msg.chat.id);
});

function showProducts(chatId) {
  const buttons = PRODUCTS.map((p, i) => [
    { text: `${p.emoji} ${p.name} — $${(p.priceCents / 100).toFixed(2)}`, callback_data: `buy_${i}` },
  ]);
  bot.sendMessage(chatId, "Select a product to purchase:", {
    reply_markup: { inline_keyboard: buttons },
  });
}

// ---------------------------------------------------------------------------
// Buy callback — create payment intent
// ---------------------------------------------------------------------------

bot.on("callback_query", async (query) => {
  const data = query.data;
  const chatId = query.message.chat.id;

  // --- Buy product ---
  if (data.startsWith("buy_")) {
    const index = parseInt(data.split("_")[1], 10);
    const product = PRODUCTS[index];

    await bot.sendMessage(chatId,
      `Creating payment for ${product.emoji} ${product.name} ($${(product.priceCents / 100).toFixed(2)})...`
    );

    try {
      const orderId = `tg_${Math.random().toString(36).slice(2, 14)}`;
      const intent = await stendly.intents.create(product.priceCents, orderId);

      pendingInvoices.set(intent.id, chatId);

      const url = stendly.invoiceUrl(intent.id);

      const expiresAt = intent.expiresAt instanceof Date
        ? intent.expiresAt.toISOString().slice(0, 16).replace("T", " ") + " UTC"
        : intent.expiresAt;

      await bot.sendMessage(chatId,
        `📦 Payment created!\n\n`
        + `Order: ${orderId}\n`
        + `Amount: $${(intent.expectedAmountCents / 100).toFixed(2)}\n`
        + `Status: ${intent.status}\n`
        + `Expires: ${expiresAt}\n\n`
        + `Tap the button below to open the payment page.\n`
        + `You will receive a notification when payment is received.`, {
        reply_markup: {
          inline_keyboard: [
            [{ text: "🔗 Open Payment Page", url }],
          ],
        },
      });
    } catch (err) {
      await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
    }

    await bot.answerCallbackQuery(query.id);
    return;
  }

  // --- Menu actions ---
  if (data.startsWith("menu_")) {
    const action = data.split("_")[1];

    if (action === "buy") {
      await bot.sendMessage(chatId, "Select a product:");
      showProducts(chatId);
    } else if (action === "stats") {
      try {
        const stats = await stendly.merchant.getStats();
        const chart = stats.chartData.slice(-5).map((d) =>
          `  ${d.date.toISOString().slice(0, 10)}: $${(d.volumeCents / 100).toFixed(2)} (${d.transactions} tx)`
        ).join("\n");

        await bot.sendMessage(chatId,
          `📊 Merchant Stats (30 days)\n\n`
          + `Total volume: $${(stats.totalVolumeCents / 100).toFixed(2)}\n`
          + `Transactions: ${stats.totalTransactions}\n`
          + `Success rate: ${stats.successRate.toFixed(1)}%\n`
          + `Avg transaction: $${(stats.averageTransactionCents / 100).toFixed(2)}\n\n`
          + `Last 5 days:\n${chart}`
        );
      } catch (err) {
        await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
      }
    } else if (action === "profile") {
      try {
        const profile = await stendly.merchant.getProfile();
        const payout = profile.payoutAddress;
        const short = `${payout.slice(0, 12)}...${payout.slice(-8)}`;

        await bot.sendMessage(chatId,
          `👤 Merchant Profile\n\n`
          + `Name: ${profile.name}\n`
          + `Payout: ${short}\n`
          + `Webhook: ${profile.webhookUrl || "Not set"}\n`
          + `Status: ${profile.verificationStatusLabel}`
        );
      } catch (err) {
        await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
      }
    } else if (action === "terminals") {
      try {
        const terminals = await stendly.terminals.list();
        if (terminals.length === 0) {
          await bot.sendMessage(chatId, "💻 No terminals found.");
        } else {
          const lines = terminals.map((t) =>
            `${t.isActive ? "🟢 Active" : "🔴 Inactive"} ${t.name}`
          );
          await bot.sendMessage(chatId,
            `💻 Terminals (${terminals.length}):\n\n${lines.join("\n")}`
          );
        }
      } catch (err) {
        await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
      }
    }

    await bot.answerCallbackQuery(query.id);
    return;
  }
});

// ---------------------------------------------------------------------------
// /stats — Merchant analytics
// ---------------------------------------------------------------------------

bot.onText(/\/stats/, async (msg) => {
  const chatId = msg.chat.id;
  try {
    const stats = await stendly.merchant.getStats();
    const chart = stats.chartData.slice(-5).map((d) =>
      `  ${d.date.toISOString().slice(0, 10)}: $${(d.volumeCents / 100).toFixed(2)} (${d.transactions} tx)`
    ).join("\n");

    await bot.sendMessage(chatId,
      `📊 Merchant Stats (30 days)\n\n`
      + `Total volume: $${(stats.totalVolumeCents / 100).toFixed(2)}\n`
      + `Transactions: ${stats.totalTransactions}\n`
      + `Success rate: ${stats.successRate.toFixed(1)}%\n`
      + `Avg transaction: $${(stats.averageTransactionCents / 100).toFixed(2)}\n\n`
      + `Last 5 days:\n${chart}`
    );
  } catch (err) {
    await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
  }
});

// ---------------------------------------------------------------------------
// /profile — Merchant profile
// ---------------------------------------------------------------------------

bot.onText(/\/profile/, async (msg) => {
  const chatId = msg.chat.id;
  try {
    const profile = await stendly.merchant.getProfile();
    const payout = profile.payoutAddress;
    const short = `${payout.slice(0, 12)}...${payout.slice(-8)}`;

    await bot.sendMessage(chatId,
      `👤 Merchant Profile\n\n`
      + `Name: ${profile.name}\n`
      + `Payout: ${short}\n`
      + `Webhook: ${profile.webhookUrl || "Not set"}\n`
      + `Status: ${profile.verificationStatusLabel}`
    );
  } catch (err) {
    await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
  }
});

// ---------------------------------------------------------------------------
// /terminals — List POS terminals
// ---------------------------------------------------------------------------

bot.onText(/\/terminals/, async (msg) => {
  const chatId = msg.chat.id;
  try {
    const terminals = await stendly.terminals.list();
    if (terminals.length === 0) {
      await bot.sendMessage(chatId, "💻 No terminals found.");
    } else {
      const lines = terminals.map((t) =>
        `${t.isActive ? "🟢 Active" : "🔴 Inactive"} ${t.name}`
      );
      await bot.sendMessage(chatId,
        `💻 Terminals (${terminals.length}):\n\n${lines.join("\n")}`
      );
    }
  } catch (err) {
    await bot.sendMessage(chatId, `❌ Error: ${err.message}`);
  }
});

// ---------------------------------------------------------------------------
// Start
// ---------------------------------------------------------------------------

app.listen(PORT, "0.0.0.0", () => {
  console.log(`Stendly Webhook Server running on port ${PORT}`);
});

console.log(`Starting Stendly Telegram Bot (Node.js)...`);
console.log(`Environment: ${STENDLY_ENVIRONMENT}`);
console.log(`Payment notifications are delivered via webhooks on port ${PORT}`);
