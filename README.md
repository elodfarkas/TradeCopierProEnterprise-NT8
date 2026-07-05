# TradeCopier Pro Enterprise Telegram Bot
## NinjaTrader 8 Multi-Account Trade Copier with Simple Telegram Alerts

TradeCopier Pro Enterprise Telegram Bot is a NinjaTrader 8 Add-On for multi-account trade copying, follower account management, risk checks, audit logging, preflight validation, and clean Telegram notifications.
<img width="3830" height="2110" alt="image" src="https://github.com/user-attachments/assets/fc1efcca-95d3-40f6-a93a-a4ac164f6f3a" />

This build focuses on one simple goal:

> One order = one clear Telegram alert.

The Telegram alert system is designed to avoid noisy lifecycle spam such as separate Submitted, Filled, PositionUpdate, and execution messages for the same order.

---

## Important Disclaimer

Trading futures, forex, CFDs, equities, or other financial instruments involves substantial risk. This software is provided for automation support, monitoring, and workflow assistance only.

Use at your own risk. The author is not responsible for trading losses, missed orders, duplicate orders, incorrect configuration, broker/platform issues, delayed Telegram delivery, internet outages, or different behavior between Playback, Simulation, and Live environments.

Always test in Playback or Simulation before using any live account.

---

## Main Features


