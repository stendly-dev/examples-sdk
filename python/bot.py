"""
Stendly Telegram Bot — Python Example

A real Telegram bot that demonstrates all Stendly SDK features:
- Create payment intents and share payment links
- View merchant profile and stats
- Manage POS terminals
- Webhook-based payment notifications (no polling)

Usage:
    pip install -r requirements.txt
    python bot.py

Environment variables:
    STENDLY_API_KEY       — Your st_live_... API key
    STENDLY_ENVIRONMENT   — "mainnet" or "devnet" (default: devnet)
    TELEGRAM_BOT_TOKEN    — Your Telegram bot token from @BotFather
    PORT                  — Webhook server port (default: 9900)
"""

import asyncio
import logging
import os
import threading
import uuid

import requests
from flask import Flask, jsonify, request

from aiogram import Bot, Dispatcher, types
from aiogram.filters import Command
from aiogram.types import InlineKeyboardMarkup, InlineKeyboardButton

from stendly import Client, StendlyError, ValidationError
from stendly.exceptions import SignatureVerificationError

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

STENDLY_API_KEY = os.environ["STENDLY_API_KEY"]
STENDLY_ENVIRONMENT = os.getenv("STENDLY_ENVIRONMENT", "devnet")
TELEGRAM_BOT_TOKEN = os.environ["TELEGRAM_BOT_TOKEN"]
WEBHOOK_SECRET = os.environ.get("STENDLY_WEBHOOK_SECRET", "")
PORT = int(os.getenv("PORT", "9900"))

# Product catalog for the /buy menu
PRODUCTS = [
    {"name": "Coffee", "price_cents": 35, "emoji": "\u2615"},
    {"name": "Sandwich", "price_cents": 65, "emoji": "\U0001f96a"},
    {"name": "Premium Meal", "price_cents": 120, "emoji": "\U0001f37d\ufe0f"},
]

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Initialize clients
# ---------------------------------------------------------------------------

stendly = Client(
    api_key=STENDLY_API_KEY,
    environment=STENDLY_ENVIRONMENT,
)

bot = Bot(token=TELEGRAM_BOT_TOKEN)
dp = Dispatcher()

# In-memory store: invoice_id -> chat_id
pending_invoices: dict[str, int] = {}

TELEGRAM_API_URL = f"https://api.telegram.org/bot{TELEGRAM_BOT_TOKEN}/sendMessage"


def send_telegram_message(chat_id: int, text: str):
    try:
        requests.post(TELEGRAM_API_URL, json={"chat_id": chat_id, "text": text, "parse_mode": "HTML"}, timeout=10)
    except Exception as e:
        logger.error("Failed to send Telegram message: %s", e)

# ---------------------------------------------------------------------------
# Webhook server (runs in a separate thread)
# ---------------------------------------------------------------------------

flask_app = Flask(__name__)


@flask_app.route("/webhook", methods=["POST"])
def handle_webhook():
    raw_body = request.get_data()
    signature = request.headers.get("X-Stendly-Signature", "")

    try:
        event = stendly.webhooks.construct_event(
            payload=raw_body,
            signature_header=signature,
            webhook_secret=WEBHOOK_SECRET,
        )
    except SignatureVerificationError as e:
        logger.warning("Invalid webhook signature: %s", e)
        return jsonify({"error": "Invalid signature"}), 401

    logger.info("Received event: %s", event.event_type)

    chat_id = pending_invoices.get(str(event.data.payment_intent_id))

    if event.event_type == "payment_intent.succeeded":
        logger.info(
            "Payment succeeded: order=%s, amount=$%.2f",
            event.data.order_id,
            event.data.amount_cents / 100,
        )
        if chat_id:
            send_telegram_message(
                chat_id,
                f"✅ Payment Received!\n\n"
                f"Order: {event.data.order_id}\n"
                f"Amount: ${event.data.amount_cents / 100:.2f}\n"
                f"Transaction: {event.data.tx_signature or 'N/A'}",
            )

    elif event.event_type == "payment_intent.failed":
        logger.warning("Payment failed: order=%s", event.data.order_id)
        if chat_id:
            send_telegram_message(
                chat_id,
                f"❌ Payment Failed\n\n"
                f"Order: {event.data.order_id}\n"
                f"Please try again or contact support.",
            )

    elif event.event_type == "payment_intent.expired":
        logger.info("Payment expired: order=%s", event.data.order_id)
        if chat_id:
            send_telegram_message(
                chat_id,
                f"⏰ Payment Expired\n\n"
                f"Order: {event.data.order_id}\n"
                f"The invoice has expired. Please create a new one.",
            )

    elif event.event_type == "payment_intent.underpaid":
        logger.warning(
            "Payment underpaid: order=%s, expected=$%.2f, got=$%.2f",
            event.data.order_id,
            event.data.expected_amount_cents / 100,
            event.data.amount_cents / 100,
        )
        if chat_id:
            send_telegram_message(
                chat_id,
                f"⚠️ Payment Underpaid\n\n"
                f"Order: {event.data.order_id}\n"
                f"Expected: ${event.data.expected_amount_cents / 100:.2f}\n"
                f"Received: ${event.data.amount_cents / 100:.2f}\n"
                f"Please pay the remaining amount.",
            )

    elif event.event_type == "payment_intent.cancelled":
        logger.info("Payment cancelled: order=%s", event.data.order_id)
        if chat_id:
            send_telegram_message(
                chat_id,
                f"🚫 Payment Cancelled\n\n"
                f"Order: {event.data.order_id}",
            )

    return jsonify({"received": True}), 200


