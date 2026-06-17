# Stendly SDK Examples

Complete Telegram bot examples demonstrating all Stendly SDK features in three languages.

## What This Bot Does

The bot is a simple product store that showcases every SDK capability:

| Command | Feature | SDK Method |
|---------|---------|------------|
| `/start` | Main menu with inline buttons | — |
| `/buy` | Create a payment intent for a product | `intents.create()` |
| `/stats` | View 30-day merchant analytics | `merchant.getStats()` |
| `/profile` | View merchant profile info | `merchant.getProfile()` |
| `/terminals` | List POS terminals | `terminals.list()` |

### Payment Flow

1. User sends `/buy` or taps "Buy a product"
2. Bot shows product menu (Coffee, Sandwich, Premium Meal)
3. User selects a product
4. Bot creates a payment intent via the Stendly API
5. Bot sends a payment link button
6. User opens the payment page and pays with USDC on Solana
7. **Built-in webhook server** receives payment event and sends notification to user
8. Bot confirms: "Payment received!" (via webhook, no polling)

## Prerequisites

- A Stendly API key (`st_live_...`) — get it from the dashboard
- A Telegram bot token — get it from [@BotFather](https://t.me/BotFather)
- A webhook secret (`whsec_...`) — configure in merchant dashboard
- For devnet testing: `STENDLY_ENVIRONMENT=devnet`

## Environment Variables

All examples use these environment variables:

```bash
STENDLY_API_KEY=st_live_your_key_here
STENDLY_ENVIRONMENT=devnet        # or "mainnet"
STENDLY_WEBHOOK_SECRET=whsec_...  # for webhook verification
TELEGRAM_BOT_TOKEN=123456:ABC-DEF...
PORT=9900                         # webhook server port (optional)
```

## Architecture

```
User ──Telegram──> Bot (with built-in webhook server on port 9900)
                      │
                      ├── Telegram polling (bot commands)
                      ├── Express/Flask/ASP.NET (webhook endpoint)
                      └── HTTP → Stendly API (create intents, etc.)

Stendly API ──Webhook──> Bot (port 9900) ──Telegram──> User
```

Payment notifications are delivered via webhooks — no polling needed.
The webhook server is built into the bot process — one process, shared state.

## Python Example

```bash
cd examples/python
pip install -r requirements.txt

# Set environment variables
export STENDLY_API_KEY="st_live_..."
export TELEGRAM_BOT_TOKEN="123456:..."

python bot.py
```

Requires: Python 3.9+, aiogram 3.x, Flask, stendly SDK.

## Node.js Example

```bash
cd examples/node
npm install

# Set environment variables
export STENDLY_API_KEY="st_live_..."
export TELEGRAM_BOT_TOKEN="123456:..."

npm start
```

Requires: Node.js 18+, stendly SDK, Express.

## .NET Example

```bash
cd examples/dotnet/

# Set environment variables
export STENDLY_API_KEY="st_live_..."
export TELEGRAM_BOT_TOKEN="123456:..."

dotnet run
```

Requires: .NET 10 SDK, Telegram.Bot package.

## SDK Features Demonstrated

### Client
- `invoice_url(intent_id)` / `invoiceUrl(intentId)` / `InvoiceUrl(intentId)` — Build public checkout URLs

### Intents
- `create(amount_cents, order_id)` — Create a payment intent
- `retrieve(intent_id)` — Get payment intent status

### Terminals
- `list()` — List all POS terminals

### Merchant
- `getProfile()` — Get merchant profile (includes `verification_status_label` / `verificationStatusLabel` / `VerificationStatusLabel`)
- `getStats()` — Get 30-day analytics with chart data

### Webhooks
- `constructEvent(payload, signature, secret)` / `ConstructEventAsync()` — Verify webhook signatures
- Handles: `payment_intent.succeeded`, `payment_intent.failed`, `payment_intent.expired`, `payment_intent.underpaid`, `payment_intent.cancelled`

## Notes

- All examples use the same logic, just different languages
- Payment links point to the checkout page (`/checkout?invoice={intentId}`) on `app.stendly.com` (mainnet) or `app-devnet.stendly.com` (devnet)
- Webhook servers run on port 9900 by default (built into the bot process)
- Configure your webhook URL in the merchant dashboard to point to `https://your-domain:9900/webhook`
- No data is stored locally — everything comes from the Stendly API