[![TradeCopier Pro Enterprise Telegram/hqdefault.jpg](https://www.youtube.com/watch?v=0kig3umPFbA)

### Trade Copier Engine

- Master account and master instrument selection
- Multiple follower account configuration
- Quantity multiplier and contract conversion support
- Quantity rounding modes
- Follower enable / disable control
- Copy support for Market, Limit, Stop Market, and Stop Limit orders
- LIVE MODE and TEST RUN mode
- Preflight safety checks before enabling copy
- Kill Switch support
- Expected vs actual position tracking
- Out-of-sync detection
- CSV audit logging

### Safety Features

- Global trading enable / disable
- Global kill switch
- Preflight validation
- Optional follower-flat requirement before enable
- Optional master-flat requirement before enable
- Config dirty detection
- Blocking if config changed after preflight
- Max order quantity, daily contracts, and trades per day
- Follower-level risk limits
- Slippage controls
- Quote availability checks
- Runtime error counters
- Emergency flatten / cancel controls

---

## Telegram Trade Monitor

The Telegram Trade Monitor sends simple, human-readable alerts for manually placed or filled orders.

Main design goal:

```text
Do not spam Telegram.
Show only the useful trading signal.
```

### Recommended Default Behavior

The default Telegram preset is optimized for clean alerts:

- Telegram Trade Monitor: ON
- Monitor when Copy is OFF: ON
- Monitor master account only: ON
- Manual order alerts: ON
- Manual execution alerts: OFF
- Position updates: OFF
- Simple order alerts: ON
- Order placement only: ON
- One alert per Order ID: ON
- Suppress lifecycle spam: ON
- Show price level: ON
- Market orders use execution price: ON
- Suppress fill alert if placement alert already sent: ON

### Behavior by Order Type

| Order Type | Telegram Behavior |
|---|---|
| Limit order | One `Order Placed` alert with limit price |
| Stop Market order | One `Order Placed` alert with stop price |
| Stop Limit order | One `Order Placed` alert with stop and limit price |
| Market order | One `Market Filled` alert with actual fill price |
| Stop/Limit order later fills | No second Telegram alert by default |
| Position update | Suppressed by default |
| Execution lifecycle spam | Suppressed by default |

---

## Example Telegram Messages

### Limit Order

```text
Order Placed

NQ 12-25
BUY LIMIT x1
Limit: 24823.5
Account: Playback101

Copy: OFF
Monitor only
```

### Stop Market Order

```text
Order Placed

NQ 12-25
SELL STOP MARKET x1
Stop: 24797.5
Account: Playback101

Copy: OFF
Monitor only
```

### Stop Limit Order

```text
Order Placed

NQ 12-25
BUY STOP LIMIT x1
Stop: 24799.75
Limit: 24800.25
Account: Playback101

Copy: OFF
Monitor only
```

### Market Order

```text
Market Filled

NQ 12-25
BUY MARKET x1
Fill: 24814.25
Account: Playback101

Copy: OFF
Monitor only
```

---

## One Alert Per Order Logic

This version prevents duplicate Telegram alerts for the same order by building an internal simple order key:

```text
Account | Instrument | OrderId
```

When a simple order placement alert is sent, that order key is stored. If a later market fill or execution event is received for the same order and the placement alert already exists, the fill alert is suppressed.

This prevents cases like:

```text
Order Placed
SELL STOP MARKET x1
Stop: 24797.5
```

followed immediately by:

```text
Market Filled
SELL MARKET x1
Fill: 24797.25
```

By default, only the first useful alert is sent.

---

## Event Types

### `MANUAL_ORDER_PLACED_SIMPLE`

Used for simple placement alerts: Limit, Stop Market, and Stop Limit orders.

```text
MANUAL_ORDER_PLACED_SIMPLE
Order Placed
NQ 12-25
SELL LIMIT x1
Limit: 24827.5
```

### `MANUAL_MARKET_FILLED_SIMPLE`

Used for pure Market order fills where there is no meaningful placement price.

```text
MANUAL_MARKET_FILLED_SIMPLE
Market Filled
NQ 12-25
BUY MARKET x1
Fill: 24800.5
```

### `TELEGRAM_FILTERED_OUT`

Used for internal/debug logging when an alert is intentionally suppressed.

```text
TELEGRAM_FILTERED_OUT
Market fill suppressed because placement alert was already sent for this OrderId.
```

---

## Telegram Default Settings Button

The Telegram Alerts panel includes a default preset button:

```text
APPLY DEFAULT TELEGRAM SETTINGS
```

or:

```text
ALAPBEÁLLÍTÁS
```

This button applies the recommended simple Telegram configuration, enables minimal alerts, saves the settings, and writes an event log entry:

```text
TELEGRAM_DEFAULT_SETTINGS_APPLIED
Default Telegram simple order alerts applied
```

---

## Telegram Status Badge

| Badge | Meaning |
|---|---|
| `TG OFF` | Telegram alerts disabled |
| `TG NOT SET` | Telegram enabled but token or chat ID is missing |
| `TG ACTIVE` | Telegram enabled and configured |
| `TG QUEUED N` | Messages are queued |
| `TG ERROR` | Telegram send failure or last error exists |

Telegram is outbound-only and never executes trading commands.

---

## Installation

Copy the `.cs` file into your NinjaTrader 8 AddOns folder.

Recommended path:

```text
Documents\NinjaTrader 8\bin\Custom\AddOns\TradeCopierProEnterpriseV3.cs
```

Then:

1. Open NinjaTrader 8.
2. Go to `New > NinjaScript Editor`.
3. Compile NinjaScript.
4. Restart NinjaTrader if needed.
5. Open the Control Center menu.
6. Launch `TradeCopier Pro Enterprise`.

---

## Telegram Setup

To use Telegram alerts, you need:

- Telegram Bot Token
- Telegram Chat ID

In the Telegram Alerts panel:

1. Enable Telegram Alerts.
2. Enter Bot Token.
3. Enter Chat ID.
4. Click Save Config.
5. Click Send Test Message.
6. Apply Default Telegram Settings.
7. Confirm that `TG ACTIVE` appears.

---

## Recommended First-Time Setup

1. Start in Playback or Simulation.
2. Open TradeCopier Pro Enterprise.
3. Select master account.
4. Select master instrument.
5. Add follower accounts if using copy routing.
6. Keep mode in TEST RUN.
7. Configure Telegram.
8. Click `APPLY DEFAULT TELEGRAM SETTINGS`.
9. Send a test Telegram message.
10. Place test Market, Limit, Stop Market, and Stop Limit orders.
11. Confirm alert behavior.
12. Only then consider LIVE MODE.

---

## Testing Checklist

### Telegram Connection

- [ ] Bot token entered
- [ ] Chat ID entered
- [ ] Send Test Message works
- [ ] Telegram badge shows `TG ACTIVE`
- [ ] No last error displayed

### Market Order

Expected:

```text
Market Filled
Instrument
BUY/SELL MARKET xQuantity
Fill: actual price
```

- [ ] Only one Telegram alert
- [ ] Fill price shown
- [ ] No separate placement alert
- [ ] No position update spam

### Limit Order

Expected:

```text
Order Placed
Instrument
BUY/SELL LIMIT xQuantity
Limit: price
```

- [ ] Only one Telegram alert
- [ ] Limit price shown
- [ ] No second fill alert by default

### Stop Market Order

Expected:

```text
Order Placed
Instrument
BUY/SELL STOP MARKET xQuantity
Stop: price
```

- [ ] Only one Telegram alert
- [ ] Stop price shown
- [ ] Fill alert suppressed if placement alert already sent

### Stop Limit Order

Expected:

```text
Order Placed
Instrument
BUY/SELL STOP LIMIT xQuantity
Stop: price
Limit: price
```

- [ ] Only one Telegram alert
- [ ] Stop price shown
- [ ] Limit price shown
- [ ] No lifecycle spam

---

## Default Telegram Configuration

```text
TelegramEnableTradeMonitor = true
TelegramMonitorWhenCopyOff = true
TelegramMonitorMasterAccountOnly = true
TelegramMonitorAllConfiguredAccounts = false
TelegramMonitorAllAccounts = false

TelegramSendManualExecutions = false
TelegramSendManualOrders = true
TelegramSendPositionUpdates = false

TelegramSimpleOrderAlerts = true
TelegramOrderPlacementOnly = true
TelegramSuppressOrderLifecycleSpam = true
TelegramSendOnlyOneAlertPerOrderId = true
TelegramShowOrderPriceLevel = true

TelegramMarketOrdersUseExecutionPrice = true
TelegramSimpleMarketExecutionAlerts = true
TelegramSuppressFillIfPlacementAlertSent = true

TelegramIgnoreCopierGeneratedOrders = true
TelegramIgnoreDuplicateExecutions = true

TelegramCompactMode = true
TelegramIncludeAccountNames = true
TelegramIncludeFollowerDetails = false
TelegramIncludePrices = true
TelegramIncludeBatchId = false

TelegramSendLiveSubmits = true
TelegramSendDryRunSubmits = false
TelegramSendRiskBlocks = true
TelegramSendSlippageBlocks = true
TelegramSendFollowerErrors = true
TelegramSendStateChanges = false
TelegramSendPreflightResults = false
TelegramSendKillSwitchEvents = true

TelegramRateLimitMs = 500
TelegramMaxQueueSize = 250
```

---

## Configuration and Audit Files

Settings are saved under the NinjaTrader user data directory:

```text
TradeCopierProEnterpriseV3\TradeCopierProEnterpriseV3_Settings.xml
```

Default audit file name:

```text
TradeCopierProEnterpriseV3_Audit.csv
```

Audit events include settings saved, preflight result, state changes, Telegram queued messages, Telegram send failures, manual order alerts, manual market fill alerts, filtered/suppressed Telegram alerts, follower errors, slippage blocks, risk blocks, and emergency events.

---

## Common Troubleshooting

### Telegram says `TG NOT SET`

Bot Token or Chat ID is missing. Enter both values, save config, and send a test message.

### Telegram says `TG ERROR`

Possible causes include wrong bot token, wrong chat ID, bot not started by the user, connection issue, Telegram API issue, or firewall/security software blocking outbound requests.

### I receive no Telegram alerts

Check Telegram Alerts, Trade Monitor, monitored account, instrument filter, master account selection, Telegram badge status, Event Log entries, Chat ID, and whether the bot was started in Telegram.

### I receive too many Telegram alerts

Use:

```text
APPLY DEFAULT TELEGRAM SETTINGS
```

Also check:

```text
TelegramSendPositionUpdates = false
TelegramSendManualExecutions = false
TelegramSuppressOrderLifecycleSpam = true
TelegramSendOnlyOneAlertPerOrderId = true
TelegramSuppressFillIfPlacementAlertSent = true
```

### Stop Market sends placement and fill alerts

This should be suppressed by default. Check:

```text
TelegramSuppressFillIfPlacementAlertSent = true
TelegramSendOnlyOneAlertPerOrderId = true
TelegramSuppressOrderLifecycleSpam = true
```

### Market order does not show placement price

This is expected. Market orders do not have a pre-defined limit or stop price. The system waits for execution and sends the actual fill price.

---

## File Structure

Current single-file NinjaTrader Add-On:

```text
TradeCopierProEnterpriseV3.cs
```

Main internal modules:

- TradeCopierProEnterpriseV3
- TradeCopierV3ControlCenter
- TradeCopierV3Services
- TradeCopierV3Engine
- TcpV3Settings
- TelegramNotifierV3
- AuditLoggerV3
- AccountSubscriptionManager
- PreflightValidatorV3
- RiskManagerV3
- SlippageEngineV3
- OrderRouterV3
- PositionSynchronizerV3
- LatencyMonitorV3

---

## Development Notes

Important NinjaTrader note:

```text
Do NOT add:
using NinjaTrader.Gui.ControlCenter;
```

In this NinjaTrader 8 build, ControlCenter is a type, not a namespace.

---

## Security Notes

- Never share your Telegram Bot Token publicly.
- Do not commit live tokens to GitHub.
- Do not commit real account identifiers if this repository is public.
- Use `.gitignore` for local settings and backups.

Recommended `.gitignore` entries:

```gitignore
*.xml
*.bak
*.tmp
*_Settings.xml
*_Audit.csv
*.corrupt_*
```

---

## Version Notes

### Telegram One Alert Per Order Build

This version adds:

- Simple Telegram default preset
- MANUAL_ORDER_PLACED_SIMPLE
- MANUAL_MARKET_FILLED_SIMPLE
- Market order fill price alerts
- Stop / Limit placement price alerts
- TelegramSuppressFillIfPlacementAlertSent
- TelegramSendOnlyOneAlertPerOrderId
- Order ID based duplicate suppression
- Cleaner Telegram message format
- Telegram status badge improvements
- Safer default Telegram monitor behavior

---

## Final Warning

Before using LIVE MODE:

- Test in Playback.
- Test in Simulation.
- Confirm Telegram alerts.
- Confirm follower routing.
- Confirm risk limits.
- Confirm preflight passes.
- Confirm Kill Switch behavior.

LIVE MODE may submit real orders to real accounts.

Use carefully.