@flask_app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok"}), 200


def run_webhook_server():
    flask_app.run(host="0.0.0.0", port=PORT, debug=False, use_reloader=False)


# ---------------------------------------------------------------------------
# /start — Main menu
# ---------------------------------------------------------------------------


@dp.message(Command("start"))
async def cmd_start(message: types.Message):
    keyboard = InlineKeyboardMarkup(
        inline_keyboard=[
            [InlineKeyboardButton(text="\U0001f6d2 Buy a product", callback_data="menu_buy")],
            [InlineKeyboardButton(text="\U0001f4ca My Stats", callback_data="menu_stats")],
            [InlineKeyboardButton(text="\U0001f464 My Profile", callback_data="menu_profile")],
            [InlineKeyboardButton(text="\U0001f4bb Terminals", callback_data="menu_terminals")],
        ]
    )
    await message.answer(
        "Welcome to Stendly Demo Bot! \U0001f4b3\n\n"
        "I can create payment intents, check stats, manage terminals, and more.\n"
        "Choose an option below:",
        reply_markup=keyboard,
    )


# ---------------------------------------------------------------------------
# /buy — Product selection
# ---------------------------------------------------------------------------


@dp.message(Command("buy"))
async def cmd_buy(message: types.Message):
    await show_products(message)


async def show_products(message: types.Message):
    buttons = []
    for i, p in enumerate(PRODUCTS):
        buttons.append(
            [InlineKeyboardButton(
                text=f"{p['emoji']} {p['name']} — ${p['price_cents'] / 100:.2f}",
                callback_data=f"buy_{i}",
            )]
        )
    keyboard = InlineKeyboardMarkup(inline_keyboard=buttons)
    await message.answer("Select a product to purchase:", reply_markup=keyboard)


# ---------------------------------------------------------------------------
# Buy callback — create payment intent
# ---------------------------------------------------------------------------


@dp.callback_query(lambda c: c.data and c.data.startswith("buy_"))
async def on_buy(callback: types.CallbackQuery):
    product_index = int(callback.data.split("_")[1])
    product = PRODUCTS[product_index]

    await callback.message.answer(
        f"Creating payment for {product['emoji']} {product['name']} "
        f"(${product['price_cents'] / 100:.2f})..."
    )

    try:
        order_id = f"tg_{uuid.uuid4().hex[:12]}"

        intent = stendly.intents.create(
            amount_cents=product["price_cents"],
            order_id=order_id,
        )

        url = stendly.invoice_url(str(intent.id))
        pending_invoices[str(intent.id)] = callback.message.chat.id

        keyboard = InlineKeyboardMarkup(
            inline_keyboard=[
                [InlineKeyboardButton(
                    text="\U0001f517 Open Payment Page",
                    url=url,
                )],
            ]
        )

        await callback.message.answer(
            f"\U0001f4e6 Payment created!\n\n"
            f"Order: {order_id}\n"
            f"Amount: ${intent.expected_amount_cents / 100:.2f}\n"
            f"Status: {intent.status}\n"
            f"Expires: {intent.expires_at.strftime('%Y-%m-%d %H:%M UTC')}\n\n"
            f"Tap the button below to open the payment page.\n"
            f"You will receive a notification when payment is received.",
            reply_markup=keyboard,
        )

    except ValidationError as e:
        await callback.message.answer(f"\u274c Validation error: {e.message}")
    except StendlyError as e:
        await callback.message.answer(f"\u274c API error: {e.message} (status={e.status_code})")
        import traceback
        traceback.print_exc()


# ---------------------------------------------------------------------------
# /stats — Merchant analytics
# ---------------------------------------------------------------------------


@dp.message(Command("stats"))
async def cmd_stats(message: types.Message):
    try:
        stats = stendly.merchant.get_stats()

        chart_summary = "\n".join(
            f"  {d.date.strftime('%m/%d')}: ${d.volume_cents / 100:.2f} ({d.transactions} tx)"
            for d in stats.chart_data[-5:]
        )

        await message.answer(
            f"\U0001f4ca Merchant Stats (30 days)\n\n"
            f"Total volume: ${stats.total_volume_cents / 100:.2f}\n"
            f"Transactions: {stats.total_transactions}\n"
            f"Success rate: {stats.success_rate:.1f}%\n"
            f"Avg transaction: ${stats.average_transaction_cents / 100:.2f}\n\n"
            f"Last 5 days:\n{chart_summary}"
        )
    except StendlyError as e:
        await message.answer(f"\u274c Error: {e.message}")


# ---------------------------------------------------------------------------
# /profile — Merchant profile
# ---------------------------------------------------------------------------


@dp.message(Command("profile"))
async def cmd_profile(message: types.Message):
    try:
        profile = stendly.merchant.get_profile()

        payout_short = f"{profile.payout_address[:12]}...{profile.payout_address[-8:]}"

        await message.answer(
            f"\U0001f464 Merchant Profile\n\n"
            f"Name: {profile.name}\n"
            f"Payout: {payout_short}\n"
            f"Webhook: {profile.webhook_url or 'Not set'}\n"
            f"Status: {profile.verification_status_label}"
        )
    except StendlyError as e:
        await message.answer(f"\u274c Error: {e.message}")


# ---------------------------------------------------------------------------
# /terminals — List POS terminals
# ---------------------------------------------------------------------------


@dp.message(Command("terminals"))
async def cmd_terminals(message: types.Message):
    try:
        terminals = stendly.terminals.list()

        if not terminals:
            await message.answer("\U0001f4bb No terminals found.")
            return

        lines = []
        for t in terminals:
            status = "\U0001f7e2 Active" if t.is_active else "\U0001f534 Inactive"
            lines.append(f"{status} {t.name}")

        await message.answer(
            f"\U0001f4bb Terminals ({len(terminals)}):\n\n" + "\n".join(lines)
        )
    except StendlyError as e:
        await message.answer(f"\u274c Error: {e.message}")


# ---------------------------------------------------------------------------
# Callback router for menu buttons
# ---------------------------------------------------------------------------


@dp.callback_query(lambda c: c.data and c.data.startswith("menu_"))
async def on_menu(callback: types.CallbackQuery):
    action = callback.data.split("_")[1]

    if action == "buy":
        await callback.message.answer("Select a product:")
        await show_products(callback.message)
    elif action == "stats":
        try:
            stats = stendly.merchant.get_stats()
            chart_summary = "\n".join(
                f"  {d.date.strftime('%m/%d')}: ${d.volume_cents / 100:.2f} ({d.transactions} tx)"
                for d in stats.chart_data[-5:]
            )
            await callback.message.answer(
                f"\U0001f4ca Merchant Stats (30 days)\n\n"
                f"Total volume: ${stats.total_volume_cents / 100:.2f}\n"
                f"Transactions: {stats.total_transactions}\n"
                f"Success rate: {stats.success_rate:.1f}%\n"
                f"Avg transaction: ${stats.average_transaction_cents / 100:.2f}\n\n"
                f"Last 5 days:\n{chart_summary}"
            )
        except StendlyError as e:
            await callback.message.answer(f"\u274c Error: {e.message}")
    elif action == "profile":
        try:
            profile = stendly.merchant.get_profile()
            payout_short = f"{profile.payout_address[:12]}...{profile.payout_address[-8:]}"
            await callback.message.answer(
                f"\U0001f464 Merchant Profile\n\n"
                f"Name: {profile.name}\n"
                f"Payout: {payout_short}\n"
                f"Webhook: {profile.webhook_url or 'Not set'}\n"
                f"Status: {profile.verification_status_label}"
            )
        except StendlyError as e:
            await callback.message.answer(f"\u274c Error: {e.message}")
    elif action == "terminals":
        try:
            terminals = stendly.terminals.list()
            if not terminals:
                await callback.message.answer("\U0001f4bb No terminals found.")
            else:
                lines = []
                for t in terminals:
                    status = "\U0001f7e2 Active" if t.is_active else "\U0001f534 Inactive"
                    lines.append(f"{status} {t.name}")
                await callback.message.answer(
                    f"\U0001f4bb Terminals ({len(terminals)}):\n\n" + "\n".join(lines)
                )
        except StendlyError as e:
            await callback.message.answer(f"\u274c Error: {e.message}")

    await callback.answer()


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------


async def main():
    logger.info("Starting Stendly Telegram Bot...")
    logger.info("Environment: %s", STENDLY_ENVIRONMENT)
    logger.info("Webhook server starting on port %d", PORT)

    # Start webhook server in background thread
    webhook_thread = threading.Thread(target=run_webhook_server, daemon=True)
    webhook_thread.start()

    logger.info("Payment notifications are delivered via webhooks on port %d", PORT)
    await dp.start_polling(bot)


if __name__ == "__main__":
    asyncio.run(main())
